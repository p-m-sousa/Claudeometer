using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    /// <summary>Everything the PDF needs that analytics alone cannot describe.</summary>
    public sealed class UsageReportOptions
    {
        public UsageReportOptions()
        {
            Title = "Claude Code Usage Report";
            RangeLabel = "All recorded dates";
            ModelLabel = "All models";
            TimeZoneLabel = TimeZoneInfo.Local.StandardName;
            DataLocations = new List<string>();
            Metric = TokenMetric.Processed;
            GeneratedAt = DateTimeOffset.Now;
        }

        public string Title { get; set; }

        public string RangeLabel { get; set; }

        public string ModelLabel { get; set; }

        public string TimeZoneLabel { get; set; }

        /// <summary>Folders the figures were read from, for provenance.</summary>
        public IList<string> DataLocations { get; set; }

        /// <summary>Which token figure headline numbers and the chart use.</summary>
        public TokenMetric Metric { get; set; }

        /// <summary>Configured daily threshold, or zero when alerts are off.</summary>
        public long DailyThresholdTokens { get; set; }

        /// <summary>Days served from this app's archive because Claude Code deleted the transcripts.</summary>
        public int ArchivedOnlyDays { get; set; }

        public DateTimeOffset GeneratedAt { get; set; }
    }

    /// <summary>
    /// Renders a paginated usage report as a PDF file. No external tooling, print driver, or
    /// package is involved, so export works on a locked-down machine.
    /// </summary>
    public static class UsageReportWriter
    {
        private const double PageWidth = 612;
        private const double PageHeight = 792;
        private const double Margin = 54;
        private const double ContentRight = PageWidth - Margin;
        private const double ContentWidth = ContentRight - Margin;
        private const double FooterRule = 752;
        private const double LastContentRow = 738;

        private static readonly PdfColor Ink = new PdfColor(28, 34, 44);
        private static readonly PdfColor Heading = new PdfColor(17, 24, 39);
        private static readonly PdfColor Muted = new PdfColor(105, 116, 132);
        private static readonly PdfColor Rule = new PdfColor(214, 220, 229);
        private static readonly PdfColor CardFill = new PdfColor(245, 247, 250);
        private static readonly PdfColor Accent = new PdfColor(24, 122, 154);
        private static readonly PdfColor Bar = new PdfColor(45, 130, 160);
        private static readonly PdfColor BarOverThreshold = new PdfColor(196, 92, 40);
        private static readonly PdfColor ZebraFill = new PdfColor(249, 250, 252);

        public static void Write(string path, UsageAnalytics analytics, UsageReportOptions options)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A file path is required.", nameof(path));
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                Write(stream, analytics, options);
            }
        }

        public static void Write(Stream stream, UsageAnalytics analytics, UsageReportOptions options)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (analytics == null) throw new ArgumentNullException(nameof(analytics));
            options = options ?? new UsageReportOptions();

            var document = new PdfBuilder(PageWidth, PageHeight);
            document.Title = options.Title;

            var page = document.AddPage();
            var y = DrawTitle(page, options);
            y = DrawSummary(page, analytics, options, y);
            y = DrawChart(page, analytics, options, y);
            DrawModelTable(document, page, analytics, options, y);
            DrawDailyTable(document, analytics, options);
            DrawFooters(document, options);
            document.Save(stream);
        }

        private static double DrawTitle(PdfPage page, UsageReportOptions options)
        {
            page.Text(Margin, 72, options.Title, PdfFont.Bold, 19, Heading);
            page.Text(
                Margin,
                90,
                options.RangeLabel + "   -   " + options.ModelLabel,
                PdfFont.Regular,
                10.5,
                Ink);

            var generated = "Generated " +
                options.GeneratedAt.ToString("f", CultureInfo.CurrentCulture) +
                "   -   Days are local calendar days (" + options.TimeZoneLabel + ")";
            page.Text(Margin, 105, generated, PdfFont.Regular, 8.5, Muted);
            page.Line(Margin, 114, ContentRight, 114, 0.8, Rule);
            return 114;
        }

        private static double DrawSummary(PdfPage page, UsageAnalytics analytics, UsageReportOptions options, double top)
        {
            var y = top + 26;
            page.Text(Margin, y, "Summary", PdfFont.Bold, 12.5, Heading);
            y += 10;

            var metricLabel = options.Metric == TokenMetric.InputOutput ? "Input + output tokens" : "Processed tokens";
            var cards = new[]
            {
                new KeyValuePair<string, string>(metricLabel.ToUpperInvariant(), Exact(analytics.Tokens.Select(options.Metric))),
                new KeyValuePair<string, string>("INPUT", Exact(analytics.Tokens.InputTokens)),
                new KeyValuePair<string, string>("OUTPUT", Exact(analytics.Tokens.OutputTokens)),
                new KeyValuePair<string, string>("CACHE READ", Exact(analytics.Tokens.CacheReadTokens)),
                new KeyValuePair<string, string>("CACHE CREATION", Exact(analytics.Tokens.CacheCreationTokens)),
                new KeyValuePair<string, string>("ACTIVE DAYS", Exact(analytics.ActiveDays))
            };

            const double Gap = 12;
            const double CardHeight = 52;
            var cardWidth = (ContentWidth - (Gap * 2)) / 3D;
            for (var index = 0; index < cards.Length; index++)
            {
                var column = index % 3;
                var row = index / 3;
                var x = Margin + (column * (cardWidth + Gap));
                var cardTop = y + (row * (CardHeight + Gap));
                page.FillRect(x, cardTop, cardWidth, CardHeight, CardFill);
                page.FillRect(x, cardTop, cardWidth, 2.5, Accent);
                page.Text(x + 10, cardTop + 20, cards[index].Key, PdfFont.Bold, 7.5, Muted);
                page.Text(
                    x + 10,
                    cardTop + 41,
                    PdfFontMetrics.Fit(cards[index].Value, PdfFont.Bold, 15, cardWidth - 20),
                    PdfFont.Bold,
                    15,
                    Heading);
            }

            y += (CardHeight * 2) + Gap + 22;
            var activity = "Responses " + Exact(analytics.TotalResponses) +
                "   -   Tool calls " + Exact(analytics.TotalToolCalls) +
                "   -   Messages " + Exact(analytics.TotalMessages) +
                "   -   Sessions " + Exact(analytics.TotalSessions) +
                "   -   Average per active day " + Exact(analytics.AveragePerActiveDay(options.Metric));
            page.Text(Margin, y, activity, PdfFont.Regular, 8.5, Ink);

            y += 13;
            var peak = analytics.PeakDay(options.Metric);
            var notes = new List<string>();
            if (peak != null)
            {
                notes.Add("Busiest day " + peak.Date + " at " + Exact(peak.Tokens.Select(options.Metric)) + " tokens");
            }

            if (options.DailyThresholdTokens > 0)
            {
                var over = analytics.Days.Count(day => day.Tokens.Select(options.Metric) >= options.DailyThresholdTokens);
                notes.Add("Daily threshold " + Exact(options.DailyThresholdTokens) + " tokens, reached on " +
                          Exact(over) + " of " + Exact(analytics.Days.Count) + " recorded days");
            }

            if (analytics.ActivityIsWholeDay)
            {
                notes.Add("Message and session counts cover all models; token, response, and tool-call figures are filtered");
            }

            if (options.ArchivedOnlyDays > 0)
            {
                notes.Add(Exact(options.ArchivedOnlyDays) +
                          " earlier day(s) come from this app's own archive, because Claude Code has deleted those transcripts");
            }

            foreach (var note in notes)
            {
                page.Text(Margin, y, PdfFontMetrics.Fit(note, PdfFont.Regular, 8.5, ContentWidth), PdfFont.Regular, 8.5, Muted);
                y += 12;
            }

            return y;
        }

        private static double DrawChart(PdfPage page, UsageAnalytics analytics, UsageReportOptions options, double top)
        {
            var buckets = BuildChartBuckets(analytics, options);
            var y = top + 22;
            var heading = buckets.IsWeekly ? "Tokens by week" : "Tokens by day";
            page.Text(Margin, y, heading, PdfFont.Bold, 12.5, Heading);
            y += 10;

            const double PlotHeight = 132;
            var plotLeft = Margin + 46;
            var plotTop = y;
            var plotBottom = plotTop + PlotHeight;
            var plotWidth = ContentRight - plotLeft;

            if (buckets.Values.Count == 0)
            {
                page.Text(Margin, plotTop + 20, "No usage was recorded in this range.", PdfFont.Regular, 9.5, Muted);
                return plotTop + 30;
            }

            var maximum = buckets.Values.Max();
            var axisMaximum = NiceCeiling(maximum);
            for (var step = 0; step <= 4; step++)
            {
                var value = axisMaximum * step / 4D;
                var lineY = plotBottom - (PlotHeight * step / 4D);
                page.Line(plotLeft, lineY, ContentRight, lineY, step == 0 ? 0.8 : 0.4, Rule);
                page.TextRight(plotLeft - 6, lineY + 3, Compact((long)Math.Round(value)), PdfFont.Regular, 7, Muted);
            }

            var slot = plotWidth / buckets.Values.Count;
            var barWidth = Math.Max(1.2, Math.Min(slot - 1.5, 26));
            for (var index = 0; index < buckets.Values.Count; index++)
            {
                var value = buckets.Values[index];
                var height = axisMaximum <= 0 ? 0 : PlotHeight * value / axisMaximum;
                if (value > 0 && height < 0.8) height = 0.8;
                var x = plotLeft + (index * slot) + ((slot - barWidth) / 2D);
                var overThreshold = options.DailyThresholdTokens > 0 && value >= options.DailyThresholdTokens;
                page.FillRect(x, plotBottom - height, barWidth, height, overThreshold ? BarOverThreshold : Bar);
            }

            if (options.DailyThresholdTokens > 0 && axisMaximum > 0 && options.DailyThresholdTokens <= axisMaximum)
            {
                var thresholdY = plotBottom - (PlotHeight * options.DailyThresholdTokens / axisMaximum);
                page.Line(plotLeft, thresholdY, ContentRight, thresholdY, 0.7, BarOverThreshold);
                page.Text(plotLeft + 3, thresholdY - 3, "Daily threshold", PdfFont.Bold, 6.5, BarOverThreshold);
            }

            var labelStep = (int)Math.Ceiling(buckets.Values.Count / 14D);
            for (var index = 0; index < buckets.Labels.Count; index += labelStep)
            {
                page.TextCenter(
                    plotLeft + (index * slot) + (slot / 2D),
                    plotBottom + 11,
                    buckets.Labels[index],
                    PdfFont.Regular,
                    6.5,
                    Muted);
            }

            return plotBottom + 18;
        }

        private static void DrawModelTable(
            PdfBuilder document,
            PdfPage page,
            UsageAnalytics analytics,
            UsageReportOptions options,
            double top)
        {
            var columns = new[]
            {
                new Column("Model", 120, false),
                new Column("Input", 62, true),
                new Column("Output", 62, true),
                new Column("Cache read", 66, true),
                new Column("Cache creation", 74, true),
                new Column("Processed", 66, true),
                new Column("Responses", 54, true)
            };

            var y = top + 22;
            page.Text(Margin, y, "Totals by model", PdfFont.Bold, 12.5, Heading);
            y += 8;

            var cursor = new TableCursor(document, page, y);
            cursor.DrawHeader(columns);
            foreach (var model in analytics.Models)
            {
                cursor.DrawRow(columns, new[]
                {
                    model.ModelId,
                    Exact(model.Tokens.InputTokens),
                    Exact(model.Tokens.OutputTokens),
                    Exact(model.Tokens.CacheReadTokens),
                    Exact(model.Tokens.CacheCreationTokens),
                    Exact(model.Tokens.ProcessedTokens),
                    Exact(model.ResponseCount)
                });
            }

            if (analytics.Models.Count == 0)
            {
                cursor.DrawEmpty("No model usage in this range.");
            }
            else
            {
                cursor.DrawTotals(columns, new[]
                {
                    "All selected models",
                    Exact(analytics.Tokens.InputTokens),
                    Exact(analytics.Tokens.OutputTokens),
                    Exact(analytics.Tokens.CacheReadTokens),
                    Exact(analytics.Tokens.CacheCreationTokens),
                    Exact(analytics.Tokens.ProcessedTokens),
                    Exact(analytics.TotalResponses)
                });
            }
        }

        private static void DrawDailyTable(PdfBuilder document, UsageAnalytics analytics, UsageReportOptions options)
        {
            var columns = new[]
            {
                new Column("Date", 62, false),
                new Column("Input", 58, true),
                new Column("Output", 58, true),
                new Column("Cache read", 64, true),
                new Column("Cache creation", 72, true),
                new Column("Processed", 64, true),
                new Column("Responses", 44, true),
                new Column("Tools", 40, true),
                new Column("Sessions", 42, true)
            };

            var page = document.AddPage();
            page.Text(Margin, 72, "Daily detail", PdfFont.Bold, 12.5, Heading);
            page.Text(
                Margin,
                86,
                "Every recorded day in the selected range, oldest first.",
                PdfFont.Regular,
                8.5,
                Muted);

            var cursor = new TableCursor(document, page, 92);
            cursor.DrawHeader(columns);
            foreach (var day in analytics.Days)
            {
                cursor.DrawRow(columns, new[]
                {
                    day.Date,
                    Exact(day.Tokens.InputTokens),
                    Exact(day.Tokens.OutputTokens),
                    Exact(day.Tokens.CacheReadTokens),
                    Exact(day.Tokens.CacheCreationTokens),
                    Exact(day.Tokens.ProcessedTokens),
                    Exact(day.ResponseCount),
                    Exact(day.ToolCallCount),
                    Exact(day.SessionCount)
                });
            }

            if (analytics.Days.Count == 0)
            {
                cursor.DrawEmpty("No usage was recorded in this range.");
                return;
            }

            cursor.DrawTotals(columns, new[]
            {
                "Total",
                Exact(analytics.Tokens.InputTokens),
                Exact(analytics.Tokens.OutputTokens),
                Exact(analytics.Tokens.CacheReadTokens),
                Exact(analytics.Tokens.CacheCreationTokens),
                Exact(analytics.Tokens.ProcessedTokens),
                Exact(analytics.TotalResponses),
                Exact(analytics.TotalToolCalls),
                Exact(analytics.TotalSessions)
            });
        }

        private static void DrawFooters(PdfBuilder document, UsageReportOptions options)
        {
            var locations = options.DataLocations == null || options.DataLocations.Count == 0
                ? "local Claude Code data"
                : string.Join(" ; ", options.DataLocations.ToArray());
            var note = "Claude Usage - read from " + locations +
                       " - locally recorded token counts, not a bill or a plan-limit statement";
            var total = document.Pages.Count;
            for (var index = 0; index < total; index++)
            {
                var page = document.Pages[index];
                page.Line(Margin, FooterRule, ContentRight, FooterRule, 0.6, Rule);
                page.Text(
                    Margin,
                    FooterRule + 13,
                    PdfFontMetrics.Fit(note, PdfFont.Regular, 7, ContentWidth - 60),
                    PdfFont.Regular,
                    7,
                    Muted);
                page.TextRight(
                    ContentRight,
                    FooterRule + 13,
                    "Page " + (index + 1).ToString(CultureInfo.CurrentCulture) + " of " +
                    total.ToString(CultureInfo.CurrentCulture),
                    PdfFont.Regular,
                    7,
                    Muted);
            }
        }

        private static ChartBuckets BuildChartBuckets(UsageAnalytics analytics, UsageReportOptions options)
        {
            var result = new ChartBuckets();
            if (analytics.Days.Count == 0) return result;

            var start = ParseDate(analytics.FromDate ?? analytics.Days[0].Date);
            var end = ParseDate(analytics.ToDate ?? analytics.Days[analytics.Days.Count - 1].Date);
            if (start == null || end == null || end.Value < start.Value) return result;

            var byDate = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var day in analytics.Days)
            {
                byDate[day.Date] = day.Tokens.Select(options.Metric);
            }

            var totalDays = (int)(end.Value - start.Value).TotalDays + 1;
            if (totalDays > 3660) totalDays = 3660;
            result.IsWeekly = totalDays > 120;
            var groupSize = result.IsWeekly ? 7 : 1;

            for (var offset = 0; offset < totalDays; offset += groupSize)
            {
                long value = 0;
                for (var inner = 0; inner < groupSize && offset + inner < totalDays; inner++)
                {
                    var date = start.Value.AddDays(offset + inner);
                    long dayValue;
                    if (byDate.TryGetValue(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), out dayValue))
                    {
                        value = Numbers.Add(value, dayValue);
                    }
                }

                result.Values.Add(value);
                result.Labels.Add(start.Value.AddDays(offset).ToString("MM-dd", CultureInfo.InvariantCulture));
            }

            return result;
        }

        private static DateTime? ParseDate(string value)
        {
            DateTime parsed;
            return DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static long NiceCeiling(long value)
        {
            if (value <= 0) return 1;
            var magnitude = (long)Math.Pow(10, Math.Floor(Math.Log10(value)));
            foreach (var step in new[] { 1D, 1.25D, 1.5D, 2D, 2.5D, 3D, 4D, 5D, 7.5D, 10D })
            {
                var candidate = (long)(step * magnitude);
                if (candidate >= value) return candidate;
            }

            return magnitude * 10;
        }

        private static string Exact(long value)
        {
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private static string Compact(long value)
        {
            if (value >= 1000000000L) return (value / 1000000000D).ToString("0.#", CultureInfo.CurrentCulture) + "B";
            if (value >= 1000000L) return (value / 1000000D).ToString("0.#", CultureInfo.CurrentCulture) + "M";
            if (value >= 1000L) return (value / 1000D).ToString("0.#", CultureInfo.CurrentCulture) + "K";
            return value.ToString("N0", CultureInfo.CurrentCulture);
        }

        private sealed class ChartBuckets
        {
            internal ChartBuckets()
            {
                Values = new List<long>();
                Labels = new List<string>();
            }

            internal IList<long> Values { get; }

            internal IList<string> Labels { get; }

            internal bool IsWeekly { get; set; }
        }

        private sealed class Column
        {
            internal Column(string title, double width, bool rightAligned)
            {
                Title = title;
                Width = width;
                RightAligned = rightAligned;
            }

            internal string Title { get; }

            internal double Width { get; }

            internal bool RightAligned { get; }
        }

        /// <summary>Lays out table rows, starting a continuation page whenever one fills up.</summary>
        private sealed class TableCursor
        {
            private const double RowHeight = 14.5;
            private const double FontSize = 7.6;

            private readonly PdfBuilder _document;
            private PdfPage _page;
            private double _y;
            private int _rowIndex;
            private Column[] _repeatHeader;

            internal TableCursor(PdfBuilder document, PdfPage page, double top)
            {
                _document = document;
                _page = page;
                _y = top;
            }

            internal void DrawHeader(Column[] columns)
            {
                _repeatHeader = columns;
                _y += 14;
                var x = Margin;
                foreach (var column in columns)
                {
                    var text = PdfFontMetrics.Fit(column.Title, PdfFont.Bold, FontSize, column.Width - 6);
                    if (column.RightAligned)
                    {
                        _page.TextRight(x + column.Width - 3, _y, text, PdfFont.Bold, FontSize, Heading);
                    }
                    else
                    {
                        _page.Text(x + 3, _y, text, PdfFont.Bold, FontSize, Heading);
                    }

                    x += column.Width;
                }

                _y += 4;
                _page.Line(Margin, _y, ContentRight, _y, 0.7, Rule);
                _rowIndex = 0;
            }

            internal void DrawRow(Column[] columns, string[] values)
            {
                if (_y + RowHeight > LastContentRow)
                {
                    _page = _document.AddPage();
                    _y = 62;
                    DrawHeader(_repeatHeader);
                }

                if (_rowIndex % 2 == 1)
                {
                    _page.FillRect(Margin, _y + 1.5, ContentWidth, RowHeight, ZebraFill);
                }

                var x = Margin;
                var baseline = _y + RowHeight - 3.5;
                for (var index = 0; index < columns.Length && index < values.Length; index++)
                {
                    var column = columns[index];
                    var text = PdfFontMetrics.Fit(values[index], PdfFont.Regular, FontSize, column.Width - 6);
                    if (column.RightAligned)
                    {
                        _page.TextRight(x + column.Width - 3, baseline, text, PdfFont.Regular, FontSize, Ink);
                    }
                    else
                    {
                        _page.Text(x + 3, baseline, text, PdfFont.Regular, FontSize, Ink);
                    }

                    x += column.Width;
                }

                _y += RowHeight;
                _rowIndex++;
            }

            internal void DrawTotals(Column[] columns, string[] values)
            {
                if (_y + RowHeight + 4 > LastContentRow)
                {
                    _page = _document.AddPage();
                    _y = 62;
                    DrawHeader(_repeatHeader);
                }

                _page.Line(Margin, _y + 1, ContentRight, _y + 1, 0.7, Rule);
                var x = Margin;
                var baseline = _y + RowHeight - 2.5;
                for (var index = 0; index < columns.Length && index < values.Length; index++)
                {
                    var column = columns[index];
                    var text = PdfFontMetrics.Fit(values[index], PdfFont.Bold, FontSize, column.Width - 6);
                    if (column.RightAligned)
                    {
                        _page.TextRight(x + column.Width - 3, baseline, text, PdfFont.Bold, FontSize, Heading);
                    }
                    else
                    {
                        _page.Text(x + 3, baseline, text, PdfFont.Bold, FontSize, Heading);
                    }

                    x += column.Width;
                }

                _y += RowHeight + 2;
            }

            internal void DrawEmpty(string message)
            {
                _page.Text(Margin + 3, _y + 14, message, PdfFont.Regular, 8.5, Muted);
                _y += 20;
            }
        }
    }
}
