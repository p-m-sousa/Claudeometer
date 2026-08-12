using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using ClaudeUsage.Core;

internal static class Program
{
    private static int _assertions;

    private static int Main()
    {
        try
        {
            TestHistoricalParsingAndFiltering();
            TestCompatibilityAndFaultTolerance();
            TestLargeCountersAndDuplicateRows();
            TestCurrentDayCollector();
            Console.WriteLine("PASS: " + _assertions + " assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("FAIL: " + error);
            return 1;
        }
    }

    private static void TestHistoricalParsingAndFiltering()
    {
        var result = ParseFixture("stats-cache-basic.json");
        True(result.IsUsable, "basic cache is usable");
        Equal(5, result.Document.Version, "schema version");
        Equal(5, result.Document.DailyModelTokensVersion, "daily token schema version");
        True(result.Document.DailyTokensIncludeCache, "v5 daily tokens include cache categories");
        Equal("2026-08-02", result.Document.LastComputedDate, "computed date");
        Equal(3, result.Document.DailyActivity.Count, "daily activity rows");
        Equal(3, result.Document.ModelIds.Count, "dynamic model union");

        var all = UsageAnalyticsCalculator.Calculate(
            result.Document,
            new UsageFilter("2026-08-02", "2026-08-03", null));
        Equal(310L, all.TotalTokens, "inclusive range processed tokens");
        Equal(11L, all.TotalMessages, "inclusive range messages");
        Equal(3L, all.TotalSessions, "inclusive range sessions");
        Equal(13L, all.TotalToolCalls, "inclusive range tools");
        Equal(2, all.ActiveDays, "active days");

        var haiku = UsageAnalyticsCalculator.Calculate(
            result.Document,
            new UsageFilter(
                "2026-08-02",
                "2026-08-03",
                new[] { "claude-haiku-3-5-20241022" }));
        Equal(40L, haiku.TotalTokens, "model-filtered tokens");
        Equal(11L, haiku.TotalMessages, "activity remains all-model");
        Equal(3L, haiku.TotalSessions, "sessions remain all-model");
        Equal(13L, haiku.TotalToolCalls, "tools remain all-model");
        True(haiku.ActivityIncludesAllModels, "activity limitation disclosed");

        var boundary = UsageAnalyticsCalculator.Calculate(
            result.Document,
            new UsageFilter("2026-08-02", "2026-08-02", null));
        Equal(230L, boundary.TotalTokens, "both date bounds are inclusive");
        Throws<ArgumentException>(
            () => new UsageFilter("2026-08-03", "2026-08-02", null),
            "reversed date range rejected");
    }

    private static void TestCompatibilityAndFaultTolerance()
    {
        var legacy = ParseFixture("stats-cache-v1-minimal.json");
        True(legacy.IsUsable, "legacy v1 usable");
        Equal(25L, UsageAnalyticsCalculator.Calculate(legacy.Document, new UsageFilter()).TotalTokens, "legacy tokens");
        Equal(null, legacy.Document.ModelUsage.Values.Single().CostUsd, "missing cost stays unknown");

        var future = ParseFixture("stats-cache-future-schema.json");
        True(future.IsUsable, "future schema best effort");
        True(future.Warnings.Any(value => value.Code == "schema.future_version"), "future schema warning");
        Equal(110L, UsageAnalyticsCalculator.Calculate(future.Document, new UsageFilter()).TotalTokens, "future known fields");
        True(future.Document.ModelIds.Contains("claude-future-9-20990101"), "unknown model retained");

        var empty = ParseFixture("stats-cache-empty.json");
        True(empty.IsUsable, "empty cache is valid");
        True(UsageAnalyticsCalculator.Calculate(empty.Document, new UsageFilter()).IsEmpty, "empty analytics state");

        var truncated = StatsCacheParser.Parse("{\"version\":3,\"dailyActivity\":[");
        True(!truncated.IsUsable, "truncated JSON rejected without exception");
        True(truncated.Warnings.Any(value => value.Severity == WarningSeverity.Error), "truncated warning severity");

        var malformedRows = StatsCacheParser.Parse(
            "{\"version\":3,\"dailyActivity\":[null,{\"date\":\"2026-08-01\",\"messageCount\":2,\"sessionCount\":1,\"toolCallCount\":3}],\"dailyModelTokens\":[],\"modelUsage\":{},\"totalSessions\":1,\"totalMessages\":2,\"hourCounts\":{}}");
        True(malformedRows.IsUsable, "malformed row does not reject document");
        Equal(2L, malformedRows.Document.DailyActivity.Single().MessageCount, "valid sibling row retained");
    }

    private static void TestLargeCountersAndDuplicateRows()
    {
        var result = StatsCacheParser.Parse(
            "{\"version\":3,\"dailyActivity\":[{\"date\":\"2026-08-01\",\"messageCount\":3000000000,\"sessionCount\":1,\"toolCallCount\":2},{\"date\":\"2026-08-01\",\"messageCount\":7,\"sessionCount\":2,\"toolCallCount\":3}],\"dailyModelTokens\":[{\"date\":\"2026-08-01\",\"tokensByModel\":{\"unknown\":5000000000}},{\"date\":\"2026-08-01\",\"tokensByModel\":{\"unknown\":11,\"daily-only-model\":13}}],\"modelUsage\":{\"usage-only-model\":{\"inputTokens\":1,\"outputTokens\":2,\"cacheReadInputTokens\":9000000000,\"cacheCreationInputTokens\":4}},\"totalSessions\":3,\"totalMessages\":3000000007,\"hourCounts\":{\"23\":3}}");
        True(result.IsUsable, "large cache usable");
        Equal(3000000007L, result.Document.DailyActivity.Single().MessageCount, "64-bit duplicate activity merge");
        Equal(5000000011L, result.Document.DailyModelTokens.Single().TokensByModel["unknown"], "64-bit duplicate token merge");
        Equal(3, result.Document.ModelIds.Count, "models union daily and all-time keys");
        Equal(5000000024L, UsageAnalyticsCalculator.Calculate(result.Document, new UsageFilter()).TotalTokens, "64-bit analytics");
    }

    private static void TestCurrentDayCollector()
    {
        var source = FixturePath("current-day-config");
        var scratch = Path.Combine(Path.GetTempPath(), "ClaudeUsageTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            CopyDirectory(source, scratch);
            var ignoredDirectory = Path.Combine(scratch, "projects", "project-alpha", "session-today-main", "tool-results");
            Directory.CreateDirectory(ignoredDirectory);
            File.WriteAllText(
                Path.Combine(ignoredDirectory, "must-not-scan.jsonl"),
                "{\"type\":\"assistant\",\"timestamp\":\"2026-08-12T10:00:00Z\",\"message\":{\"model\":\"privacy-leak-model\",\"usage\":{\"input_tokens\":999999}}}",
                new UTF8Encoding(false));

            var before = HashTree(scratch);
            var snapshot = TodayUsageCollector.Collect(scratch, "2026-08-12", CancellationToken.None);
            var after = HashTree(scratch);

            Equal(3, snapshot.FilesScanned, "only main and subagent transcript locations scanned");
            Equal(3, snapshot.FilesMatched, "overnight session contributes entries after UTC midnight");
            Equal(1L, snapshot.Activity.SessionCount, "today sessions");
            Equal(7L, snapshot.Activity.MessageCount, "today main messages across sessions");
            Equal(7L, snapshot.Activity.ToolCallCount, "today main plus subagent tool calls");
            Equal(1023L, Sum(snapshot, usage => usage.InputTokens), "today input");
            Equal(2034L, Sum(snapshot, usage => usage.OutputTokens), "today output");
            Equal(4067L, Sum(snapshot, usage => usage.CacheReadInputTokens), "today cache read");
            Equal(3051L, Sum(snapshot, usage => usage.CacheCreationInputTokens), "today cache creation");
            Equal(10175L, snapshot.ModelTokens.TokensByModel.Values.Sum(), "today daily processed tokens");
            Equal(10175L, Sum(snapshot, usage => Processed(usage)), "today all processed");
            True(snapshot.Warnings.Any(value => value.Code == "transcript.line_invalid"), "stable malformed EOF is informational");
            True(!snapshot.Warnings.Any(value => value.IsTransient), "stable malformed EOF does not stale a refresh");
            True(!snapshot.ModelUsage.ContainsKey("privacy-leak-model"), "tool-result sidecar not read");
            Equal(before, after, "collector never modifies transcript tree");

            var exposedStrings = new List<string>();
            CollectStrings(snapshot, exposedStrings, new HashSet<object>(ReferenceEqualityComparer.Instance));
            var joined = string.Join("\n", exposedStrings);
            foreach (var canary in new[] { "PRIVATE_MAIN_PROMPT", "PRIVATE_ASSISTANT_TEXT", "PRIVATE_TOOL_INPUT", "PRIVATE_TOOL_RESULT", "PRIVATE_WRITE_CONTENT", "private-work" })
            {
                True(!joined.Contains(canary, StringComparison.Ordinal), "content is not retained: " + canary);
            }

            var history = ParseFixture("stats-cache-basic.json").Document;
            var overlay = StatsCacheOverlay.ApplyToday(history, snapshot);
            var today = UsageAnalyticsCalculator.Calculate(
                overlay,
                new UsageFilter("2026-08-12", "2026-08-12", null));
            Equal(10175L, today.TotalTokens, "live today overlay processed tokens");
            Equal(7L, today.TotalMessages, "live today overlay messages");
            Equal(1L, today.TotalSessions, "live today overlay sessions");
            Equal(7L, today.TotalToolCalls, "live today overlay tools");

            var legacy = ParseFixture("stats-cache-v1-minimal.json").Document;
            var legacyOverlay = StatsCacheOverlay.ApplyToday(legacy, snapshot);
            var legacyToday = UsageAnalyticsCalculator.Calculate(
                legacyOverlay,
                new UsageFilter("2026-08-12", "2026-08-12", null));
            Equal(3057L, legacyToday.TotalTokens, "legacy daily series keeps input + output semantics");
        }
        finally
        {
            if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
        }
    }

    private static StatsCacheParseResult ParseFixture(string name)
    {
        return StatsCacheParser.Parse(File.ReadAllText(FixturePath(name), Encoding.UTF8));
    }

    private static string FixturePath(string name)
    {
        return Path.Combine(AppContext.BaseDirectory, "fixtures", name);
    }

    private static long Sum(TodayUsageSnapshot snapshot, Func<ModelUsage, long> selector)
    {
        return snapshot.ModelUsage.Values.Sum(selector);
    }

    private static long Processed(ModelUsage usage)
    {
        return usage.InputTokens + usage.OutputTokens + usage.CacheReadInputTokens + usage.CacheCreationInputTokens;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        }
        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    private static string HashTree(string root)
    {
        using var sha = SHA256.Create();
        foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
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
        foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public).Where(value => value.GetIndexParameters().Length == 0))
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
            throw new InvalidOperationException("Assertion failed: " + name + "; expected " + expected + ", got " + actual);
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

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
        public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
        public int GetHashCode(object obj) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj); }
    }
}
