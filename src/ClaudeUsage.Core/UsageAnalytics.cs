using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    /// <summary>An inclusive date range and an optional model subset.</summary>
    public sealed class UsageFilter
    {
        public UsageFilter()
            : this(null, null, null)
        {
        }

        public UsageFilter(string fromDate, string toDate, IEnumerable<string> modelIds)
        {
            FromDate = DateKey.ValidateBound(fromDate, nameof(fromDate));
            ToDate = DateKey.ValidateBound(toDate, nameof(toDate));
            if (FromDate != null && ToDate != null && string.CompareOrdinal(FromDate, ToDate) > 0)
            {
                throw new ArgumentException("The From date cannot be after the To date.", nameof(fromDate));
            }

            var models = modelIds == null
                ? new List<string>()
                : modelIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
            ModelIds = new ReadOnlyCollection<string>(models);
        }

        public string FromDate { get; }

        public string ToDate { get; }

        public IReadOnlyList<string> ModelIds { get; }

        public bool IncludesAllModels
        {
            get { return ModelIds.Count == 0; }
        }
    }

    public sealed class DailyUsage
    {
        internal DailyUsage(
            string date,
            TokenTotals tokens,
            long responseCount,
            long toolCallCount,
            long messageCount,
            long sessionCount)
        {
            Date = date;
            Tokens = tokens;
            ResponseCount = responseCount;
            ToolCallCount = toolCallCount;
            MessageCount = messageCount;
            SessionCount = sessionCount;
        }

        public string Date { get; }

        public TokenTotals Tokens { get; }

        public long ResponseCount { get; }

        public long ToolCallCount { get; }

        /// <summary>Whole-day message count, across every model.</summary>
        public long MessageCount { get; }

        /// <summary>Sessions that began on this date, across every model.</summary>
        public long SessionCount { get; }

        public bool IsEmpty
        {
            get { return Tokens.IsEmpty && ResponseCount == 0 && MessageCount == 0 && SessionCount == 0; }
        }
    }

    public sealed class ModelTotal
    {
        internal ModelTotal(
            string modelId,
            TokenTotals tokens,
            long responseCount,
            long toolCallCount,
            long webSearchRequests)
        {
            ModelId = modelId;
            Tokens = tokens;
            ResponseCount = responseCount;
            ToolCallCount = toolCallCount;
            WebSearchRequests = webSearchRequests;
        }

        public string ModelId { get; }

        public TokenTotals Tokens { get; }

        public long ResponseCount { get; }

        public long ToolCallCount { get; }

        public long WebSearchRequests { get; }
    }

    public sealed class UsageAnalytics
    {
        internal UsageAnalytics(
            IList<DailyUsage> days,
            IList<ModelTotal> models,
            TokenTotals tokens,
            long totalResponses,
            long totalToolCalls,
            long totalMessages,
            long totalSessions,
            IList<string> availableModels,
            IList<string> selectedModels,
            bool includesAllModels,
            string fromDate,
            string toDate)
        {
            IncludesAllModels = includesAllModels;
            Days = new ReadOnlyCollection<DailyUsage>(days.ToList());
            Models = new ReadOnlyCollection<ModelTotal>(models.ToList());
            Tokens = tokens;
            TotalResponses = totalResponses;
            TotalToolCalls = totalToolCalls;
            TotalMessages = totalMessages;
            TotalSessions = totalSessions;
            AvailableModels = new ReadOnlyCollection<string>(availableModels.ToList());
            SelectedModels = new ReadOnlyCollection<string>(selectedModels.ToList());
            FromDate = fromDate;
            ToDate = toDate;
            ActiveDays = Days.Count(day => !day.IsEmpty);
        }

        /// <summary>Days inside the range that recorded activity, oldest first.</summary>
        public IReadOnlyList<DailyUsage> Days { get; }

        /// <summary>Selected models with usage in the range, largest first.</summary>
        public IReadOnlyList<ModelTotal> Models { get; }

        public TokenTotals Tokens { get; }

        public long TotalResponses { get; }

        public long TotalToolCalls { get; }

        public long TotalMessages { get; }

        public long TotalSessions { get; }

        public int ActiveDays { get; }

        public IReadOnlyList<string> AvailableModels { get; }

        public IReadOnlyList<string> SelectedModels { get; }

        public string FromDate { get; }

        public string ToDate { get; }

        /// <summary>True when no model filter narrowed the result.</summary>
        public bool IncludesAllModels { get; }

        /// <summary>
        /// True when a model filter is active. Token, response, and tool-call figures respect it;
        /// message and session counts are whole-day values because a session spans models.
        /// </summary>
        public bool ActivityIsWholeDay
        {
            get { return !IncludesAllModels; }
        }

        public bool IsEmpty
        {
            get { return Tokens.IsEmpty && TotalMessages == 0 && TotalSessions == 0 && TotalToolCalls == 0; }
        }

        public DailyUsage PeakDay(TokenMetric metric)
        {
            DailyUsage peak = null;
            foreach (var day in Days)
            {
                if (peak == null || day.Tokens.Select(metric) > peak.Tokens.Select(metric)) peak = day;
            }

            return peak;
        }

        public long AveragePerActiveDay(TokenMetric metric)
        {
            return ActiveDays == 0 ? 0 : Tokens.Select(metric) / ActiveDays;
        }
    }

    public static class UsageAnalyticsCalculator
    {
        public static UsageAnalytics Calculate(UsageHistory history, UsageFilter filter)
        {
            if (history == null) throw new ArgumentNullException(nameof(history));
            filter = filter ?? new UsageFilter();

            var selected = filter.IncludesAllModels
                ? new HashSet<string>(history.ModelIds, StringComparer.Ordinal)
                : new HashSet<string>(filter.ModelIds, StringComparer.Ordinal);

            var days = new List<DailyUsage>();
            var modelTotals = new Dictionary<string, ModelBuilder>(StringComparer.Ordinal);
            var tokens = TokenTotals.Zero;
            long totalResponses = 0;
            long totalToolCalls = 0;
            long totalMessages = 0;
            long totalSessions = 0;

            foreach (var day in history.Days)
            {
                if (!DateKey.IsWithinInclusiveRange(day.Date, filter.FromDate, filter.ToDate)) continue;

                var dayTokens = TokenTotals.Zero;
                long dayResponses = 0;
                long dayToolCalls = 0;
                foreach (var pair in day.Models)
                {
                    if (!selected.Contains(pair.Key)) continue;
                    var model = pair.Value;
                    dayTokens = dayTokens.Add(model.Tokens);
                    dayResponses = Numbers.Add(dayResponses, model.ResponseCount);
                    dayToolCalls = Numbers.Add(dayToolCalls, model.ToolCallCount);

                    ModelBuilder builder;
                    if (!modelTotals.TryGetValue(pair.Key, out builder))
                    {
                        builder = new ModelBuilder(pair.Key);
                        modelTotals.Add(pair.Key, builder);
                    }

                    builder.Input = Numbers.Add(builder.Input, model.Tokens.InputTokens);
                    builder.Output = Numbers.Add(builder.Output, model.Tokens.OutputTokens);
                    builder.CacheRead = Numbers.Add(builder.CacheRead, model.Tokens.CacheReadTokens);
                    builder.CacheCreation = Numbers.Add(builder.CacheCreation, model.Tokens.CacheCreationTokens);
                    builder.Responses = Numbers.Add(builder.Responses, model.ResponseCount);
                    builder.ToolCalls = Numbers.Add(builder.ToolCalls, model.ToolCallCount);
                    builder.WebSearches = Numbers.Add(builder.WebSearches, model.WebSearchRequests);
                }

                tokens = tokens.Add(dayTokens);
                totalResponses = Numbers.Add(totalResponses, dayResponses);
                totalToolCalls = Numbers.Add(totalToolCalls, dayToolCalls);
                totalMessages = Numbers.Add(totalMessages, day.MessageCount);
                totalSessions = Numbers.Add(totalSessions, day.SessionCount);

                days.Add(new DailyUsage(
                    day.Date,
                    dayTokens,
                    dayResponses,
                    dayToolCalls,
                    day.MessageCount,
                    day.SessionCount));
            }

            var models = modelTotals.Values
                .Select(builder => builder.Build())
                .Where(model => !model.Tokens.IsEmpty || model.ResponseCount > 0)
                .Select(model => new ModelTotal(
                    model.ModelId,
                    model.Tokens,
                    model.ResponseCount,
                    model.ToolCallCount,
                    model.WebSearchRequests))
                .OrderByDescending(model => model.Tokens.ProcessedTokens)
                .ThenBy(model => model.ModelId, StringComparer.Ordinal)
                .ToList();

            return new UsageAnalytics(
                days,
                models,
                tokens,
                totalResponses,
                totalToolCalls,
                totalMessages,
                totalSessions,
                history.ModelIds.ToList(),
                selected.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                filter.IncludesAllModels,
                filter.FromDate,
                filter.ToDate);
        }
    }
}
