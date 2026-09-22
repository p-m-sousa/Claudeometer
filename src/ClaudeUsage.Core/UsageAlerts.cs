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

        /// <summary>Daily token budget. Zero disables token-based evaluation.</summary>
        public long DailyLimitTokens { get; set; }

        /// <summary>Use estimated USD spend, including all token categories, instead of tokens.</summary>
        public bool UseSpend { get; set; }

        public decimal DailyLimitUsd { get; set; }

        /// <summary>Percentage of the limit that raises the early warning, clamped to 1-100.</summary>
        public int WarnPercent { get; set; }

        public TokenMetric Metric { get; set; }

        public bool IsActive
        {
            get { return Enabled && (UseSpend ? DailyLimitUsd > 0 : DailyLimitTokens > 0); }
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
                UseSpend = UseSpend,
                DailyLimitUsd = DailyLimitUsd,
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
            string message,
            SpendAmount spend = null,
            decimal limitUsd = 0)
        {
            Level = level;
            ShouldNotify = shouldNotify;
            Tokens = tokens;
            Limit = limit;
            Percent = percent;
            Title = title;
            Message = message;
            Spend = spend;
            LimitUsd = limitUsd;
        }

        public AlertLevel Level { get; }

        /// <summary>True only the first time a day reaches a level, so a refresh loop stays quiet.</summary>
        public bool ShouldNotify { get; }

        public long Tokens { get; }

        public long Limit { get; }

        /// <summary>Spend and its coverage for spend evaluations; null for token evaluations.</summary>
        public SpendAmount Spend { get; }

        public decimal LimitUsd { get; }

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
            if (settings.UseSpend) throw new ArgumentException("Spend thresholds require a spend estimate.", nameof(settings));

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

        public static AlertEvaluation EvaluateSpend(
            AlertSettings settings, string date, SpendAmount spendToday, AlertState alreadyNotified)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            if (spendToday == null) throw new ArgumentNullException(nameof(spendToday));
            if (!settings.UseSpend) throw new ArgumentException("A spend threshold must be selected.", nameof(settings));

            var limit = settings.DailyLimitUsd;
            var amount = spendToday.KnownUsd;
            // Divide before multiplying and cap before converting to handle very small limits.
            var percent = limit <= 0 ? 0 : amount / (int.MaxValue / 100M) >= limit
                ? int.MaxValue : (int)decimal.Floor(amount / limit * 100M);
            var level = AlertLevel.None;
            if (settings.IsActive)
            {
                if (amount >= limit) level = AlertLevel.Limit;
                else if (amount >= limit * (settings.EffectiveWarnPercent / 100M)) level = AlertLevel.Warning;
            }

            var previous = alreadyNotified == null ? AlertLevel.None : alreadyNotified.LevelFor(date);
            var title = level == AlertLevel.Limit ? "Daily estimated spend threshold reached"
                : level == AlertLevel.Warning ? "Approaching your daily estimated spend threshold" : string.Empty;
            var message = limit <= 0 ? "No daily spend threshold is set."
                : "Today's estimated spend is " + FormatUsd(amount) + ", which is " +
                  percent.ToString(CultureInfo.CurrentCulture) + "% of your " + FormatUsd(limit) + " daily threshold.";
            if (spendToday.UnpricedTokens > 0)
                message += " Prices are missing: this is a known subtotal. Configure missing rates in Pricing.";

            return new AlertEvaluation(level, settings.IsActive && level > previous, 0, 0, percent, title, message,
                spendToday, limit);
        }

        public static string FormatUsd(decimal value)
        {
            return "USD $" + value.ToString("0.00##########################", CultureInfo.InvariantCulture);
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
