using System;
using System.Globalization;

namespace ClaudeUsage.Core
{
    public enum AlertLevel
    {
        None = 0,
        Warning = 1,
        Limit = 2
    }

    /// <summary>User-configured daily threshold.</summary>
    public sealed class AlertSettings
    {
        public AlertSettings()
        {
            WarnPercent = 80;
            Metric = TokenMetric.Processed;
        }

        public bool Enabled { get; set; }

        /// <summary>Daily token budget. Zero disables evaluation regardless of <see cref="Enabled"/>.</summary>
        public long DailyLimitTokens { get; set; }

        /// <summary>Percentage of the limit that raises the early warning, clamped to 1-100.</summary>
        public int WarnPercent { get; set; }

        public TokenMetric Metric { get; set; }

        public bool IsActive
        {
            get { return Enabled && DailyLimitTokens > 0; }
        }

        public int EffectiveWarnPercent
        {
            get { return Math.Max(1, Math.Min(100, WarnPercent)); }
        }

        public long WarnTokens
        {
            get
            {
                return DailyLimitTokens <= 0
                    ? 0
                    : (long)Math.Ceiling(DailyLimitTokens * (EffectiveWarnPercent / 100D));
            }
        }

        public AlertSettings Clone()
        {
            return new AlertSettings
            {
                Enabled = Enabled,
                DailyLimitTokens = DailyLimitTokens,
                WarnPercent = WarnPercent,
                Metric = Metric
            };
        }
    }

    /// <summary>The highest level already announced for a given day, so alerts never repeat.</summary>
    public sealed class AlertState
    {
        public AlertState()
        {
        }

        public AlertState(string date, AlertLevel level)
        {
            Date = date;
            Level = level;
        }

        public string Date { get; set; }

        public AlertLevel Level { get; set; }

        public AlertLevel LevelFor(string date)
        {
            return string.Equals(Date, date, StringComparison.Ordinal) ? Level : AlertLevel.None;
        }
    }

    public sealed class AlertEvaluation
    {
        internal AlertEvaluation(
            AlertLevel level,
            bool shouldNotify,
            long tokens,
            long limit,
            int percent,
            string title,
            string message)
        {
            Level = level;
            ShouldNotify = shouldNotify;
            Tokens = tokens;
            Limit = limit;
            Percent = percent;
            Title = title;
            Message = message;
        }

        public AlertLevel Level { get; }

        /// <summary>True only the first time a day reaches a level, so a refresh loop stays quiet.</summary>
        public bool ShouldNotify { get; }

        public long Tokens { get; }

        public long Limit { get; }

        /// <summary>Percentage of the daily threshold used, which may exceed 100.</summary>
        public int Percent { get; }

        public string Title { get; }

        public string Message { get; }
    }

    public static class UsageAlertEvaluator
    {
        public static AlertEvaluation Evaluate(
            AlertSettings settings,
            string date,
            long tokensToday,
            AlertState alreadyNotified)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));

            var limit = settings.DailyLimitTokens;
            var percent = limit <= 0 ? 0 : (int)Math.Min(int.MaxValue, (long)Math.Floor(tokensToday * 100D / limit));
            var level = AlertLevel.None;
            if (settings.IsActive)
            {
                if (tokensToday >= limit) level = AlertLevel.Limit;
                else if (tokensToday >= settings.WarnTokens) level = AlertLevel.Warning;
            }

            var previous = alreadyNotified == null ? AlertLevel.None : alreadyNotified.LevelFor(date);
            var shouldNotify = settings.IsActive && level > previous;

            var metricLabel = settings.Metric == TokenMetric.InputOutput
                ? "input + output tokens"
                : "processed tokens";
            string title;
            string message;
            if (level == AlertLevel.Limit)
            {
                title = "Daily Claude Code threshold reached";
                message = "Today's usage is " + Format(tokensToday) + " " + metricLabel + ", which is " +
                          percent.ToString(CultureInfo.CurrentCulture) + "% of your " + Format(limit) +
                          " daily threshold.";
            }
            else if (level == AlertLevel.Warning)
            {
                title = "Approaching your daily Claude Code threshold";
                message = "Today's usage is " + Format(tokensToday) + " " + metricLabel + ", which is " +
                          percent.ToString(CultureInfo.CurrentCulture) + "% of your " + Format(limit) +
                          " daily threshold.";
            }
            else
            {
                title = string.Empty;
                message = limit <= 0
                    ? "No daily threshold is set."
                    : "Today's usage is " + Format(tokensToday) + " " + metricLabel + ", which is " +
                      percent.ToString(CultureInfo.CurrentCulture) + "% of your " + Format(limit) +
                      " daily threshold.";
            }

            return new AlertEvaluation(level, shouldNotify, tokensToday, limit, percent, title, message);
        }

        private static string Format(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000D).ToString("0.##", CultureInfo.CurrentCulture) + "B";
            if (value >= 1000000L) return (value / 1000000D).ToString("0.##", CultureInfo.CurrentCulture) + "M";
            if (value >= 1000L) return (value / 1000D).ToString("0.##", CultureInfo.CurrentCulture) + "K";
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }
    }
}
