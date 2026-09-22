using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ClaudeUsage.Core;

internal static class Program
{
    private static int _assertions;

    private static readonly TimeZoneInfo MinusFive = TimeZoneInfo.CreateCustomTimeZone(
        "Test-Minus-Five",
        TimeSpan.FromHours(-5),
        "Test minus five",
        "Test minus five");

    private static int Main()
    {
        var failures = new List<string>();
        foreach (var test in new (string Name, Action Run)[]
        {
            ("scanner collapses repeated response lines", ScannerCollapsesRepeatedResponseLines),
            ("scanner attributes activity per model and day", ScannerAttributesActivity),
            ("scanner includes subagents and skips sidecars", ScannerScopesFiles),
            ("scanner merges multiple data roots", ScannerMergesRoots),
            ("scanner buckets days in the requested time zone", ScannerHonoursTimeZone),
            ("scanner never modifies transcripts", ScannerIsReadOnly),
            ("scanner retains no conversation content", ScannerRetainsNoContent),
            ("scanner reports partial and malformed lines", ScannerReportsBadLines),
            ("index reuses unchanged transcripts", IndexReusesUnchangedFiles),
            ("store persists and reloads its index", StorePersistsIndex),
            ("archive outlives transcript cleanup", ArchiveOutlivesCleanup),
            ("archive never shrinks", ArchiveNeverShrinks),
            ("analytics filter dates and models", AnalyticsFilters),
            ("analytics summarise a range", AnalyticsSummarise),
            ("pricing calculates all four categories precisely", PricingCalculatesCategories),
            ("pricing selects inclusive effective dates", PricingEffectiveDates),
            ("pricing preserves coverage through filters and totals", PricingFiltersAndCoverage),
            ("pricing validates identifiers, dates and rates", PricingValidates),
            ("pricing persists independently of culture", PricingPersists),
            ("pricing recalculates archived history", PricingRecalculatesArchive),
            ("alerts fire once per level per day", AlertsFireOncePerLevel),
            ("spend alerts use precise estimates and preserve coverage", SpendAlerts),
            ("pdf report is structurally valid", PdfIsValid),
            ("pdf report paginates and tolerates empty input", PdfPaginates)
        })
        {
            try
            {
                test.Run();
            }
            catch (Exception error)
            {
                failures.Add(test.Name + ": " + error.Message);
                Console.Error.WriteLine("FAIL " + test.Name);
                Console.Error.WriteLine("     " + error);
            }
        }

        if (failures.Count > 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine(failures.Count + " test(s) failed.");
            return 1;
        }

        Console.WriteLine("PASS: " + _assertions + " assertions across 24 tests");
        return 0;
    }

    // ---------------------------------------------------------------- scanning

