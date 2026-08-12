using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    public sealed class TodayUsageScanProgress
    {
        internal TodayUsageScanProgress(int filesScanned, int totalFiles)
        {
            FilesScanned = filesScanned;
            TotalFiles = totalFiles;
        }

        public int FilesScanned { get; }

        public int TotalFiles { get; }
    }

    public sealed class TodayUsageSnapshot
    {
        internal TodayUsageSnapshot(
            string date,
            DailyActivity activity,
            DailyModelTokens modelTokens,
            IDictionary<string, ModelUsage> modelUsage,
            long totalSessions,
            long totalMessages,
            IDictionary<int, long> hourCounts,
            IList<ParseWarning> warnings,
            int filesScanned,
            int filesMatched)
        {
            Date = date;
            Activity = activity;
            ModelTokens = modelTokens;
            ModelUsage = CollectionHelpers.ReadOnlyDictionary(modelUsage, StringComparer.Ordinal);
            TotalSessions = totalSessions;
            TotalMessages = totalMessages;
            HourCounts = CollectionHelpers.ReadOnlyDictionary(hourCounts);
            Warnings = new ReadOnlyCollection<ParseWarning>(warnings.ToList());
            FilesScanned = filesScanned;
            FilesMatched = filesMatched;
        }

        public string Date { get; }

        public DailyActivity Activity { get; }

        public DailyModelTokens ModelTokens { get; }

        public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; }

        public long TotalSessions { get; }

        public long TotalMessages { get; }

        public IReadOnlyDictionary<int, long> HourCounts { get; }

        public IReadOnlyList<ParseWarning> Warnings { get; }

        public int FilesScanned { get; }

        public int FilesMatched { get; }
    }

    /// <summary>
    /// Reads only local transcript metadata needed for the current UTC day. Prompt and response
    /// text is never placed in the returned object. Source files are never modified.
    /// </summary>
    public static class TodayUsageCollector
    {
        private const int MaximumDetailedWarnings = 100;

        private static readonly HashSet<string> TranscriptMessageTypes = new HashSet<string>(
            new[] { "user", "assistant", "attachment", "system" },
            StringComparer.Ordinal);

        public static TodayUsageSnapshot CollectToday(
            string claudeConfigDirectory,
            CancellationToken cancellationToken,
            IProgress<TodayUsageScanProgress> progress = null)
        {
            return Collect(
                claudeConfigDirectory,
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                cancellationToken,
                progress);
        }

        public static TodayUsageSnapshot Collect(
            string claudeConfigDirectory,
            string utcDate,
            CancellationToken cancellationToken,
            IProgress<TodayUsageScanProgress> progress = null)
        {
            if (string.IsNullOrWhiteSpace(claudeConfigDirectory))
            {
                throw new ArgumentException("A Claude configuration directory is required.", nameof(claudeConfigDirectory));
            }

            if (!DateKey.IsValid(utcDate))
            {
                throw new ArgumentException("utcDate must be a valid YYYY-MM-DD date.", nameof(utcDate));
            }

            cancellationToken.ThrowIfCancellationRequested();
            var projectsDirectory = Path.Combine(claudeConfigDirectory, "projects");
            var warningAccumulator = new WarningAccumulator(MaximumDetailedWarnings);
            var accumulator = new TodayAccumulator(utcDate);
            var files = EnumerateTranscriptFiles(
                projectsDirectory,
                warningAccumulator,
                cancellationToken).ToArray();

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            var filesScanned = 0;
            var filesMatched = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (ScanFile(file, projectsDirectory, utcDate, accumulator, warningAccumulator, cancellationToken))
                    {
                        filesMatched++;
                    }
                }
                catch (Exception exception) when (IsRecoverableIoException(exception))
                {
                    warningAccumulator.Add(
                        "transcript.read_failed",
                        "A transcript changed or could not be read during this refresh.",
                        HasSubagentsPathSegment(file) ? "subagent transcript" : "main transcript",
                        WarningSeverity.Warning);
                }

                filesScanned++;
                progress?.Report(new TodayUsageScanProgress(filesScanned, files.Length));
            }

            return accumulator.ToSnapshot(warningAccumulator.ToList(), filesScanned, filesMatched);
        }

        private static IEnumerable<string> EnumerateTranscriptFiles(
            string projectsDirectory,
            WarningAccumulator warnings,
            CancellationToken cancellationToken)
        {
            var result = new List<string>();
            if (!Directory.Exists(projectsDirectory))
            {
                return result;
            }

            string[] projectDirectories;
            try
            {
                projectDirectories = Directory.GetDirectories(projectsDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception exception) when (IsRecoverableIoException(exception))
            {
                warnings.Add(
                    "transcripts.enumeration_failed",
                    "Claude project directories could not be enumerated during this refresh.",
                    "projects",
                    WarningSeverity.Warning);
                return result;
            }

            foreach (var projectDirectory in projectDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    // Match Claude Code itself: main transcripts live directly in each
                    // project directory. Do not recursively read tool-result sidecars.
                    result.AddRange(Directory.GetFiles(
                        projectDirectory,
                        "*.jsonl",
                        SearchOption.TopDirectoryOnly));

                    foreach (var sessionDirectory in Directory.GetDirectories(
                                 projectDirectory,
                                 "*",
                                 SearchOption.TopDirectoryOnly))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var subagentsDirectory = Path.Combine(sessionDirectory, "subagents");
                        if (!Directory.Exists(subagentsDirectory))
                        {
                            continue;
                        }

                        result.AddRange(Directory.GetFiles(
                            subagentsDirectory,
                            "agent-*.jsonl",
                            SearchOption.TopDirectoryOnly));
                    }
                }
                catch (Exception exception) when (IsRecoverableIoException(exception))
                {
                    warnings.Add(
                        "transcripts.project_enumeration_failed",
                        "One Claude project directory could not be fully enumerated during this refresh.",
                        "project directory",
                        WarningSeverity.Warning);
                }
            }

            return result;
        }

        private static bool ScanFile(
            string file,
            string projectsDirectory,
            string targetDate,
            TodayAccumulator accumulator,
            WarningAccumulator warnings,
            CancellationToken cancellationToken)
        {
            var isSubagent = HasSubagentsPathSegment(file);
            var warningLocation = isSubagent ? "subagent transcript" : "main transcript";
            try
            {
                var modifiedDate = File.GetLastWriteTimeUtc(file)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.CompareOrdinal(modifiedDate, targetDate) < 0)
                {
                    return false;
                }
            }
            catch (Exception exception) when (IsRecoverableIoException(exception))
            {
                // Fall through to the shared read path; it will surface an aggregate warning.
            }

            using (var stream = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024))
            {
                var lengthBeforeRead = stream.Length;
                var foundFirstTranscriptMessage = false;
                var matched = false;
                var lineNumber = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if ((lineNumber & 127) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    IDictionary<string, object> entry;
                    try
                    {
                        entry = MiniJsonParser.Parse(line) as IDictionary<string, object>;
                    }
                    catch (MiniJsonException)
                    {
                        // A writer may have exposed its final, not-yet-complete
                        // line. Treat that as an incomplete scan so the UI retries
                        // and retains its last known-good snapshot.
                        var atEndOfFile = reader.Peek() < 0;
                        var isPartialFinalLine = atEndOfFile &&
                                                 !FileEndsWithLineBreak(stream, lengthBeforeRead);
                        warnings.Add(
                            isPartialFinalLine
                                ? "transcript.partial_final_line"
                                : "transcript.line_invalid",
                            isPartialFinalLine
                                ? "A transcript was still being written during this refresh."
                                : "A malformed transcript line was skipped.",
                            warningLocation + " line " + lineNumber.ToString(CultureInfo.InvariantCulture),
                            isPartialFinalLine ? WarningSeverity.Warning : WarningSeverity.Information);
                        continue;
                    }

                    if (entry == null || !IsIncludedTranscriptMessage(entry, isSubagent))
                    {
                        continue;
                    }

                    DateTimeOffset timestamp;
                    if (!TryReadTimestamp(entry, out timestamp))
                    {
                        warnings.Add(
                            "transcript.timestamp_invalid",
                            "A transcript message with no valid timestamp was skipped.",
                            warningLocation + " line " + lineNumber.ToString(CultureInfo.InvariantCulture),
                            WarningSeverity.Information);
                        continue;
                    }

                    if (!foundFirstTranscriptMessage)
                    {
                        var sessionDate = timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                        foundFirstTranscriptMessage = true;
                        accumulator.BeginTranscript(
                            isSubagent,
                            timestamp,
                            string.Equals(sessionDate, targetDate, StringComparison.Ordinal));
                    }

                    var entryDate = timestamp.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    if (!string.Equals(entryDate, targetDate, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    matched = true;
                    accumulator.AddMessage(entry, isSubagent, warnings, warningLocation, lineNumber);
                }

                if (stream.Length != lengthBeforeRead)
                {
                    warnings.Add(
                        "transcript.changed_during_read",
                        "A transcript changed while it was being read during this refresh.",
                        warningLocation,
                        WarningSeverity.Warning);
                }

                return matched;
            }
        }

        private static bool FileEndsWithLineBreak(FileStream stream, long originalLength)
        {
            if (originalLength <= 0)
            {
                return true;
            }

            var readPosition = stream.Position;
            try
            {
                stream.Seek(originalLength - 1, SeekOrigin.Begin);
                var lastByte = stream.ReadByte();
                return lastByte == '\n' || lastByte == '\r';
            }
            finally
            {
                stream.Seek(readPosition, SeekOrigin.Begin);
            }
        }

        private static bool IsIncludedTranscriptMessage(IDictionary<string, object> entry, bool isSubagent)
        {
            object typeValue;
            var type = entry.TryGetValue("type", out typeValue) ? typeValue as string : null;
            if (type == null || !TranscriptMessageTypes.Contains(type))
            {
                return false;
            }

            if (isSubagent)
            {
                return true;
            }

            object sidechainValue;
            return !entry.TryGetValue("isSidechain", out sidechainValue)
                || !(sidechainValue is bool)
                || !(bool)sidechainValue;
        }

        private static bool TryReadTimestamp(IDictionary<string, object> entry, out DateTimeOffset timestamp)
        {
            object value;
            var raw = entry.TryGetValue("timestamp", out value) ? value as string : null;
            return raw != null
                && DateTimeOffset.TryParse(
                    raw,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out timestamp);
        }

        private static bool HasSubagentsPathSegment(string file)
        {
            var normalized = file.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var segment = Path.DirectorySeparatorChar + "subagents" + Path.DirectorySeparatorChar;
            return normalized.IndexOf(segment, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsRecoverableIoException(Exception exception)
        {
            return exception is IOException
                || exception is UnauthorizedAccessException
                || exception is DirectoryNotFoundException
                || exception is PathTooLongException
                || exception is NotSupportedException;
        }

        private sealed class TodayAccumulator
        {
            private readonly string _date;
            private readonly Dictionary<string, MutableModelUsage> _modelUsage =
                new Dictionary<string, MutableModelUsage>(StringComparer.Ordinal);
            private readonly Dictionary<int, long> _hourCounts = new Dictionary<int, long>();

            private long _messages;
            private long _sessions;
            private long _toolCalls;
            private bool _hasDailyActivity;
            private readonly List<PendingSubagentToolCall> _pendingSubagentToolCalls =
                new List<PendingSubagentToolCall>();

            internal TodayAccumulator(string date)
            {
                _date = date;
            }

            internal void BeginTranscript(
                bool isSubagent,
                DateTimeOffset firstTimestamp,
                bool sessionStartsOnTargetDate)
            {
                if (isSubagent || !sessionStartsOnTargetDate)
                {
                    return;
                }

                _sessions = SaturatingAdd(_sessions, 1);
                var localHour = firstTimestamp.ToLocalTime().Hour;
                long existing;
                _hourCounts.TryGetValue(localHour, out existing);
                _hourCounts[localHour] = SaturatingAdd(existing, 1);
            }

            internal void AddMessage(
                IDictionary<string, object> entry,
                bool isSubagent,
                WarningAccumulator warnings,
                string file,
                int lineNumber)
            {
                if (!isSubagent)
                {
                    _hasDailyActivity = true;
                    _messages = SaturatingAdd(_messages, 1);
                    if (_pendingSubagentToolCalls.Count > 0)
                    {
                        foreach (var pending in _pendingSubagentToolCalls)
                        {
                            _toolCalls = SaturatingAdd(_toolCalls, pending.Count);
                        }
                        _pendingSubagentToolCalls.Clear();
                    }
                }

                object typeValue;
                var type = entry.TryGetValue("type", out typeValue) ? typeValue as string : null;
                if (!string.Equals(type, "assistant", StringComparison.Ordinal))
                {
                    return;
                }

                object messageValue;
                var message = entry.TryGetValue("message", out messageValue)
                    ? messageValue as IDictionary<string, object>
                    : null;
                if (message == null)
                {
                    return;
                }

                if (!isSubagent)
                {
                    CountTools(message);
                }
                else
                {
                    var count = CountToolBlocks(message);
                    if (_hasDailyActivity)
                    {
                        _toolCalls = SaturatingAdd(_toolCalls, count);
                    }
                    else if (count > 0)
                    {
                        _pendingSubagentToolCalls.Add(new PendingSubagentToolCall(count));
                    }
                }
                object usageValue;
                var usage = message.TryGetValue("usage", out usageValue)
                    ? usageValue as IDictionary<string, object>
                    : null;
                if (usage == null)
                {
                    return;
                }

                object modelValue;
                var model = message.TryGetValue("model", out modelValue) ? modelValue as string : null;
                if (string.IsNullOrWhiteSpace(model))
                {
                    model = "unknown";
                }

                // Claude's synthetic internal model does not belong in usage statistics.
                if (string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(model, "synthetic", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                MutableModelUsage modelAccumulator;
                if (!_modelUsage.TryGetValue(model, out modelAccumulator))
                {
                    modelAccumulator = new MutableModelUsage();
                    _modelUsage.Add(model, modelAccumulator);
                }

                var path = file + ":" + lineNumber.ToString(CultureInfo.InvariantCulture);
                modelAccumulator.InputTokens = SaturatingAdd(
                    modelAccumulator.InputTokens,
                    ReadUsageCounter(usage, "input_tokens", path, warnings));
                modelAccumulator.OutputTokens = SaturatingAdd(
                    modelAccumulator.OutputTokens,
                    ReadUsageCounter(usage, "output_tokens", path, warnings));
                modelAccumulator.CacheReadInputTokens = SaturatingAdd(
                    modelAccumulator.CacheReadInputTokens,
                    ReadUsageCounter(usage, "cache_read_input_tokens", path, warnings));
                modelAccumulator.CacheCreationInputTokens = SaturatingAdd(
                    modelAccumulator.CacheCreationInputTokens,
                    ReadUsageCounter(usage, "cache_creation_input_tokens", path, warnings));

            }

            internal TodayUsageSnapshot ToSnapshot(
                IList<ParseWarning> warnings,
                int filesScanned,
                int filesMatched)
            {
                var models = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
                var dailyTokens = new Dictionary<string, long>(StringComparer.Ordinal);
                foreach (var pair in _modelUsage)
                {
                    models[pair.Key] = new ModelUsage(
                        pair.Key,
                        pair.Value.InputTokens,
                        pair.Value.OutputTokens,
                        pair.Value.CacheReadInputTokens,
                        pair.Value.CacheCreationInputTokens,
                        pair.Value.WebSearchRequests,
                        null,
                        null,
                        null);
                    dailyTokens[pair.Key] = SaturatingAdd(
                        SaturatingAdd(pair.Value.InputTokens, pair.Value.OutputTokens),
                        SaturatingAdd(
                            pair.Value.CacheReadInputTokens,
                            pair.Value.CacheCreationInputTokens));
                }

                return new TodayUsageSnapshot(
                    _date,
                    new DailyActivity(_date, _messages, _sessions, _toolCalls),
                    new DailyModelTokens(_date, dailyTokens),
                    models,
                    _sessions,
                    _messages,
                    _hourCounts,
                    warnings,
                    filesScanned,
                    filesMatched);
            }

            private void CountTools(IDictionary<string, object> message)
            {
                _toolCalls = SaturatingAdd(_toolCalls, CountToolBlocks(message));
            }

            private static long CountToolBlocks(IDictionary<string, object> message)
            {
                object contentValue;
                var content = message.TryGetValue("content", out contentValue) ? contentValue as IList<object> : null;
                if (content == null)
                {
                    return 0;
                }

                long count = 0;
                foreach (var value in content)
                {
                    var block = value as IDictionary<string, object>;
                    object blockTypeValue;
                    var blockType = block != null && block.TryGetValue("type", out blockTypeValue)
                        ? blockTypeValue as string
                        : null;
                    if (string.Equals(blockType, "tool_use", StringComparison.Ordinal))
                    {
                        count = SaturatingAdd(count, 1);
                    }
                }

                return count;
            }

            private static long ReadUsageCounter(
                IDictionary<string, object> source,
                string property,
                string path,
                WarningAccumulator warnings)
            {
                object value;
                if (!source.TryGetValue(property, out value) || value == null)
                {
                    return 0;
                }

                var number = value as JsonNumber;
                var raw = number == null ? value as string : number.Raw;
                long parsed;
                if (raw == null
                    || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                    || parsed < 0)
                {
                    warnings.Add(
                        "transcript.usage_invalid",
                        property + " is not a non-negative 64-bit integer and was treated as zero.",
                        path,
                        WarningSeverity.Information);
                    return 0;
                }

                return parsed;
            }
        }

        private sealed class PendingSubagentToolCall
        {
            internal PendingSubagentToolCall(long count)
            {
                Count = count;
            }

            internal long Count { get; }
        }

        private sealed class MutableModelUsage
        {
            internal long InputTokens { get; set; }

            internal long OutputTokens { get; set; }

            internal long CacheReadInputTokens { get; set; }

            internal long CacheCreationInputTokens { get; set; }

            internal long WebSearchRequests { get; set; }
        }

        private sealed class WarningAccumulator
        {
            private readonly int _maximum;
            private readonly List<ParseWarning> _warnings = new List<ParseWarning>();
            private int _suppressed;

            internal WarningAccumulator(int maximum)
            {
                _maximum = maximum;
            }

            internal void Add(string code, string message, string path, WarningSeverity severity)
            {
                if (_warnings.Count < _maximum)
                {
                    _warnings.Add(new ParseWarning(code, message, path, severity));
                }
                else
                {
                    _suppressed++;
                }
            }

            internal IList<ParseWarning> ToList()
            {
                var result = _warnings.ToList();
                if (_suppressed > 0)
                {
                    result.Add(new ParseWarning(
                        "transcript.warnings_suppressed",
                        _suppressed.ToString(CultureInfo.InvariantCulture) + " additional scan warnings were suppressed.",
                        string.Empty,
                        WarningSeverity.Information));
                }

                return result;
            }
        }

        private static long SaturatingAdd(long left, long right)
        {
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }
    }

    public static class StatsCacheOverlay
    {
        /// <summary>
        /// Replaces the matching daily row with a live snapshot. When the cache already contains
        /// that date, aggregate all-time fields are left unchanged because their per-day category
        /// breakdown cannot be subtracted safely. This avoids double-counting while making all
        /// date-filtered token and activity analytics exact.
        /// </summary>
        public static StatsCacheDocument ApplyToday(StatsCacheDocument history, TodayUsageSnapshot today)
        {
            if (history == null)
            {
                throw new ArgumentNullException(nameof(history));
            }

            if (today == null)
            {
                throw new ArgumentNullException(nameof(today));
            }

            var hadDailyActivity = history.DailyActivity.Any(value => value.Date == today.Date);
            var hadDailyTokens = history.DailyModelTokens.Any(value => value.Date == today.Date);
            var hadToday = hadDailyActivity || hadDailyTokens;
            var dailyActivity = history.DailyActivity
                .Where(value => value.Date != today.Date)
                .Concat(new[] { today.Activity })
                .OrderBy(value => value.Date, StringComparer.Ordinal)
                .ToList();
            var todayDailyTokens = history.DailyTokensIncludeCache
                ? today.ModelTokens
                : new DailyModelTokens(
                    today.Date,
                    today.ModelUsage.ToDictionary(
                        pair => pair.Key,
                        pair => SaturatingAdd(pair.Value.InputTokens, pair.Value.OutputTokens),
                        StringComparer.Ordinal));
            var dailyTokens = history.DailyModelTokens
                .Where(value => value.Date != today.Date)
                .Concat(new[] { todayDailyTokens })
                .OrderBy(value => value.Date, StringComparer.Ordinal)
                .ToList();
            var models = history.ModelUsage.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var hourCounts = history.HourCounts.ToDictionary(pair => pair.Key, pair => pair.Value);
            var totalSessions = history.TotalSessions;
            var totalMessages = history.TotalMessages;

            if (!hadToday)
            {
                foreach (var pair in today.ModelUsage)
                {
                    ModelUsage existing;
                    if (!models.TryGetValue(pair.Key, out existing))
                    {
                        models[pair.Key] = pair.Value;
                        continue;
                    }

                    models[pair.Key] = new ModelUsage(
                        pair.Key,
                        SaturatingAdd(existing.InputTokens, pair.Value.InputTokens),
                        SaturatingAdd(existing.OutputTokens, pair.Value.OutputTokens),
                        SaturatingAdd(existing.CacheReadInputTokens, pair.Value.CacheReadInputTokens),
                        SaturatingAdd(existing.CacheCreationInputTokens, pair.Value.CacheCreationInputTokens),
                        SaturatingAdd(existing.WebSearchRequests, pair.Value.WebSearchRequests),
                        existing.CostUsd,
                        Maximum(existing.ContextWindow, pair.Value.ContextWindow),
                        Maximum(existing.MaxOutputTokens, pair.Value.MaxOutputTokens));
                }

                totalSessions = SaturatingAdd(totalSessions, today.TotalSessions);
                totalMessages = SaturatingAdd(totalMessages, today.TotalMessages);
                foreach (var pair in today.HourCounts)
                {
                    long existing;
                    hourCounts.TryGetValue(pair.Key, out existing);
                    hourCounts[pair.Key] = SaturatingAdd(existing, pair.Value);
                }
            }

            return new StatsCacheDocument(
                history.Version,
                history.DailyModelTokensVersion,
                history.LastComputedDate,
                dailyActivity,
                dailyTokens,
                models,
                totalSessions,
                totalMessages,
                history.LongestSession,
                history.FirstSessionDate,
                hourCounts,
                history.TotalSpeculationTimeSavedMs,
                history.ShotDistribution.ToDictionary(pair => pair.Key, pair => pair.Value));
        }

        private static long SaturatingAdd(long left, long right)
        {
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }

        private static long? Maximum(long? left, long? right)
        {
            if (!left.HasValue)
            {
                return right;
            }

            if (!right.HasValue)
            {
                return left;
            }

            return Math.Max(left.Value, right.Value);
        }
    }
}
