using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    internal sealed class StatsLoadResult
    {
        public StatsLoadResult(string path, StatsCacheParseResult parsed, DateTime readAtUtc)
        {
            Path = path;
            Parsed = parsed;
            ReadAtUtc = readAtUtc;
        }

        public string Path { get; private set; }
        public StatsCacheParseResult Parsed { get; private set; }
        public DateTime ReadAtUtc { get; private set; }
    }

    internal static class StatsFileLoader
    {
        public static StatsLoadResult LoadWithRetry(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new FileNotFoundException("No stats cache path is configured.");

            Exception lastError = null;
            var delays = new[] { 0, 80, 180, 350, 700 };
            foreach (var delay in delays)
            {
                if (delay > 0) Thread.Sleep(delay);
                try
                {
                    string json;
                    using (var stream = new FileStream(
                        path,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    {
                        json = reader.ReadToEnd();
                    }

                    if (string.IsNullOrWhiteSpace(json))
                        throw new InvalidDataException("The stats cache is empty.");

                    var parsed = StatsCacheParser.Parse(json);
                    if (!parsed.IsUsable)
                        throw new InvalidDataException("The stats cache does not contain usable Claude Code statistics.");

                    return new StatsLoadResult(path, parsed, DateTime.UtcNow);
                }
                catch (Exception error)
                {
                    lastError = error;
                }
            }

            throw new InvalidDataException(
                "The stats cache could not be read. Claude Code may be updating it, or the JSON may be incomplete.",
                lastError);
        }

        public static string ResolveDefaultPath(string explicitPath, string savedPath)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return Normalize(explicitPath);
            if (!string.IsNullOrWhiteSpace(savedPath)) return Normalize(savedPath);

            var overrideDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(overrideDirectory))
                return Normalize(System.IO.Path.Combine(overrideDirectory, "stats-cache.json"));

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Normalize(System.IO.Path.Combine(profile, ".claude", "stats-cache.json"));
        }

        private static string Normalize(string path)
        {
            return System.IO.Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
        }
    }
}
