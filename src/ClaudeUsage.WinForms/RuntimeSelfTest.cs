using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    /// <summary>
    /// End-to-end check of the shipped binary: scan transcripts, keep the archive after the
    /// transcripts are gone, filter a date range, and write a PDF. Runs from
    /// <c>ClaudeUsage.exe --self-test</c> so packaging is verified, not just the source.
    /// </summary>
    internal static class RuntimeSelfTest
    {
        internal static bool Run(TextWriter output)
        {
            var scratch = Path.Combine(
                Path.GetTempPath(),
                "ClaudeUsageSelfTest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var transcript = WriteFixture(scratch);
                var storePath = Path.Combine(scratch, "store.json");
                var roots = new[] { Path.Combine(scratch, "data") };

                var store = new UsageStore(storePath);
                store.Load(TimeZoneInfo.Utc);
                var first = store.Refresh(roots, TimeZoneInfo.Utc, CancellationToken.None);

                var day = first.History.FindDay("2026-08-12");
                Check(day != null, "the scanned day is present", output);
                if (day == null) return false;

                // Claude Code repeats one response's usage on every content-block line. Counting
                // lines would report 210 processed tokens here instead of 110.
                Check(day.Tokens.ProcessedTokens == 110, "repeated response lines are counted once", output);
                Check(day.Tokens.InputTokens == 11, "input tokens", output);
                Check(day.Tokens.OutputTokens == 22, "output tokens", output);
                Check(day.Tokens.CacheReadTokens == 33, "cache read tokens", output);
                Check(day.Tokens.CacheCreationTokens == 44, "cache creation tokens", output);
                Check(day.ResponseCount == 2, "distinct responses", output);
                Check(day.ToolCallCount == 3, "tool-use blocks across every line of a response", output);
                Check(day.Models.Count == 2, "per-model breakdown", output);

                var started = first.History.FindDay("2026-08-11");
                Check(started != null && started.SessionCount == 1, "session dated by its first entry", output);
                Check(first.Report.IsComplete, "settled transcripts produce a complete scan", output);

                var filtered = UsageAnalyticsCalculator.Calculate(
                    first.History,
                    new UsageFilter("2026-08-12", "2026-08-12", new[] { "claude-opus-5" }));
                Check(filtered.Tokens.ProcessedTokens == 100, "date and model filtering", output);
                Check(filtered.Days.Count == 1, "one day in range", output);

                Check(store.Save(), "the archive can be written", output);

                // Claude Code deletes transcripts once they age out. The archive must outlive them.
                File.Delete(transcript);
                var reopened = new UsageStore(storePath);
                reopened.Load(TimeZoneInfo.Utc);
                var second = reopened.Refresh(roots, TimeZoneInfo.Utc, CancellationToken.None);
                var archived = second.History.FindDay("2026-08-12");
                Check(
                    archived != null && archived.Tokens.ProcessedTokens == 110,
                    "history survives transcript cleanup",
                    output);
                Check(second.Report.FilesSeen == 0, "the deleted transcript is no longer read", output);
                Check(second.ArchivedOnlyDays == 2, "days with no transcript are reported as archived", output);

                var evaluation = UsageAlertEvaluator.Evaluate(
                    new AlertSettings { Enabled = true, DailyLimitTokens = 100, WarnPercent = 50 },
                    "2026-08-12",
                    110,
                    new AlertState());
                Check(evaluation.Level == AlertLevel.Limit, "threshold evaluation", output);
                Check(evaluation.ShouldNotify, "a newly crossed threshold notifies once", output);
                Check(
                    !UsageAlertEvaluator.Evaluate(
                        new AlertSettings { Enabled = true, DailyLimitTokens = 100, WarnPercent = 50 },
                        "2026-08-12",
                        110,
                        new AlertState("2026-08-12", AlertLevel.Limit)).ShouldNotify,
                    "an already announced threshold stays quiet",
                    output);

                using (var pdf = new MemoryStream())
                {
                    UsageReportWriter.Write(
                        pdf,
                        UsageAnalyticsCalculator.Calculate(second.History, new UsageFilter()),
                        new UsageReportOptions { RangeLabel = "Self test" });
                    var bytes = pdf.ToArray();
                    var text = Encoding.ASCII.GetString(bytes);
                    Check(bytes.Length > 1200, "the PDF has content", output);
                    Check(text.StartsWith("%PDF-1.4", StringComparison.Ordinal), "PDF header", output);
                    Check(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "PDF trailer", output);
                    Check(text.Contains("/Type /Catalog"), "PDF catalog", output);
                    Check(CrossReferenceOffsetsResolve(text), "PDF cross-reference offsets resolve", output);
                }

                if (output != null) output.WriteLine("PASS: self-test");
                return true;
            }
            catch (Exception error)
            {
                if (output != null) output.WriteLine("FAIL: " + error);
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
                }
                catch
                {
                    // A leftover temp folder is harmless.
                }
            }
        }

        private static string WriteFixture(string scratch)
        {
            var project = Path.Combine(scratch, "data", "projects", "-c-work-demo");
            Directory.CreateDirectory(project);
            var transcript = Path.Combine(project, "11111111-2222-3333-4444-555555555555.jsonl");

            const string OpusUsage =
                "\"usage\":{\"input_tokens\":10,\"output_tokens\":20,\"cache_read_input_tokens\":30," +
                "\"cache_creation_input_tokens\":40,\"server_tool_use\":{\"web_search_requests\":1}}";
            var lines = new[]
            {
                "{\"type\":\"user\",\"timestamp\":\"2026-08-11T23:30:00Z\",\"message\":{\"role\":\"user\"," +
                "\"content\":\"PRIVATE_PROMPT\"}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T00:10:00Z\",\"requestId\":\"req_1\"," +
                "\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5\"," + OpusUsage +
                ",\"content\":[{\"type\":\"text\",\"text\":\"PRIVATE_REPLY\"}]}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T00:10:01Z\",\"requestId\":\"req_1\"," +
                "\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5\"," + OpusUsage +
                ",\"content\":[{\"type\":\"tool_use\",\"input\":{\"path\":\"PRIVATE_PATH\"}}]}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T00:10:02Z\",\"requestId\":\"req_2\"," +
                "\"message\":{\"id\":\"msg_2\",\"model\":\"claude-sonnet-5\",\"usage\":{\"input_tokens\":1," +
                "\"output_tokens\":2,\"cache_read_input_tokens\":3,\"cache_creation_input_tokens\":4}," +
                "\"content\":[{\"type\":\"tool_use\"},{\"type\":\"tool_use\"}]}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T00:10:03Z\",\"requestId\":\"req_3\"," +
                "\"message\":{\"id\":\"msg_3\",\"model\":\"<synthetic>\",\"usage\":{\"input_tokens\":9999}}}",
                "{\"type\":\"queue-operation\",\"timestamp\":\"2026-08-12T00:10:04Z\",\"operation\":\"add\"}"
            };
            File.WriteAllText(transcript, string.Join("\r\n", lines) + "\r\n", new UTF8Encoding(false));
            return transcript;
        }

        /// <summary>
        /// Verifies that every offset in the cross-reference table points at the matching
        /// "<c>N 0 obj</c>" header, which is what a reader uses to find objects.
        /// </summary>
        private static bool CrossReferenceOffsetsResolve(string text)
        {
            var startIndex = text.LastIndexOf("startxref", StringComparison.Ordinal);
            if (startIndex < 0) return false;
            var digits = new string(text.Substring(startIndex + 9)
                .SkipWhile(character => !char.IsDigit(character))
                .TakeWhile(char.IsDigit)
                .ToArray());
            int xrefOffset;
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out xrefOffset)) return false;
            if (xrefOffset <= 0 || xrefOffset >= text.Length) return false;
            if (!text.Substring(xrefOffset).StartsWith("xref", StringComparison.Ordinal)) return false;

            var lines = text.Substring(xrefOffset).Split('\n');
            if (lines.Length < 3) return false;
            var counts = lines[1].Trim().Split(' ');
            int objectCount;
            if (counts.Length != 2 || !int.TryParse(counts[1], out objectCount)) return false;

            for (var number = 1; number < objectCount; number++)
            {
                var entry = lines[number + 2];
                int offset;
                if (entry.Length < 10 ||
                    !int.TryParse(entry.Substring(0, 10), NumberStyles.Integer, CultureInfo.InvariantCulture, out offset))
                {
                    return false;
                }

                var expected = number.ToString(CultureInfo.InvariantCulture) + " 0 obj";
                if (offset < 0 || offset + expected.Length > text.Length) return false;
                if (!text.Substring(offset, expected.Length).Equals(expected, StringComparison.Ordinal)) return false;
            }

            return true;
        }

        private static void Check(bool condition, string name, TextWriter output)
        {
            if (condition) return;
            throw new InvalidOperationException("Self-test assertion failed: " + name);
        }
    }
}
