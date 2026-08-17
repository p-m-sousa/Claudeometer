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

    /// <summary>
    /// A diagnostic raised while reading local Claude Code data. Locations are deliberately
    /// coarse ("session transcript line 42") so that project paths never reach the UI or logs.
    /// </summary>
    public sealed class UsageWarning
    {
        public UsageWarning(string code, string message, string location, WarningSeverity severity)
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            if (message == null) throw new ArgumentNullException(nameof(message));
            Code = code;
            Message = message;
            Location = location ?? string.Empty;
            Severity = severity;
        }

        public string Code { get; }

        public string Message { get; }

        public string Location { get; }

        public WarningSeverity Severity { get; }

        /// <summary>
        /// True only for warnings that indicate a scan may have observed a transiently
        /// incomplete filesystem snapshot and should be retried before publishing totals.
        /// </summary>
        public bool IsTransient
        {
            get
            {
                return Code == "transcript.read_failed"
                    || Code == "transcripts.enumeration_failed"
                    || Code == "transcripts.project_enumeration_failed"
                    || Code == "transcript.changed_during_read"
                    || Code == "transcript.partial_final_line";
            }
        }

        public override string ToString()
        {
            return Location.Length == 0
                ? Code + ": " + Message
                : Code + " at " + Location + ": " + Message;
        }
    }

    /// <summary>
    /// The four token categories Claude Code records for every model response.
    /// </summary>
    public sealed class TokenTotals
    {
        public static readonly TokenTotals Zero = new TokenTotals(0, 0, 0, 0);

        public TokenTotals(
            long inputTokens,
            long outputTokens,
            long cacheReadTokens,
            long cacheCreationTokens)
        {
            InputTokens = inputTokens;
            OutputTokens = outputTokens;
            CacheReadTokens = cacheReadTokens;
            CacheCreationTokens = cacheCreationTokens;
        }

        public long InputTokens { get; }

        public long OutputTokens { get; }

        public long CacheReadTokens { get; }

        public long CacheCreationTokens { get; }

        /// <summary>Input + output. Excludes cache traffic.</summary>
        public long InputOutputTokens
        {
            get { return Numbers.Add(InputTokens, OutputTokens); }
        }

        /// <summary>Every token Claude Code processed: input + output + cache read + cache creation.</summary>
        public long ProcessedTokens
        {
            get
            {
                return Numbers.Add(
                    Numbers.Add(InputTokens, OutputTokens),
                    Numbers.Add(CacheReadTokens, CacheCreationTokens));
            }
        }

        public bool IsEmpty
        {
            get { return ProcessedTokens == 0; }
        }

        public TokenTotals Add(TokenTotals other)
        {
            if (other == null) return this;
            return new TokenTotals(
                Numbers.Add(InputTokens, other.InputTokens),
                Numbers.Add(OutputTokens, other.OutputTokens),
                Numbers.Add(CacheReadTokens, other.CacheReadTokens),
                Numbers.Add(CacheCreationTokens, other.CacheCreationTokens));
        }

        /// <summary>
        /// Element-wise maximum. Daily totals only ever grow, so this safely merges a freshly
        /// scanned day with an archived copy of the same day.
        /// </summary>
        public TokenTotals Max(TokenTotals other)
        {
            if (other == null) return this;
            return new TokenTotals(
                Math.Max(InputTokens, other.InputTokens),
                Math.Max(OutputTokens, other.OutputTokens),
                Math.Max(CacheReadTokens, other.CacheReadTokens),
                Math.Max(CacheCreationTokens, other.CacheCreationTokens));
        }

        public long Select(TokenMetric metric)
        {
            return metric == TokenMetric.InputOutput ? InputOutputTokens : ProcessedTokens;
        }
    }

    /// <summary>Which token figure a daily threshold is measured against.</summary>
    public enum TokenMetric
    {
        /// <summary>Input + output + cache read + cache creation.</summary>
        Processed,

        /// <summary>Input + output only.</summary>
        InputOutput
    }

    /// <summary>One model's contribution to a single local calendar day.</summary>
    public sealed class ModelUsage
    {
        public ModelUsage(
            string modelId,
            TokenTotals tokens,
            long responseCount,
            long toolCallCount,
            long webSearchRequests)
        {
            if (modelId == null) throw new ArgumentNullException(nameof(modelId));
            ModelId = modelId;
            Tokens = tokens ?? TokenTotals.Zero;
            ResponseCount = responseCount;
            ToolCallCount = toolCallCount;
            WebSearchRequests = webSearchRequests;
        }

        public string ModelId { get; }

        public TokenTotals Tokens { get; }

        /// <summary>Distinct model responses, after collapsing Claude Code's per-block transcript lines.</summary>
        public long ResponseCount { get; }

        public long ToolCallCount { get; }

        public long WebSearchRequests { get; }

        public ModelUsage Add(ModelUsage other)
        {
            if (other == null) return this;
            return new ModelUsage(
                ModelId,
                Tokens.Add(other.Tokens),
                Numbers.Add(ResponseCount, other.ResponseCount),
                Numbers.Add(ToolCallCount, other.ToolCallCount),
                Numbers.Add(WebSearchRequests, other.WebSearchRequests));
        }

        public ModelUsage Max(ModelUsage other)
        {
            if (other == null) return this;
            return new ModelUsage(
                ModelId,
                Tokens.Max(other.Tokens),
                Math.Max(ResponseCount, other.ResponseCount),
                Math.Max(ToolCallCount, other.ToolCallCount),
                Math.Max(WebSearchRequests, other.WebSearchRequests));
        }
    }

    /// <summary>
    /// Everything known about one local calendar day. Token, response, and tool-call figures
    /// are attributable to a model; session and message counts are whole-day figures.
    /// </summary>
    public sealed class UsageDay
    {
        public UsageDay(
            string date,
            IDictionary<string, ModelUsage> models,
            long sessionCount,
            long messageCount)
        {
            if (date == null) throw new ArgumentNullException(nameof(date));
            Date = date;
            Models = Collections.ReadOnly(models ?? new Dictionary<string, ModelUsage>(StringComparer.Ordinal));
            SessionCount = sessionCount;
            MessageCount = messageCount;

            var tokens = TokenTotals.Zero;
            long responses = 0;
            long toolCalls = 0;
            long webSearches = 0;
            foreach (var model in Models.Values)
            {
                tokens = tokens.Add(model.Tokens);
                responses = Numbers.Add(responses, model.ResponseCount);
                toolCalls = Numbers.Add(toolCalls, model.ToolCallCount);
                webSearches = Numbers.Add(webSearches, model.WebSearchRequests);
            }

            Tokens = tokens;
            ResponseCount = responses;
            ToolCallCount = toolCalls;
            WebSearchRequests = webSearches;
        }

        /// <summary>Local calendar date as YYYY-MM-DD.</summary>
        public string Date { get; }

        public IReadOnlyDictionary<string, ModelUsage> Models { get; }

        /// <summary>Sessions whose first transcript entry falls on this date.</summary>
        public long SessionCount { get; }

        /// <summary>Transcript messages on this date, across every model.</summary>
        public long MessageCount { get; }

        public TokenTotals Tokens { get; }

        public long ResponseCount { get; }

        public long ToolCallCount { get; }

        public long WebSearchRequests { get; }

        public bool IsEmpty
        {
            get { return Tokens.IsEmpty && MessageCount == 0 && SessionCount == 0 && ToolCallCount == 0; }
        }
    }

    /// <summary>A complete local usage history, ordered oldest day first.</summary>
    public sealed class UsageHistory
    {
        public static readonly UsageHistory Empty = new UsageHistory(new UsageDay[0]);

        public UsageHistory(IEnumerable<UsageDay> days)
        {
            if (days == null) throw new ArgumentNullException(nameof(days));
            var ordered = days
                .Where(day => day != null)
                .OrderBy(day => day.Date, StringComparer.Ordinal)
                .ToList();
            Days = new ReadOnlyCollection<UsageDay>(ordered);

            var modelIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var day in ordered)
            {
                foreach (var modelId in day.Models.Keys)
                {
                    modelIds.Add(modelId);
                }
            }

            ModelIds = new ReadOnlyCollection<string>(modelIds.ToList());
            FirstDate = ordered.Count == 0 ? null : ordered[0].Date;
            LastDate = ordered.Count == 0 ? null : ordered[ordered.Count - 1].Date;
        }

        public IReadOnlyList<UsageDay> Days { get; }

        public IReadOnlyList<string> ModelIds { get; }

        public string FirstDate { get; }

        public string LastDate { get; }

        public UsageDay FindDay(string date)
        {
            foreach (var day in Days)
            {
                if (string.Equals(day.Date, date, StringComparison.Ordinal)) return day;
            }

            return null;
        }
    }

    /// <summary>Saturating arithmetic. Counters are never allowed to wrap or go negative.</summary>
    internal static class Numbers
    {
        internal static long Add(long left, long right)
        {
            if (right > 0 && left > long.MaxValue - right) return long.MaxValue;
            if (right < 0 && left < long.MinValue - right) return long.MinValue;
            return left + right;
        }
    }

    internal static class Collections
    {
        internal static IReadOnlyDictionary<string, TValue> ReadOnly<TValue>(IDictionary<string, TValue> source)
        {
            return new ReadOnlyDictionary<string, TValue>(
                new Dictionary<string, TValue>(source, StringComparer.Ordinal));
        }
    }
}
