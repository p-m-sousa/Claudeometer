using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    /// <summary>
    /// Per-user preferences, stored as a small key/value file under LOCALAPPDATA so the app never
    /// needs the registry or a machine-wide location. Nothing here requires elevation, and no
    /// usage values are written.
    /// </summary>
    internal sealed class AppSettings
    {
        private const char SourceSeparator = '|';

        private readonly string _path;
        private readonly Dictionary<string, string> _values =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal AppSettings()
            : this(Path.Combine(DataDirectory, "preferences.txt"))
        {
        }

        internal AppSettings(string path)
        {
            _path = path;
            Sources = new List<string>();
            Alerts = new AlertSettings();
            NotifiedAlert = new AlertState();
            AutoDetectSources = true;
            RefreshSeconds = 30;
            MinimizeToTray = true;
            Load();
        }

        internal static string DataDirectory
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ClaudeUsage");
            }
        }

        internal static string StorePath
        {
            get { return Path.Combine(DataDirectory, "usage-archive.json"); }
        }

        /// <summary>Folders the user pinned. Empty means "use whatever discovery finds".</summary>
        internal IList<string> Sources { get; private set; }

        /// <summary>When true, discovered locations are used in addition to any pinned folders.</summary>
        internal bool AutoDetectSources { get; set; }

        internal int RefreshSeconds { get; set; }

        internal bool MinimizeToTray { get; set; }

        internal AlertSettings Alerts { get; private set; }

        /// <summary>Highest alert level already shown, so restarting the app does not re-announce it.</summary>
        internal AlertState NotifiedAlert { get; private set; }

        internal string ExportFolder { get; set; }

        internal void ReplaceSources(IEnumerable<string> sources)
        {
            Sources = (sources ?? new string[0])
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        internal void ReplaceAlerts(AlertSettings settings)
        {
            Alerts = settings ?? new AlertSettings();
        }

        internal void RecordNotifiedAlert(string date, AlertLevel level)
        {
            NotifiedAlert = new AlertState(date, level);
        }

        internal void Save()
        {
            try
            {
                if (!Directory.Exists(DataDirectory)) Directory.CreateDirectory(DataDirectory);
                var builder = new StringBuilder();
                builder.AppendLine("# Claude Usage preferences. Delete this file to reset the app.");
                builder.AppendLine("schema=1");
                builder.AppendLine("autoDetect=" + (AutoDetectSources ? "1" : "0"));
                builder.AppendLine("sources=" + string.Join(SourceSeparator.ToString(), Sources.ToArray()));
                builder.AppendLine("refreshSeconds=" + RefreshSeconds.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("minimizeToTray=" + (MinimizeToTray ? "1" : "0"));
                builder.AppendLine("alertEnabled=" + (Alerts.Enabled ? "1" : "0"));
                builder.AppendLine("alertLimit=" + Alerts.DailyLimitTokens.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("alertWarnPercent=" + Alerts.WarnPercent.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("alertMetric=" + (Alerts.Metric == TokenMetric.InputOutput ? "io" : "processed"));
                builder.AppendLine("alertDate=" + (NotifiedAlert.Date ?? string.Empty));
                builder.AppendLine("alertLevel=" + ((int)NotifiedAlert.Level).ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("exportFolder=" + (ExportFolder ?? string.Empty));
                File.WriteAllText(_path, builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
                // Preferences are a convenience; a locked-down profile must not break the app.
            }
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    MigrateLegacySettings();
                    return;
                }

                foreach (var line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (line.Length == 0 || line[0] == '#') continue;
                    var separator = line.IndexOf('=');
                    if (separator <= 0) continue;
                    _values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }
            catch
            {
                return;
            }

            AutoDetectSources = ReadBool("autoDetect", true);
            ReplaceSources(ReadString("sources").Split(new[] { SourceSeparator }, StringSplitOptions.RemoveEmptyEntries));
            RefreshSeconds = (int)ReadLong("refreshSeconds", 30);
            MinimizeToTray = ReadBool("minimizeToTray", true);
            Alerts = new AlertSettings
            {
                Enabled = ReadBool("alertEnabled", false),
                DailyLimitTokens = ReadLong("alertLimit", 0),
                WarnPercent = (int)ReadLong("alertWarnPercent", 80),
                Metric = string.Equals(ReadString("alertMetric"), "io", StringComparison.OrdinalIgnoreCase)
                    ? TokenMetric.InputOutput
                    : TokenMetric.Processed
            };
            NotifiedAlert = new AlertState(
                ReadString("alertDate"),
                (AlertLevel)Math.Max(0, Math.Min(2, ReadLong("alertLevel", 0))));
            var exportFolder = ReadString("exportFolder");
            ExportFolder = string.IsNullOrWhiteSpace(exportFolder) ? null : exportFolder;
        }

        /// <summary>
        /// Version 0.1 stored the path of a <c>stats-cache.json</c> file plus a refresh interval
        /// in two sibling files. That file's folder is the Claude data directory, so an upgrade
        /// can carry the user's choice forward instead of starting from scratch.
        /// </summary>
        private void MigrateLegacySettings()
        {
            try
            {
                var legacySource = Path.Combine(DataDirectory, "settings.txt");
                if (File.Exists(legacySource))
                {
                    var cachePath = File.ReadAllText(legacySource, Encoding.UTF8).Trim();
                    if (cachePath.Length > 0)
                    {
                        var folder = Path.GetDirectoryName(cachePath);
                        if (!string.IsNullOrEmpty(folder) && ClaudeDataLocator.HasProjects(folder))
                        {
                            ReplaceSources(new[] { folder });
                        }
                    }
                }

                var legacyInterval = legacySource + ".interval";
                if (File.Exists(legacyInterval))
                {
                    int seconds;
                    if (int.TryParse(File.ReadAllText(legacyInterval, Encoding.ASCII).Trim(), out seconds)
                        && seconds >= 0)
                    {
                        RefreshSeconds = seconds;
                    }
                }
            }
            catch
            {
                // Ignored: discovery and the default interval cover a failed migration.
            }
        }

        private string ReadString(string key)
        {
            string value;
            return _values.TryGetValue(key, out value) ? value : string.Empty;
        }

        private bool ReadBool(string key, bool fallback)
        {
            var value = ReadString(key);
            if (value.Length == 0) return fallback;
            return value == "1"
                || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private long ReadLong(string key, long fallback)
        {
            long parsed;
            return long.TryParse(ReadString(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0
                ? parsed
                : fallback;
        }
    }
}