    private static void ScannerCollapsesRepeatedResponseLines()
    {
        using (var tree = new Scratch())
        {
            // One assistant response spread over three transcript lines, exactly as Claude Code
            // writes it: same message id and request id, same usage payload, different blocks.
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "text"),
                Assistant("2026-08-12T10:00:01Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "tool_use"),
                Assistant("2026-08-12T10:00:02Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "tool_use")
            });

            var day = ScanDay(tree, "2026-08-12");
            Equal(100L, day.Tokens.ProcessedTokens, "usage counted once per response");
            Equal(1L, day.ResponseCount, "one response");
            Equal(2L, day.ToolCallCount, "every tool_use block still counts");
            Equal(1L, day.MessageCount, "a response is one message");
        }
    }

    private static void ScannerAttributesActivity()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                User("2026-08-11T22:00:00Z"),
                Assistant("2026-08-11T22:00:05Z", "msg_a", "req_a", "claude-opus-5", 1, 2, 3, 4, "text"),
                User("2026-08-12T09:00:00Z"),
                Assistant("2026-08-12T09:00:05Z", "msg_b", "req_b", "claude-sonnet-5", 5, 6, 7, 8, "text"),
                Assistant("2026-08-12T09:00:06Z", "msg_c", "req_c", "<synthetic>", 999, 999, 0, 0, "text"),
                "{\"type\":\"queue-operation\",\"timestamp\":\"2026-08-12T09:00:07Z\",\"operation\":\"add\"}"
            });

            var scan = Scan(tree, TimeZoneInfo.Utc);
            var first = scan.History.FindDay("2026-08-11");
            var second = scan.History.FindDay("2026-08-12");

            Equal(1L, first.SessionCount, "session dated by its first entry");
            Equal(0L, second.SessionCount, "a continuing session is not counted again");
            Equal(10L, first.Tokens.ProcessedTokens, "first day tokens");
            Equal(26L, second.Tokens.ProcessedTokens, "synthetic responses are excluded from tokens");
            Equal(3L, second.MessageCount, "user, model, and synthetic messages all count as messages");
            Equal(1, second.Models.Count, "only the real model appears");
            True(!second.Models.ContainsKey("<synthetic>"), "synthetic model is not a usage row");
            Equal(2, scan.History.ModelIds.Count, "model union across days");
        }
    }

    private static void ScannerScopesFiles()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_main", "req_main", "claude-opus-5", 10, 0, 0, 0, "text"),
                // Subagent work is recorded inline as a sidechain entry in current Claude Code.
                Sidechain("2026-08-12T10:01:00Z", "msg_side", "req_side", "claude-sonnet-5", 5, 0, 0, 0)
            });
            tree.WriteSubagentTranscript("alpha", "session-1", "agent-1", new[]
            {
                Assistant("2026-08-12T10:02:00Z", "msg_agent", "req_agent", "claude-sonnet-5", 3, 0, 0, 0, "text")
            });
            tree.WriteIgnoredSidecar("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:03:00Z", "msg_leak", "req_leak", "leaked-model", 100000, 0, 0, 0, "text")
            });

            var scan = Scan(tree, TimeZoneInfo.Utc);
            var day = scan.History.FindDay("2026-08-12");
            Equal(2, scan.Report.FilesSeen, "main and subagent transcripts only");
            Equal(18L, day.Tokens.ProcessedTokens, "sidechain and subagent usage is real usage");
            True(!day.Models.ContainsKey("leaked-model"), "tool-result sidecars are never opened");
            Equal(1L, day.SessionCount, "a subagent transcript is not its own session");
        }
    }

    private static void ScannerMergesRoots()
    {
        using (var first = new Scratch())
        using (var second = new Scratch())
        {
            first.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 0, 0, 0, "text")
            });
            second.WriteTranscript("beta", "session-2", new[]
            {
                Assistant("2026-08-12T11:00:00Z", "msg_2", "req_2", "claude-opus-5", 7, 0, 0, 0, "text")
            });

            var scan = TranscriptScanner.Scan(
                new[] { first.DataRoot, second.DataRoot },
                null,
                TimeZoneInfo.Utc,
                CancellationToken.None);
            Equal(17L, scan.History.FindDay("2026-08-12").Tokens.ProcessedTokens, "both roots contribute");
            Equal(2L, scan.History.FindDay("2026-08-12").SessionCount, "sessions from both roots");
            Equal(2, scan.Report.RootsSearched, "both roots searched");
        }
    }

    private static void ScannerHonoursTimeZone()
    {
        using (var tree = new Scratch())
        {
            // 02:30Z is still the previous day five hours west.
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T02:30:00Z", "msg_1", "req_1", "claude-opus-5", 10, 0, 0, 0, "text")
            });

            Equal(
                10L,
                Scan(tree, TimeZoneInfo.Utc).History.FindDay("2026-08-12").Tokens.ProcessedTokens,
                "UTC day");
            var shifted = Scan(tree, MinusFive).History;
            Equal(10L, shifted.FindDay("2026-08-11").Tokens.ProcessedTokens, "local day is the day before");
            True(shifted.FindDay("2026-08-12") == null, "no usage on the later local day");
        }
    }

    private static void ScannerIsReadOnly()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "text")
            });

            var before = HashTree(tree.DataRoot);
            Scan(tree, TimeZoneInfo.Utc);
            Equal(before, HashTree(tree.DataRoot), "the scanner never writes to the transcript tree");
        }
    }

    private static void ScannerRetainsNoContent()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("private-project", "session-1", new[]
            {
                "{\"type\":\"user\",\"timestamp\":\"2026-08-12T10:00:00Z\",\"message\":{\"role\":\"user\"," +
                "\"content\":\"PRIVATE_PROMPT\"}}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T10:00:01Z\",\"requestId\":\"req_1\"," +
                "\"sessionId\":\"PRIVATE_SESSION\",\"cwd\":\"C:/PRIVATE_PATH\",\"message\":{\"id\":\"PRIVATE_MESSAGE_ID\"," +
                "\"model\":\"claude-opus-5\",\"usage\":{\"input_tokens\":10},\"content\":[{\"type\":\"thinking\"," +
                "\"thinking\":\"PRIVATE_THINKING\"},{\"type\":\"tool_use\",\"input\":{\"file\":\"PRIVATE_TOOL_INPUT\"}}]}}"
            });

            var scan = Scan(tree, TimeZoneInfo.Utc);
            var exposed = new List<string>();
            CollectStrings(scan.History, exposed, new HashSet<object>(new ReferenceComparer()));
            CollectStrings(scan.Report, exposed, new HashSet<object>(new ReferenceComparer()));
            var joined = string.Join("\n", exposed);
            foreach (var canary in new[]
            {
                "PRIVATE_PROMPT", "PRIVATE_THINKING", "PRIVATE_TOOL_INPUT",
                "PRIVATE_SESSION", "PRIVATE_PATH", "PRIVATE_MESSAGE_ID", "private-project"
            })
            {
                True(!joined.Contains(canary, StringComparison.Ordinal), "not retained: " + canary);
            }
        }
    }

    private static void ScannerReportsBadLines()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscriptRaw("alpha", "session-1",
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 0, 0, 0, "text") + "\n" +
                "{not json at all}\n" +
                Assistant("2026-08-12T10:00:02Z", "msg_2", "req_2", "claude-opus-5", 5, 0, 0, 0, "text") + "\n");
            var settled = Scan(tree, TimeZoneInfo.Utc);
            Equal(15L, settled.History.FindDay("2026-08-12").Tokens.ProcessedTokens, "valid lines still count");
            True(
                settled.Report.Warnings.Any(warning => warning.Code == "transcript.line_invalid"),
                "a malformed middle line is reported");
            True(settled.Report.IsComplete, "a stable malformed line does not stall a refresh");

            // A transcript captured mid-write ends without a line break.
            tree.WriteTranscriptRaw("beta", "session-2",
                Assistant("2026-08-12T10:00:00Z", "msg_3", "req_3", "claude-opus-5", 10, 0, 0, 0, "text") + "\n" +
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T10:00:0");
            var partial = Scan(tree, TimeZoneInfo.Utc);
            True(
                partial.Report.Warnings.Any(warning => warning.Code == "transcript.partial_final_line"),
                "an unfinished final line is reported");
            True(!partial.Report.IsComplete, "an unfinished write marks the scan incomplete");
        }
    }

    // ------------------------------------------------------------- incremental

    private static void IndexReusesUnchangedFiles()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 0, 0, 0, "text")
            });

            var first = Scan(tree, TimeZoneInfo.Utc);
            Equal(1, first.Report.FilesParsed, "the first pass reads the transcript");

            var second = TranscriptScanner.Scan(
                new[] { tree.DataRoot },
                first.Index,
                TimeZoneInfo.Utc,
                CancellationToken.None);
            Equal(0, second.Report.FilesParsed, "an unchanged transcript is not reread");
            Equal(1, second.Report.FilesReused, "it is served from the index");
            Equal(
                10L,
                second.History.FindDay("2026-08-12").Tokens.ProcessedTokens,
                "cached totals match a full scan");

            tree.AppendToTranscript("alpha", "session-1",
                Assistant("2026-08-12T10:05:00Z", "msg_2", "req_2", "claude-opus-5", 5, 0, 0, 0, "text"));
            var third = TranscriptScanner.Scan(
                new[] { tree.DataRoot },
                second.Index,
                TimeZoneInfo.Utc,
                CancellationToken.None);
            Equal(1, third.Report.FilesParsed, "a changed transcript is reread");
            Equal(15L, third.History.FindDay("2026-08-12").Tokens.ProcessedTokens, "appended usage is added");
        }
    }

    // ------------------------------------------------------------------- store

    private static void StorePersistsIndex()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "text")
            });

            var storePath = Path.Combine(tree.Root, "store.json");
            var store = new UsageStore(storePath);
            store.Load(TimeZoneInfo.Utc);
            var first = store.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            Equal(1, first.Report.FilesParsed, "the first refresh reads the transcript");
            True(store.Save(), "the store is written");
            True(File.Exists(storePath), "the store file exists");

            var reopened = new UsageStore(storePath);
            reopened.Load(TimeZoneInfo.Utc);
            var second = reopened.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            Equal(0, second.Report.FilesParsed, "a reloaded index avoids rereading transcripts");
            var day = second.History.FindDay("2026-08-12");
            Equal(100L, day.Tokens.ProcessedTokens, "totals survive a restart");
            Equal(30L, day.Tokens.CacheReadTokens, "each category survives a restart");
            Equal(1L, day.SessionCount, "session counts survive a restart");
            Equal(1, day.Models.Count, "model rows survive a restart");
        }
    }

    private static void ArchiveOutlivesCleanup()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-07-01T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 10, 20, 30, 40, "text")
            });

            var storePath = Path.Combine(tree.Root, "store.json");
            var store = new UsageStore(storePath);
            store.Load(TimeZoneInfo.Utc);
            store.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            store.Save();

            // Claude Code prunes transcripts once they age past its cleanup period.
            tree.DeleteTranscript("alpha", "session-1");

            var reopened = new UsageStore(storePath);
            reopened.Load(TimeZoneInfo.Utc);
            var result = reopened.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            Equal(0, result.Report.FilesSeen, "the transcript is gone");
            Equal(
                100L,
                result.History.FindDay("2026-07-01").Tokens.ProcessedTokens,
                "the archive still reports the day");
            True(result.Scanned.FindDay("2026-07-01") == null, "the scan alone has nothing");
            Equal(1, result.ArchivedOnlyDays, "the day is reported as archive-only");

            reopened.Clear();
            var rebuilt = reopened.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            Equal(0, rebuilt.History.Days.Count, "rebuilding discards archived days");
        }
    }

    private static void ArchiveNeverShrinks()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 100, 0, 0, 0, "text"),
                Assistant("2026-08-12T10:01:00Z", "msg_2", "req_2", "claude-opus-5", 100, 0, 0, 0, "text")
            });

            var store = new UsageStore(Path.Combine(tree.Root, "store.json"));
            store.Load(TimeZoneInfo.Utc);
            Equal(
                200L,
                store.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None)
                    .History.FindDay("2026-08-12").Tokens.ProcessedTokens,
                "both responses counted");

            // A partially written or partially pruned transcript must not lower a recorded day.
            tree.WriteTranscript("alpha", "session-1", new[]
            {
                Assistant("2026-08-12T10:00:00Z", "msg_1", "req_1", "claude-opus-5", 100, 0, 0, 0, "text")
            });
            var second = store.Refresh(new[] { tree.DataRoot }, TimeZoneInfo.Utc, CancellationToken.None);
            Equal(
                200L,
                second.History.FindDay("2026-08-12").Tokens.ProcessedTokens,
                "the archived day keeps its higher total");
            Equal(
                100L,
                second.Scanned.FindDay("2026-08-12").Tokens.ProcessedTokens,
                "the raw scan reports what is on disk");
        }
    }

    // --------------------------------------------------------------- analytics

    private static void AnalyticsFilters()
    {
        var history = SampleHistory();

        var all = UsageAnalyticsCalculator.Calculate(history, new UsageFilter());
        Equal(740L, all.Tokens.ProcessedTokens, "all dates");
        Equal(3, all.ActiveDays, "active days");

        var bounded = UsageAnalyticsCalculator.Calculate(history, new UsageFilter("2026-08-02", "2026-08-03", null));
        Equal(640L, bounded.Tokens.ProcessedTokens, "both bounds are inclusive");
        Equal(2, bounded.Days.Count, "two days in range");

        var single = UsageAnalyticsCalculator.Calculate(history, new UsageFilter("2026-08-02", "2026-08-02", null));
        Equal(200L, single.Tokens.ProcessedTokens, "single day range");

        var opus = UsageAnalyticsCalculator.Calculate(
            history,
            new UsageFilter(null, null, new[] { "claude-opus-5" }));
        Equal(540L, opus.Tokens.ProcessedTokens, "model filtered tokens");
        Equal(23L, opus.TotalResponses, "model filtered responses");
        Equal(60L, opus.TotalMessages, "messages stay whole-day");
        Equal(6L, opus.TotalSessions, "sessions stay whole-day");
        True(opus.ActivityIsWholeDay, "the whole-day caveat is exposed");
        Equal(1, opus.Models.Count, "one model row");

        var unknown = UsageAnalyticsCalculator.Calculate(history, new UsageFilter(null, null, new[] { "nope" }));
        True(unknown.Tokens.IsEmpty, "an unmatched model yields no tokens");

        var outside = UsageAnalyticsCalculator.Calculate(history, new UsageFilter("2027-01-01", "2027-01-31", null));
        True(outside.IsEmpty, "a range with no data is empty");
        Equal(0, outside.Days.Count, "no rows outside the range");

        Throws<ArgumentException>(() => new UsageFilter("2026-08-03", "2026-08-02", null), "reversed range rejected");
        Throws<ArgumentException>(() => new UsageFilter("2026-13-01", null, null), "invalid date rejected");
    }

    private static void AnalyticsSummarise()
    {
        var analytics = UsageAnalyticsCalculator.Calculate(SampleHistory(), new UsageFilter());
        Equal("2026-08-03", analytics.PeakDay(TokenMetric.Processed).Date, "busiest day");
        Equal(246L, analytics.AveragePerActiveDay(TokenMetric.Processed), "average per active day");
        Equal(140L, analytics.Tokens.InputOutputTokens, "input plus output");
        Equal(2, analytics.Models.Count, "model rows ordered");
        Equal("claude-opus-5", analytics.Models[0].ModelId, "largest model first");
        Equal(0L, UsageAnalyticsCalculator.Calculate(UsageHistory.Empty, new UsageFilter())
            .AveragePerActiveDay(TokenMetric.Processed), "no divide by zero on empty history");
    }

    // ------------------------------------------------------------------ pricing

    private static PricingCatalog TestPricing()
    {
        // Deliberately out of order: ordering is by effective date, not entry order.
        return new PricingCatalog(new[]
        {
            new ModelPrice("alpha", "2026-08-04", 100, 200, 400, 300),
            new ModelPrice("alpha", null, 1, 2, 4, 3),
            new ModelPrice("alpha", "2026-08-02", 10, 20, 40, 30)
        });
    }

    private static UsageHistory PricingHistory()
    {
        return new UsageHistory(new UsageDay[]
        {
            new SampleDay("2026-08-01", "alpha", 1000000, 2000000, 3000000, 4000000, 1, 0, 1, 1)
                .WithExtraModel("beta", 1000000, 0, 0, 0, 1, 0),
            new SampleDay("2026-08-02", "alpha", 1000000, 2000000, 3000000, 4000000, 1, 0, 1, 1),
            new SampleDay("2026-08-03", "alpha", 1000000, 2000000, 3000000, 4000000, 1, 0, 1, 1)
        });
    }

    private static void PricingCalculatesCategories()
    {
        var cost = TestPricing().Calculate("alpha", "2026-08-01", new TokenTotals(1000000, 2000000, 3000000, 4000000));
        Equal(1M, cost.Input.KnownUsd, "input per million");
        Equal(4M, cost.Output.KnownUsd, "output per million");
        Equal(9M, cost.CacheRead.KnownUsd, "cache read rate");
        Equal(16M, cost.CacheWrite.KnownUsd, "cache creation uses cache write rate");
        Equal(5M, cost.InputOutput.KnownUsd, "input plus output spend");
        Equal(30M, cost.Total.KnownUsd, "total spend");
        Equal(10000000L, cost.Total.PricedTokens, "coverage");
        var tiny = new PricingCatalog(new[] { new ModelPrice("alpha", null, 0.000001M, 0, 0, 0) });
        Equal(0.000000000001M, tiny.Calculate("alpha", "2026-08-01", new TokenTotals(1, 0, 0, 0)).Total.KnownUsd,
            "small prices retain decimal precision before presentation");
        var huge = new PricingCatalog(new[] { new ModelPrice("alpha", null, 1000000, 1000000, 1000000, 1000000) });
        Equal((decimal)long.MaxValue * 4, huge.Calculate("alpha", "2026-08-01",
            new TokenTotals(long.MaxValue, long.MaxValue, long.MaxValue, long.MaxValue)).Total.KnownUsd,
            "large counters do not overflow money calculation");
        Equal(0M, PricingCatalog.Empty.Calculate("missing", "2026-08-01", TokenTotals.Zero).Total.KnownUsd, "no usage has zero cost");
    }

    private static void PricingEffectiveDates()
    {
        var catalog = TestPricing();
        Equal(1M, catalog.Find("alpha", "2020-01-01").Input, "baseline covers historical usage");
        Equal(10M, catalog.Find("alpha", "2026-08-02").Input, "effective date is inclusive");
        Equal(10M, catalog.Find("alpha", "2026-08-03").Input, "rate continues until the next change");
        Equal(100M, catalog.Find("alpha", "2026-08-04").Input, "later change takes precedence");
        var datedOnly = new PricingCatalog(catalog.Prices.Where(value => value.EffectiveDate != null));
        True(datedOnly.Find("alpha", "2026-08-01") == null, "no backfill before first dated rate");
        True(catalog.Find("Alpha", "2026-08-01") == null, "model identifiers match exactly");
        var tokens = new TokenTotals(1, 2, 3, 4);
        var missing = datedOnly.Calculate("alpha", "2026-08-01", tokens);
        Equal(10L, missing.Total.UnpricedTokens, "unpriced before effective date");
        Equal(0L, missing.Total.PricedTokens, "unpriced tokens do not become free");
        var zero = new PricingCatalog(new[] { new ModelPrice("alpha", null, 0, 0, 0, 0) }).Calculate("alpha", "2026-08-01", tokens);
        Equal(0M, zero.Total.KnownUsd, "explicit free pricing");
        Equal(10L, zero.Total.PricedTokens, "free is still priced");
    }

    private static void PricingFiltersAndCoverage()
    {
        var history = PricingHistory();
        var all = UsageAnalyticsCalculator.Calculate(history, new UsageFilter(), TestPricing());
        Equal(630M, all.Spend.Total.KnownUsd, "prices selected per day before aggregation");
        Equal(1000000L, all.Spend.Total.UnpricedTokens, "missing model retained in coverage");
        True(all.Spend.Total.IsPartial, "mixed total is explicitly partial");
        True(all.Spend.Input.IsPartial, "category coverage is independent");
        True(!all.Spend.Output.IsPartial, "unused unpriced category does not contaminate coverage");
        Equal(30M, all.Days[0].Spend.Total.KnownUsd, "daily spend before change");
        Equal(300M, all.Days[1].Spend.Total.KnownUsd, "daily spend on change date");
        Equal(630M, all.Models.Single(value => value.ModelId == "alpha").Spend.Total.KnownUsd, "model span includes all rates");
        Equal(all.Spend.Total.KnownUsd, all.Days.Sum(value => value.Spend.Total.KnownUsd), "daily sum reconciles");
        Equal(all.Spend.Total.KnownUsd, all.Models.Sum(value => value.Spend.Total.KnownUsd), "model sum reconciles");
        var filtered = UsageAnalyticsCalculator.Calculate(history, new UsageFilter("2026-08-01", "2026-08-02", new[] { "alpha" }), TestPricing());
        Equal(330M, filtered.Spend.Total.KnownUsd, "range and model filter both apply to spend");
        Equal(0L, filtered.Spend.Total.UnpricedTokens, "excluded model cannot contaminate coverage");
        var empty = UsageAnalyticsCalculator.Calculate(history, new UsageFilter(null, null, new[] { "unknown" }), TestPricing());
        Equal(0M, empty.Spend.Total.KnownUsd, "empty filter spend");
        Equal(0L, empty.Spend.Total.UnpricedTokens, "empty filter coverage");
        var unpriced = UsageAnalyticsCalculator.Calculate(history, new UsageFilter());
        Equal(31000000L, unpriced.Spend.Total.UnpricedTokens, "no configuration marks all usage unpriced");
        Equal(31000000L, all.Tokens.ProcessedTokens, "pricing never changes token counts");
    }

    private static void PricingValidates()
    {
        Throws<ArgumentException>(() => new ModelPrice(" ", null, 1, 1, 1, 1), "empty model rejected");
        Throws<ArgumentException>(() => new ModelPrice("a\nb", null, 1, 1, 1, 1), "control characters rejected");
        Throws<ArgumentException>(() => new ModelPrice("a", "2026-02-29", 1, 1, 1, 1), "invalid date rejected");
        Throws<ArgumentException>(() => new ModelPrice("a", null, -1, 1, 1, 1), "negative rate rejected");
        Throws<ArgumentException>(() => new ModelPrice("a", null, 1, 1, 1000001, 1), "excessive rate rejected");
        Throws<ArgumentException>(() => new ModelPrice("a", null, 1, 1, 1, 0.0000001M), "precision limit explicit");
        Throws<ArgumentException>(() => new PricingCatalog(new[]
        {
            new ModelPrice("a", null, 1, 1, 1, 1), new ModelPrice(" a ", "", 2, 2, 2, 2)
        }), "duplicate baseline rejected after identifier normalization");
        Throws<ArgumentException>(() => new PricingCatalog(new[]
        {
            new ModelPrice("a", "2026-08-01", 1, 1, 1, 1), new ModelPrice("a", "2026-08-01", 2, 2, 2, 2)
        }), "duplicate date rejected");
        Equal("manual-model-id", new ModelPrice(" manual-model-id ", null, 1, 1, 1, 1).ModelId, "manual model accepted");
    }

    private static void PricingPersists()
    {
        using (var tree = new Scratch())
        {
            var path = Path.Combine(tree.Root, "settings", "pricing.xml");
            Equal(0, PricingStore.Load(path).Prices.Count, "first run has no configured models");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var catalog = new PricingCatalog(TestPricing().Prices.Concat(new[]
                {
                    new ModelPrice("manual<&>é", null, 1.234567M, 0, 2.3M, 0.01M)
                }));
                PricingStore.Save(path, catalog);
                var reloaded = PricingStore.Load(path);
                Equal(4, reloaded.Prices.Count, "all pricing periods persisted");
                Equal(1.234567M, reloaded.Find("manual<&>é", "2026-08-01").Input, "culture and escaped model round trip");
                Equal(10M, reloaded.Find("alpha", "2026-08-02").Input, "effective dates persisted");
                PricingStore.Save(path, new PricingCatalog(new[] { new ModelPrice("alpha", null, 7, 0, 0, 0) }));
                Equal(7M, PricingStore.Load(path).Find("alpha", "2020-01-01").Input, "existing file replaced with edited rates");
                PricingStore.Save(path, PricingCatalog.Empty);
                Equal(0, PricingStore.Load(path).Prices.Count, "deletion persists");
                File.WriteAllText(path, "<pricing schema=\"9\" />");
                Throws<FormatException>(() => PricingStore.Load(path), "unsupported file reported");
                Equal("<pricing schema=\"9\" />", File.ReadAllText(path), "failed load preserves file");
                File.WriteAllText(path, "<pricing schema=\"1\" currency=\"USD\" unit=\"1000000\"><price model=\"a\" /></pricing>");
                Throws<FormatException>(() => PricingStore.Load(path), "missing rates cannot silently become free");
                File.WriteAllText(path, "<!DOCTYPE pricing SYSTEM \"https://example.invalid/pricing.dtd\"><pricing />");
                Throws<System.Xml.XmlException>(() => PricingStore.Load(path), "external DTD prohibited");
                Throws<IOException>(() => PricingStore.Save(tree.Root, TestPricing()), "save failure reported to caller");
            }
            finally { CultureInfo.CurrentCulture = culture; }
        }
    }

    private static void PricingRecalculatesArchive()
    {
        using (var tree = new Scratch())
        {
            tree.WriteTranscript("alpha", "session", new[]
            {
                Assistant("2026-08-01T04:00:00Z", "msg_p", "req_p", "alpha", 1000000, 0, 0, 0, "text")
            });
            var store = new UsageStore(Path.Combine(tree.Root, "archive.json"));
            store.Load(MinusFive);
            store.Refresh(new[] { tree.DataRoot }, MinusFive, CancellationToken.None);
            True(store.Save(), "priced fixture archive saved");
            tree.DeleteTranscript("alpha", "session");
            var reopened = new UsageStore(Path.Combine(tree.Root, "archive.json"));
            reopened.Load(MinusFive);
            var history = reopened.Refresh(new[] { tree.DataRoot }, MinusFive, CancellationToken.None).History;
            var dated = new PricingCatalog(new[] { new ModelPrice("alpha", "2026-08-01", 8, 0, 0, 0) });
            var first = UsageAnalyticsCalculator.Calculate(history, new UsageFilter(), dated);
            Equal("2026-07-31", first.Days[0].Date, "effective prices follow archived local day, not UTC");
            Equal(1000000L, first.Spend.Total.UnpricedTokens, "future rate does not backfill history");
            var baseline = new PricingCatalog(new[] { new ModelPrice("alpha", null, 4, 0, 0, 0) });
            Equal(4M, UsageAnalyticsCalculator.Calculate(history, new UsageFilter(), baseline).Spend.Total.KnownUsd,
                "undated edit recalculates after transcript cleanup");
            Equal(1000000L, history.FindDay("2026-07-31").Tokens.InputTokens, "recorded usage is unchanged");
        }
    }

    // ------------------------------------------------------------------ alerts

    private static void AlertsFireOncePerLevel()
    {
        var settings = new AlertSettings
        {
            Enabled = true,
            DailyLimitTokens = 1000,
            WarnPercent = 80,
            Metric = TokenMetric.Processed
        };

        Equal(800L, settings.WarnTokens, "warning threshold");
        Equal(AlertLevel.None, UsageAlertEvaluator.Evaluate(settings, "2026-08-12", 799, new AlertState()).Level, "below warning");

        var warned = UsageAlertEvaluator.Evaluate(settings, "2026-08-12", 800, new AlertState());
        Equal(AlertLevel.Warning, warned.Level, "at the warning level");
        True(warned.ShouldNotify, "the warning is announced");
        Equal(80, warned.Percent, "percentage of the threshold");

        var repeat = UsageAlertEvaluator.Evaluate(
            settings,
            "2026-08-12",
            900,
            new AlertState("2026-08-12", AlertLevel.Warning));
        Equal(AlertLevel.Warning, repeat.Level, "still at the warning level");
        True(!repeat.ShouldNotify, "the same level is not announced twice");

        var reached = UsageAlertEvaluator.Evaluate(
            settings,
            "2026-08-12",
            1000,
            new AlertState("2026-08-12", AlertLevel.Warning));
        Equal(AlertLevel.Limit, reached.Level, "at the threshold");
        True(reached.ShouldNotify, "escalation is announced");

        var nextDay = UsageAlertEvaluator.Evaluate(
            settings,
            "2026-08-13",
            800,
            new AlertState("2026-08-12", AlertLevel.Limit));
        True(nextDay.ShouldNotify, "a new day starts over");

        var off = settings.Clone();
        off.Enabled = false;
        True(!UsageAlertEvaluator.Evaluate(off, "2026-08-12", 5000, new AlertState()).ShouldNotify, "disabled stays quiet");

        var noLimit = settings.Clone();
        noLimit.DailyLimitTokens = 0;
        True(!noLimit.IsActive, "a zero threshold is inactive");

        var inputOutput = settings.Clone();
        inputOutput.Metric = TokenMetric.InputOutput;
        var tokens = new TokenTotals(400, 500, 5000, 5000);
        Equal(900L, tokens.Select(inputOutput.Metric), "input + output metric");
        Equal(10900L, tokens.Select(TokenMetric.Processed), "processed metric");
    }

    private static void SpendAlerts()
    {
        const string date = "2026-08-12";
        var settings = new AlertSettings
        {
            Enabled = true, UseSpend = true, DailyLimitUsd = 0.01M, WarnPercent = 80,
            DailyLimitTokens = 999999, Metric = TokenMetric.InputOutput
        };
        var prices = new PricingCatalog(new[] { new ModelPrice("alpha", null, 1, 2, 4, 3) });
        SpendAmount Spend(long tokens) => prices.Calculate("alpha", date, new TokenTotals(tokens, 0, 0, 0)).Total;
        AlertEvaluation Evaluate(SpendAmount spend, AlertState state = null) =>
            UsageAlertEvaluator.EvaluateSpend(settings, date, spend, state);

        True(settings.IsActive, "USD limit activates independently of token metric");
        var copy = settings.Clone();
        True(copy.UseSpend, "cloning preserves spend mode");
        Equal(0.01M, copy.DailyLimitUsd, "cloning preserves USD precision");
        Equal(AlertLevel.None, Evaluate(Spend(7999)).Level, "no rounding up to warning");
        var warning = Evaluate(Spend(8000));
        Equal(AlertLevel.Warning, warning.Level, "exact USD warning boundary");
        Equal(80, warning.Percent, "USD percentage");
        Equal(0.008M, warning.Spend.KnownUsd, "evaluation retains spend");
        Equal(0.01M, warning.LimitUsd, "evaluation retains dollar limit");
        True(warning.ShouldNotify, "first USD warning notifies");
        True(!Evaluate(Spend(9000), new AlertState(date, AlertLevel.Warning)).ShouldNotify, "USD warning stays quiet");
        Equal(AlertLevel.Warning, Evaluate(Spend(9999)).Level, "no rounding up to limit");
        True(Evaluate(Spend(10000), new AlertState(date, AlertLevel.Warning)).ShouldNotify, "USD limit escalation notifies");
        Equal(AlertLevel.Limit, Evaluate(Spend(10000)).Level, "exact dollar limit");
        Equal(150, Evaluate(Spend(15000)).Percent, "spend over threshold");
        True(!Evaluate(Spend(15000), new AlertState(date, AlertLevel.Limit)).ShouldNotify, "USD limit remains quiet on refresh");
        True(UsageAlertEvaluator.EvaluateSpend(settings, "2026-08-13", Spend(8000),
            new AlertState(date, AlertLevel.Limit)).ShouldNotify, "next local day rearms USD warning");

        var unpriced = PricingCatalog.Empty.Calculate("missing", date, new TokenTotals(1000, 0, 0, 0)).Total;
        var missing = Evaluate(unpriced);
        True(!missing.ShouldNotify, "unconfigured rates do not trigger spend alerts");
        True(missing.Message.Contains("Prices are missing"), "missing rates are explicit");
        var partial = Evaluate(Spend(10000).Add(unpriced));
        True(partial.ShouldNotify, "known subtotal can reach a threshold");
        True(partial.Message.Contains("known subtotal"), "partial alert states coverage");
        var cacheOnly = prices.Calculate("alpha", date, new TokenTotals(0, 0, 2000, 1000)).Total;
        Equal(AlertLevel.Limit, Evaluate(cacheOnly).Level, "spend includes cache even with stored IO metric");
        var free = new PricingCatalog(new[] { new ModelPrice("free", null, 0, 0, 0, 0) })
            .Calculate("free", date, new TokenTotals(100000, 0, 0, 0)).Total;
        True(!Evaluate(free).ShouldNotify, "explicit free rates stay quiet");
        True(!Evaluate(free).Message.Contains("missing"), "zero prices are configured");
        True(!Evaluate(SpendAmount.Zero).ShouldNotify, "empty day stays quiet");

        settings.DailyLimitUsd = 0.000001M;
        Equal(AlertLevel.Limit, Evaluate(Spend(1)).Level, "six-place dollar threshold");
        Equal(int.MaxValue, Evaluate(Spend(long.MaxValue)).Percent, "large percentage saturates without overflow");
        settings.WarnPercent = 100;
        Equal(AlertLevel.Limit, Evaluate(Spend(1)).Level, "100 percent warning goes straight to limit");
        settings.Enabled = false;
        True(!Evaluate(Spend(10000)).ShouldNotify, "disabled spend alerts stay quiet");
        settings.Enabled = true;
        settings.DailyLimitUsd = 0;
        True(!settings.IsActive && !Evaluate(Spend(10000)).ShouldNotify, "zero USD limit is inactive despite token limit");
        settings.DailyLimitUsd = -1;
        True(!settings.IsActive, "negative USD limit is inactive");

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            settings.DailyLimitUsd = 0.01M;
            True(Evaluate(Spend(8000)).Message.Contains("USD $0.008"), "USD wording is culture-independent and precise");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }

    // --------------------------------------------------------------------- pdf

    private static void PdfIsValid()
    {
        var analytics = UsageAnalyticsCalculator.Calculate(SampleHistory(), new UsageFilter());
        var options = new UsageReportOptions
        {
            RangeLabel = "2026-08-01 to 2026-08-03",
            ModelLabel = "All models",
            DailyThresholdTokens = 250,
            DataLocations = new List<string> { @"C:\Users\tester\.claude" },
            ArchivedOnlyDays = 1
        };

        using (var stream = new MemoryStream())
        {
            UsageReportWriter.Write(stream, analytics, options);
            var bytes = stream.ToArray();
            var text = Encoding.Latin1.GetString(bytes);
            True(text.StartsWith("%PDF-1.4", StringComparison.Ordinal), "header");
            True(text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "trailer");
            True(text.Contains("/Type /Catalog"), "catalog");
            True(text.Contains("/BaseFont /Helvetica-Bold"), "bold font resource");
            True(text.Contains("(2026-08-03)"), "a daily row is drawn");
            True(text.Contains("(claude-opus-5)"), "a model row is drawn");
            True(!text.Contains("PRIVATE"), "no unexpected content");
            AssertXrefResolves(text);
            AssertContentWithinPage(text);
            Equal(2, CountPages(text), "summary page plus daily detail");
        }
    }

    private static void PdfPaginates()
    {
        var days = new List<UsageDay>();
        for (var index = 0; index < 200; index++)
        {
            var date = new DateTime(2026, 1, 1).AddDays(index).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            days.Add(Day(date, "claude-opus-5", 1000 + index, 10, 20, 30, 2, 1, 5, 1));
        }

        var analytics = UsageAnalyticsCalculator.Calculate(new UsageHistory(days), new UsageFilter());
        using (var stream = new MemoryStream())
        {
            UsageReportWriter.Write(stream, analytics, new UsageReportOptions());
            var text = Encoding.Latin1.GetString(stream.ToArray());
            True(CountPages(text) >= 6, "long ranges paginate");
            AssertXrefResolves(text);
            AssertContentWithinPage(text);
            True(text.Contains("(Page 1 of "), "pages are numbered against the final total");
        }

        using (var stream = new MemoryStream())
        {
            UsageReportWriter.Write(
                stream,
                UsageAnalyticsCalculator.Calculate(UsageHistory.Empty, new UsageFilter()),
                new UsageReportOptions());
            var text = Encoding.Latin1.GetString(stream.ToArray());
            True(text.Contains("No usage was recorded in this range."), "an empty range still renders");
            AssertXrefResolves(text);
        }
    }

    private static int CountPages(string pdf)
    {
        var count = 0;
        var index = 0;
        while ((index = pdf.IndexOf("/Type /Page ", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 5;
        }

        return count;
    }

    /// <summary>
    /// Nothing may be drawn outside the printable area. This is the guard rail for the
    /// hand-rolled paginator: it checks every page, including continuation pages.
    /// </summary>
    private static void AssertContentWithinPage(string pdf)
    {
        const double PageHeight = 792;
        const double PageWidth = 612;
        var texts = 0;

        foreach (Match match in Regex.Matches(pdf, @"1 0 0 1 (-?[\d.]+) (-?[\d.]+) Tm"))
        {
            var x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var y = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            texts++;
            True(x >= 50 && x < PageWidth - 20, "text x inside the page: " + x);
            True(y >= 20 && y <= PageHeight - 54, "text baseline inside the page: " + y);
        }

        True(texts > 20, "the report draws text");

        foreach (Match match in Regex.Matches(pdf, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re"))
        {
            var x = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var y = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            var width = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            var height = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
            True(width >= 0 && height >= 0, "no inverted rectangles");
            True(x >= 50 && x + width <= PageWidth - 46, "rectangle inside the horizontal margins");
            True(y >= 20 && y + height <= PageHeight - 50, "rectangle inside the vertical margins");
        }
    }

    /// <summary>Every cross-reference offset must land on its object header.</summary>
    private static void AssertXrefResolves(string pdf)
    {
        var startIndex = pdf.LastIndexOf("startxref", StringComparison.Ordinal);
        True(startIndex > 0, "startxref present");
        var digits = new string(pdf.Substring(startIndex + 9)
            .SkipWhile(character => !char.IsDigit(character))
            .TakeWhile(char.IsDigit)
            .ToArray());
        var offset = int.Parse(digits, CultureInfo.InvariantCulture);
        True(pdf.Substring(offset).StartsWith("xref", StringComparison.Ordinal), "startxref points at the table");

        var lines = pdf.Substring(offset).Split('\n');
        var total = int.Parse(lines[1].Trim().Split(' ')[1], CultureInfo.InvariantCulture);
        for (var number = 1; number < total; number++)
        {
            var entryOffset = int.Parse(lines[number + 2].Substring(0, 10), CultureInfo.InvariantCulture);
            var expected = number.ToString(CultureInfo.InvariantCulture) + " 0 obj";
            True(
                pdf.Substring(entryOffset, expected.Length) == expected,
                "object " + number + " is where the table says");
        }
    }

    // ------------------------------------------------------------------ shared

    private static UsageHistory SampleHistory()
    {
        return new UsageHistory(new List<UsageDay>
        {
            Day("2026-08-01", "claude-opus-5", 10, 20, 30, 40, 5, 3, 12, 2),
            Day("2026-08-02", "claude-opus-5", 20, 30, 50, 100, 8, 5, 20, 2),
            Day("2026-08-03", "claude-opus-5", 20, 20, 100, 100, 10, 6, 28, 2)
                .WithExtraModel("claude-haiku-4-5", 10, 10, 90, 90, 4, 2)
        });
    }

    private static SampleDay Day(
        string date,
        string modelId,
        long input,
        long output,
        long cacheRead,
        long cacheCreation,
        long responses,
        long toolCalls,
        long messages,
        long sessions)
    {
        return new SampleDay(date, modelId, input, output, cacheRead, cacheCreation, responses, toolCalls, messages, sessions);
    }

    /// <summary>Builds a <see cref="UsageDay"/> through its public constructor.</summary>
    private sealed class SampleDay
    {
        private readonly Dictionary<string, ModelUsage> _models = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
        private readonly string _date;
        private readonly long _messages;
        private readonly long _sessions;

        internal SampleDay(
            string date,
            string modelId,
            long input,
            long output,
            long cacheRead,
            long cacheCreation,
            long responses,
            long toolCalls,
            long messages,
            long sessions)
        {
            _date = date;
            _messages = messages;
            _sessions = sessions;
            _models[modelId] = new ModelUsage(
                modelId,
                new TokenTotals(input, output, cacheRead, cacheCreation),
                responses,
                toolCalls,
                0);
        }

        internal SampleDay WithExtraModel(
            string modelId,
            long input,
            long output,
            long cacheRead,
            long cacheCreation,
            long responses,
            long toolCalls)
        {
            _models[modelId] = new ModelUsage(
                modelId,
                new TokenTotals(input, output, cacheRead, cacheCreation),
                responses,
                toolCalls,
                0);
            return this;
        }

        public static implicit operator UsageDay(SampleDay day)
        {
            return new UsageDay(day._date, day._models, day._sessions, day._messages);
        }
    }

    private static TranscriptScanResult Scan(Scratch tree, TimeZoneInfo zone)
    {
        return TranscriptScanner.Scan(new[] { tree.DataRoot }, null, zone, CancellationToken.None);
    }

    private static UsageDay ScanDay(Scratch tree, string date)
    {
        return Scan(tree, TimeZoneInfo.Utc).History.FindDay(date);
    }

    private static string User(string timestamp)
    {
        return "{\"type\":\"user\",\"timestamp\":\"" + timestamp +
               "\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}";
    }

    private static string Assistant(
        string timestamp,
        string messageId,
        string requestId,
        string model,
        long input,
        long output,
        long cacheRead,
        long cacheCreation,
        string blockType)
    {
        return "{\"type\":\"assistant\",\"timestamp\":\"" + timestamp + "\",\"requestId\":\"" + requestId +
               "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\",\"usage\":{" +
               "\"input_tokens\":" + input + ",\"output_tokens\":" + output +
               ",\"cache_read_input_tokens\":" + cacheRead +
               ",\"cache_creation_input_tokens\":" + cacheCreation + "},\"content\":[{\"type\":\"" +
               blockType + "\"}]}}";
    }

    private static string Sidechain(
        string timestamp,
        string messageId,
        string requestId,
        string model,
        long input,
        long output,
        long cacheRead,
        long cacheCreation)
    {
        return "{\"type\":\"assistant\",\"isSidechain\":true,\"timestamp\":\"" + timestamp + "\",\"requestId\":\"" +
               requestId + "\",\"message\":{\"id\":\"" + messageId + "\",\"model\":\"" + model + "\",\"usage\":{" +
               "\"input_tokens\":" + input + ",\"output_tokens\":" + output +
               ",\"cache_read_input_tokens\":" + cacheRead +
               ",\"cache_creation_input_tokens\":" + cacheCreation + "},\"content\":[{\"type\":\"text\"}]}}";
    }

    private sealed class Scratch : IDisposable
    {
        internal Scratch()
        {
            Root = Path.Combine(Path.GetTempPath(), "ClaudeUsageTests-" + Guid.NewGuid().ToString("N"));
            DataRoot = Path.Combine(Root, "data");
            Directory.CreateDirectory(Path.Combine(DataRoot, "projects"));
        }

        internal string Root { get; }

        internal string DataRoot { get; }

        internal void WriteTranscript(string project, string session, IEnumerable<string> lines)
        {
            WriteTranscriptRaw(project, session, string.Join("\n", lines) + "\n");
        }

        internal void WriteTranscriptRaw(string project, string session, string content)
        {
            var directory = Path.Combine(DataRoot, "projects", project);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, session + ".jsonl"), content, new UTF8Encoding(false));
        }

        internal void AppendToTranscript(string project, string session, string line)
        {
            var path = Path.Combine(DataRoot, "projects", project, session + ".jsonl");
            // Guarantee a different last-write time even on a coarse filesystem clock.
            var existing = File.ReadAllText(path, Encoding.UTF8);
            File.WriteAllText(path, existing + line + "\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(1));
        }

        internal void DeleteTranscript(string project, string session)
        {
            File.Delete(Path.Combine(DataRoot, "projects", project, session + ".jsonl"));
        }

        internal void WriteSubagentTranscript(string project, string session, string agent, IEnumerable<string> lines)
        {
            var directory = Path.Combine(DataRoot, "projects", project, session, "subagents");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, agent + ".jsonl"),
                string.Join("\n", lines) + "\n",
                new UTF8Encoding(false));
        }

        internal void WriteIgnoredSidecar(string project, string session, IEnumerable<string> lines)
        {
            var directory = Path.Combine(DataRoot, "projects", project, session, "tool-results");
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                Path.Combine(directory, "results.jsonl"),
                string.Join("\n", lines) + "\n",
                new UTF8Encoding(false));
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, true);
            }
            catch
            {
                // A leftover temp folder is harmless.
            }
        }
    }

    private static string HashTree(string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(value => value, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            var nameBytes = Encoding.UTF8.GetBytes(relative);
            sha.TransformBlock(nameBytes, 0, nameBytes.Length, null, 0);
            var bytes = File.ReadAllBytes(file);
            sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    private static void CollectStrings(object value, IList<string> strings, ISet<object> visited)
    {
        if (value == null) return;
        if (value is string text)
        {
            strings.Add(text);
            return;
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is decimal || value is DateTime || value is DateTimeOffset) return;
        if (!type.IsValueType && !visited.Add(value)) return;
        if (value is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                CollectStrings(entry.Key, strings, visited);
                CollectStrings(entry.Value, strings, visited);
            }

            return;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable) CollectStrings(item, strings, visited);
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => property.GetIndexParameters().Length == 0))
        {
            CollectStrings(property.GetValue(value), strings, visited);
        }
    }

    private static void True(bool condition, string name)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException("Assertion failed: " + name);
    }

    private static void Equal<T>(T expected, T actual, string name)
    {
        _assertions++;
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                "Assertion failed: " + name + "; expected " + expected + ", got " + actual);
        }
    }

    private static void Throws<T>(Action action, string name) where T : Exception
    {
        _assertions++;
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new InvalidOperationException("Assertion failed: " + name + "; expected " + typeof(T).Name);
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        public new bool Equals(object x, object y)
        {
            return ReferenceEquals(x, y);
        }

        public int GetHashCode(object obj)
        {
            return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
        }
    }
}
