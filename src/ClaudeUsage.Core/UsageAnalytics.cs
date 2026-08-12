using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
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
                throw new ArgumentException("fromDate cannot be after toDate.", nameof(fromDate));
            }

            var normalizedModels = modelIds == null
                ? new List<string>()
                : modelIds
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList();
            ModelIds = new ReadOnlyCollection<string>(normalizedModels);
        }

        public string FromDate { get; }

        public string ToDate { get; }

        public IReadOnlyList<string> ModelIds { get; }

        public bool IncludesAllModels => ModelIds.Count == 0;
    }

    public sealed class DailyUsage
    {
        internal DailyUsage(
            string date,
            long tokens,
            long messageCount,
            long sessionCount,
            long toolCallCount)
        {
            Date = date;
            Tokens = tokens;
            MessageCount = messageCount;
            SessionCount = sessionCount;
            ToolCallCount = toolCallCount;
        }

        public string Date { get; }

        public long Tokens { get; }

        public long MessageCount { get; }

        public long SessionCount { get; }

        public long ToolCallCount { get; }
    }

    public sealed class ModelTokenTotal
    {
        internal ModelTokenTotal(string modelId, long tokens)
        {
            ModelId = modelId;
            Tokens = tokens;
        }

        public string ModelId { get; }

        public long Tokens { get; }
    }

    public sealed class UsageAnalytics
    {
        internal UsageAnalytics(
            IList<DailyUsage> dailyUsage,
            IList<ModelTokenTotal> modelTotals,
            long totalTokens,
            long totalMessages,
            long totalSessions,
            long totalToolCalls,
            int activeDays,
            IList<string> availableModels,
            IList<string> selectedModels,
            string fromDate,
            string toDate)
        {
            DailyUsage = new ReadOnlyCollection<DailyUsage>(dailyUsage.ToList());
            ModelTotals = new ReadOnlyCollection<ModelTokenTotal>(modelTotals.ToList());
            TotalTokens = totalTokens;
            TotalMessages = totalMessages;
            TotalSessions = totalSessions;
            TotalToolCalls = totalToolCalls;
            ActiveDays = activeDays;
            AvailableModels = new ReadOnlyCollection<string>(availableModels.ToList());
            SelectedModels = new ReadOnlyCollection<string>(selectedModels.ToList());
            FromDate = fromDate;
            ToDate = toDate;
        }

        public IReadOnlyList<DailyUsage> DailyUsage { get; }

        public IReadOnlyList<ModelTokenTotal> ModelTotals { get; }

        public long TotalTokens { get; }

        public long TotalMessages { get; }

        public long TotalSessions { get; }

        public long TotalToolCalls { get; }

        public int ActiveDays { get; }

        public IReadOnlyList<string> AvailableModels { get; }

        public IReadOnlyList<string> SelectedModels { get; }

        public string FromDate { get; }

        public string ToDate { get; }

        /// <summary>
        /// Always true. stats-cache.json does not contain per-model message, session, or tool counts.
        /// </summary>
        public bool ActivityIncludesAllModels => true;

        public bool IsEmpty => TotalTokens == 0
                               && TotalMessages == 0
                               && TotalSessions == 0
                               && TotalToolCalls == 0;
    }

    public static class UsageAnalyticsCalculator
    {
        public static UsageAnalytics Calculate(StatsCacheDocument document, UsageFilter filter)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            filter = filter ?? new UsageFilter();
            var selectedModels = filter.IncludesAllModels
                ? new HashSet<string>(document.ModelIds, StringComparer.Ordinal)
                : new HashSet<string>(filter.ModelIds, StringComparer.Ordinal);
            var activityByDate = document.DailyActivity.ToDictionary(value => value.Date, StringComparer.Ordinal);
            var tokensByDate = document.DailyModelTokens.ToDictionary(value => value.Date, StringComparer.Ordinal);
            var dates = new SortedSet<string>(StringComparer.Ordinal);

            foreach (var date in activityByDate.Keys)
            {
                if (DateKey.IsWithinInclusiveRange(date, filter.FromDate, filter.ToDate))
                {
                    dates.Add(date);
                }
            }

            foreach (var date in tokensByDate.Keys)
            {
                if (DateKey.IsWithinInclusiveRange(date, filter.FromDate, filter.ToDate))
                {
                    dates.Add(date);
                }
            }

            var modelTotals = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var model in selectedModels)
            {
                modelTotals[model] = 0;
            }

            long totalTokens = 0;
            long totalMessages = 0;
            long totalSessions = 0;
            long totalToolCalls = 0;
            var activeDays = 0;
            var daily = new List<DailyUsage>();
            foreach (var date in dates)
            {
                DailyActivity activity;
                activityByDate.TryGetValue(date, out activity);
                DailyModelTokens tokenDay;
                tokensByDate.TryGetValue(date, out tokenDay);

                long dayTokens = 0;
                if (tokenDay != null)
                {
                    foreach (var modelPair in tokenDay.TokensByModel)
                    {
                        if (!selectedModels.Contains(modelPair.Key))
                        {
                            continue;
                        }

                        dayTokens = SaturatingAdd(dayTokens, modelPair.Value);
                        long existing;
                        modelTotals.TryGetValue(modelPair.Key, out existing);
                        modelTotals[modelPair.Key] = SaturatingAdd(existing, modelPair.Value);
                    }
                }

                var messageCount = activity == null ? 0 : activity.MessageCount;
                var sessionCount = activity == null ? 0 : activity.SessionCount;
                var toolCallCount = activity == null ? 0 : activity.ToolCallCount;
                totalTokens = SaturatingAdd(totalTokens, dayTokens);
                totalMessages = SaturatingAdd(totalMessages, messageCount);
                totalSessions = SaturatingAdd(totalSessions, sessionCount);
                totalToolCalls = SaturatingAdd(totalToolCalls, toolCallCount);
                if (messageCount > 0 || sessionCount > 0 || toolCallCount > 0 || dayTokens > 0)
                {
                    activeDays++;
                }

                daily.Add(new DailyUsage(date, dayTokens, messageCount, sessionCount, toolCallCount));
            }

            return new UsageAnalytics(
                daily,
                modelTotals
                    .Where(pair => pair.Value > 0 || !filter.IncludesAllModels)
                    .OrderByDescending(pair => pair.Value)
                    .ThenBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new ModelTokenTotal(pair.Key, pair.Value))
                    .ToList(),
                totalTokens,
                totalMessages,
                totalSessions,
                totalToolCalls,
                activeDays,
                document.ModelIds.ToList(),
                selectedModels.OrderBy(value => value, StringComparer.Ordinal).ToList(),
                filter.FromDate,
                filter.ToDate);
        }

        private static long SaturatingAdd(long left, long right)
        {
            return long.MaxValue - left < right ? long.MaxValue : left + right;
        }
    }
}
