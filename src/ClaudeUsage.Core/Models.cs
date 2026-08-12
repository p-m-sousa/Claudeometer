using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClaudeUsage.Core
{
    public enum WarningSeverity
    {
        Information,
        Warning,
        Error
    }

    public sealed class ParseWarning
    {
        public ParseWarning(string code, string message, string path, WarningSeverity severity)
        {
            Code = code ?? throw new ArgumentNullException(nameof(code));
            Message = message ?? throw new ArgumentNullException(nameof(message));
            Path = path ?? string.Empty;
            Severity = severity;
        }

        public string Code { get; }

        public string Message { get; }

        public string Path { get; }

        public WarningSeverity Severity { get; }

        public override string ToString()
        {
            return string.IsNullOrEmpty(Path)
                ? Code + ": " + Message
                : Code + " at " + Path + ": " + Message;
        }

        /// <summary>
        /// True only for warnings that indicate a refresh may have observed a
        /// transiently incomplete filesystem snapshot and should be retried.
        /// </summary>
        public bool IsTransient => Code == "transcript.read_failed"
                                   || Code == "transcripts.enumeration_failed"
                                   || Code == "transcripts.project_enumeration_failed"
                                   || Code == "transcript.changed_during_read"
                                   || Code == "transcript.partial_final_line";
    }

    public sealed class DailyActivity
    {
        internal DailyActivity(string date, long messageCount, long sessionCount, long toolCallCount)
        {
            Date = date;
            MessageCount = messageCount;
            SessionCount = sessionCount;
            ToolCallCount = toolCallCount;
        }

        public string Date { get; }

        public long MessageCount { get; }

        public long SessionCount { get; }

        public long ToolCallCount { get; }
    }

    public sealed class DailyModelTokens
    {
        internal DailyModelTokens(string date, IDictionary<string, long> tokensByModel)
        {
            Date = date;
            TokensByModel = CollectionHelpers.ReadOnlyDictionary(tokensByModel, StringComparer.Ordinal);
        }

        public string Date { get; }

        /// <summary>
        /// Daily token totals by raw model identifier. The represented categories depend on
        /// StatsCacheDocument.DailyModelTokensVersion: version 5 includes all four token
        /// categories, while legacy caches contain input + output only.
        /// </summary>
        public IReadOnlyDictionary<string, long> TokensByModel { get; }
    }

    public sealed class ModelUsage
    {
        internal ModelUsage(
            string modelId,
            long inputTokens,
            long outputTokens,
            long cacheReadInputTokens,
            long cacheCreationInputTokens,
            long webSearchRequests,
            double? costUsd,
            long? contextWindow,
            long? maxOutputTokens)
        {
            ModelId = modelId;
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
            CacheReadInputTokens = cacheReadInputTokens;
            CacheCreationInputTokens = cacheCreationInputTokens;
            WebSearchRequests = webSearchRequests;
            CostUsd = costUsd;
            ContextWindow = contextWindow;
            MaxOutputTokens = maxOutputTokens;
        }

        public string ModelId { get; }

        public long InputTokens { get; }

        public long OutputTokens { get; }

        public long CacheReadInputTokens { get; }

        public long CacheCreationInputTokens { get; }

        public long WebSearchRequests { get; }

        /// <summary>
        /// Client-estimated all-time cost when present. A value of zero does not mean the
        /// user's subscription usage was free.
        /// </summary>
        public double? CostUsd { get; }

        public long? ContextWindow { get; }

        public long? MaxOutputTokens { get; }
    }

    public sealed class LongestSession
    {
        internal LongestSession(string sessionId, long durationMilliseconds, long messageCount, string timestamp)
        {
            SessionId = sessionId;
            DurationMilliseconds = durationMilliseconds;
            MessageCount = messageCount;
            Timestamp = timestamp;
        }

        public string SessionId { get; }

        public long DurationMilliseconds { get; }

        public long MessageCount { get; }

        public string Timestamp { get; }
    }

    public sealed class StatsCacheDocument
    {
        internal StatsCacheDocument(
            int version,
            int dailyModelTokensVersion,
            string lastComputedDate,
            IList<DailyActivity> dailyActivity,
            IList<DailyModelTokens> dailyModelTokens,
            IDictionary<string, ModelUsage> modelUsage,
            long totalSessions,
            long totalMessages,
            LongestSession longestSession,
            string firstSessionDate,
            IDictionary<int, long> hourCounts,
            long totalSpeculationTimeSavedMs,
            IDictionary<int, long> shotDistribution)
        {
            Version = version;
            DailyModelTokensVersion = dailyModelTokensVersion;
            LastComputedDate = lastComputedDate;
            DailyActivity = new ReadOnlyCollection<DailyActivity>(dailyActivity.ToList());
            DailyModelTokens = new ReadOnlyCollection<DailyModelTokens>(dailyModelTokens.ToList());
            ModelUsage = CollectionHelpers.ReadOnlyDictionary(modelUsage, StringComparer.Ordinal);
            TotalSessions = totalSessions;
            TotalMessages = totalMessages;
            LongestSession = longestSession;
            FirstSessionDate = firstSessionDate;
            HourCounts = CollectionHelpers.ReadOnlyDictionary(hourCounts);
            TotalSpeculationTimeSavedMs = totalSpeculationTimeSavedMs;
            ShotDistribution = CollectionHelpers.ReadOnlyDictionary(shotDistribution);

            var modelIds = new HashSet<string>(modelUsage.Keys, StringComparer.Ordinal);
            foreach (var day in dailyModelTokens)
            {
                foreach (var modelId in day.TokensByModel.Keys)
                {
                    modelIds.Add(modelId);
                }
            }

            ModelIds = new ReadOnlyCollection<string>(modelIds.OrderBy(value => value, StringComparer.Ordinal).ToList());
        }

        public int Version { get; }

        /// <summary>
        /// Version of Claude Code's daily token aggregation. Version 5 and later includes
        /// input, output, cache-read, and cache-creation tokens; older data is input + output.
        /// </summary>
        public int DailyModelTokensVersion { get; }

        public bool DailyTokensIncludeCache => DailyModelTokensVersion >= 5;

        public string LastComputedDate { get; }

        public IReadOnlyList<DailyActivity> DailyActivity { get; }

        public IReadOnlyList<DailyModelTokens> DailyModelTokens { get; }

        public IReadOnlyDictionary<string, ModelUsage> ModelUsage { get; }

        public long TotalSessions { get; }

        public long TotalMessages { get; }

        public LongestSession LongestSession { get; }

        public string FirstSessionDate { get; }

        public IReadOnlyDictionary<int, long> HourCounts { get; }

        public long TotalSpeculationTimeSavedMs { get; }

        public IReadOnlyDictionary<int, long> ShotDistribution { get; }

        /// <summary>
        /// Sorted union of model identifiers found in all-time model usage and every daily record.
        /// </summary>
        public IReadOnlyList<string> ModelIds { get; }
    }

    public sealed class StatsCacheParseResult
    {
        internal StatsCacheParseResult(StatsCacheDocument document, IList<ParseWarning> warnings, bool isUsable)
        {
            Document = document;
            Warnings = new ReadOnlyCollection<ParseWarning>(warnings.ToList());
            IsUsable = isUsable;
        }

        public StatsCacheDocument Document { get; }

        public IReadOnlyList<ParseWarning> Warnings { get; }

        /// <summary>
        /// False only when the input could not be parsed as a JSON object. A structurally sparse
        /// but valid object remains usable and carries warnings.
        /// </summary>
        public bool IsUsable { get; }
    }

    internal static class CollectionHelpers
    {
        internal static IReadOnlyDictionary<TKey, TValue> ReadOnlyDictionary<TKey, TValue>(
            IDictionary<TKey, TValue> source)
        {
            return new ReadOnlyDictionary<TKey, TValue>(new Dictionary<TKey, TValue>(source));
        }

        internal static IReadOnlyDictionary<string, TValue> ReadOnlyDictionary<TValue>(
            IDictionary<string, TValue> source,
            IEqualityComparer<string> comparer)
        {
            return new ReadOnlyDictionary<string, TValue>(new Dictionary<string, TValue>(source, comparer));
        }
    }
}
