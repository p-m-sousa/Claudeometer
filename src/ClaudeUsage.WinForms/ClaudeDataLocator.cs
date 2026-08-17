using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Win32;

namespace ClaudeUsage.WinForms
{
    internal sealed class ClaudeDataLocation
    {
        internal ClaudeDataLocation(string path, string source, bool hasTranscripts)
        {
            Path = path;
            Source = source;
            HasTranscripts = hasTranscripts;
        }

        internal string Path { get; }

        /// <summary>Why this folder was offered, shown to the user so the list is explainable.</summary>
        internal string Source { get; }

        /// <summary>True when the folder contains a Claude Code <c>projects</c> directory.</summary>
        internal bool HasTranscripts { get; }

        public override string ToString()
        {
            return Path;
        }
    }

    /// <summary>
    /// Finds the folders Claude Code keeps its data in. Everything checked here is inside the
    /// current user's own profile or an explicitly configured location, so discovery never needs
    /// administrator rights.
    /// </summary>
    internal static class ClaudeDataLocator
    {
        private const string ProjectsFolderName = "projects";

        /// <summary>
        /// Probes the locations Claude Code is known to use, in priority order, and returns those
        /// that exist. Only fast local checks are performed; WSL is offered separately because
        /// reaching into a distribution can start it.
        /// </summary>
        internal static IList<ClaudeDataLocation> Discover()
        {
            var found = new List<ClaudeDataLocation>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var directory in SplitConfigDirectories(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")))
            {
                Consider(directory, "CLAUDE_CONFIG_DIR", found, seen);
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Consider(Combine(profile, ".claude"), "User profile", found, seen);
            Consider(Combine(profile, ".config", "claude"), "User profile", found, seen);

            var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (!string.IsNullOrWhiteSpace(xdg)) Consider(Combine(xdg, "claude"), "XDG_CONFIG_HOME", found, seen);

            // Domain-joined machines often redirect the home directory somewhere other than
            // USERPROFILE, and shells such as Git Bash set HOME instead.
            var home = Environment.GetEnvironmentVariable("HOME");
            if (!string.IsNullOrWhiteSpace(home)) Consider(Combine(home, ".claude"), "HOME", found, seen);

            var homeDrive = Environment.GetEnvironmentVariable("HOMEDRIVE");
            var homePath = Environment.GetEnvironmentVariable("HOMEPATH");
            if (!string.IsNullOrWhiteSpace(homeDrive) && !string.IsNullOrWhiteSpace(homePath))
            {
                Consider(Combine(homeDrive + homePath, ".claude"), "HOMEDRIVE/HOMEPATH", found, seen);
            }

            return found;
        }

        /// <summary>
        /// Names of installed WSL distributions, read from the current user's registry. Reading
        /// the names does not start a distribution.
        /// </summary>
        internal static IList<string> FindWslDistributions()
        {
            var names = new List<string>();
            try
            {
                using (var lxss = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Lxss",
                    false))
                {
                    if (lxss == null) return names;
                    foreach (var subKeyName in lxss.GetSubKeyNames())
                    {
                        using (var distribution = lxss.OpenSubKey(subKeyName, false))
                        {
                            var name = distribution == null
                                ? null
                                : distribution.GetValue("DistributionName") as string;
                            if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name)) names.Add(name);
                        }
                    }
                }
            }
            catch
            {
                // A locked-down or missing key simply means no WSL suggestions.
            }

            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        /// <summary>
        /// Looks for Claude Code data inside WSL distributions. This can start a stopped
        /// distribution, so it runs only when the user asks and gives up after
        /// <paramref name="timeout"/>.
        /// </summary>
        internal static IList<ClaudeDataLocation> SearchWsl(TimeSpan timeout)
        {
            var results = new List<ClaudeDataLocation>();
            var distributions = FindWslDistributions();
            if (distributions.Count == 0) return results;

            var worker = new Thread(() =>
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var distribution in distributions)
                {
                    foreach (var prefix in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
                    {
                        var root = prefix + distribution;
                        TryAddWslCandidate(Combine(root, "root", ".claude"), distribution, results, seen);
                        var homes = Combine(root, "home");
                        string[] users;
                        try
                        {
                            users = Directory.Exists(homes)
                                ? Directory.GetDirectories(homes)
                                : new string[0];
                        }
                        catch
                        {
                            users = new string[0];
                        }

                        foreach (var user in users)
                        {
                            TryAddWslCandidate(Combine(user, ".claude"), distribution, results, seen);
                        }

                        if (results.Count > 0) break;
                    }
                }
            });

            worker.IsBackground = true;
            worker.Start();
            worker.Join(timeout);
            lock (results)
            {
                return results.ToList();
            }
        }

        /// <summary>
        /// Accepts a folder the user picked, correcting the two most likely near-misses: choosing
        /// the <c>projects</c> folder itself, or the folder that contains <c>.claude</c>.
        /// </summary>
        internal static string NormalizeChosenFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder)) return null;
            string full;
            try
            {
                full = Path.GetFullPath(folder.Trim().Trim('"'));
            }
            catch
            {
                return null;
            }

            if (HasProjects(full)) return full;

            var leaf = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.Equals(leaf, ProjectsFolderName, StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) return parent;
            }

            var nested = Combine(full, ".claude");
            if (HasProjects(nested)) return nested;

            return full;
        }

        internal static bool HasProjects(string directory)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(directory)
                    && Directory.Exists(Path.Combine(directory, ProjectsFolderName));
            }
            catch
            {
                return false;
            }
        }

        internal static int CountTranscripts(string directory)
        {
            try
            {
                var projects = Path.Combine(directory, ProjectsFolderName);
                if (!Directory.Exists(projects)) return 0;
                var count = 0;
                foreach (var project in Directory.GetDirectories(projects))
                {
                    count += Directory.GetFiles(project, "*.jsonl", SearchOption.TopDirectoryOnly).Length;
                }

                return count;
            }
            catch
            {
                return 0;
            }
        }

        private static void TryAddWslCandidate(
            string candidate,
            string distribution,
            IList<ClaudeDataLocation> results,
            ISet<string> seen)
        {
            if (!HasProjects(candidate)) return;
            lock (results)
            {
                if (seen.Add(candidate))
                {
                    results.Add(new ClaudeDataLocation(candidate, "WSL: " + distribution, true));
                }
            }
        }

        private static void Consider(
            string directory,
            string source,
            IList<ClaudeDataLocation> found,
            ISet<string> seen)
        {
            if (string.IsNullOrWhiteSpace(directory)) return;
            string full;
            try
            {
                full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
            }
            catch
            {
                return;
            }

            if (!seen.Add(full)) return;
            if (!HasProjects(full)) return;
            found.Add(new ClaudeDataLocation(full, source, true));
        }

        /// <summary>
        /// CLAUDE_CONFIG_DIR may name more than one folder. Semicolons and commas separate them;
        /// colons cannot, because Windows paths use them for drive letters.
        /// </summary>
        private static IEnumerable<string> SplitConfigDirectories(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return new string[0];
            return value
                .Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim().Trim('"'))
                .Where(part => part.Length > 0);
        }

        private static string Combine(params string[] parts)
        {
            try
            {
                var result = parts[0];
                for (var index = 1; index < parts.Length; index++)
                {
                    result = Path.Combine(result, parts[index]);
                }

                return result;
            }
            catch
            {
                return null;
            }
        }
    }
}
