using System;
using ClaudeUsage.Core;

namespace ClaudeUsage.WinForms
{
    internal static class RuntimeSelfTest
    {
        public static bool Run()
        {
            const string json = "{\"version\":5,\"dailyModelTokensVersion\":5,\"lastComputedDate\":\"2026-08-02\",\"dailyActivity\":[{\"date\":\"2026-08-02\",\"messageCount\":8,\"sessionCount\":2,\"toolCallCount\":12}],\"dailyModelTokens\":[{\"date\":\"2026-08-02\",\"tokensByModel\":{\"claude-test\":1280}}],\"modelUsage\":{\"claude-test\":{\"inputTokens\":200,\"outputTokens\":30,\"cacheReadInputTokens\":1000,\"cacheCreationInputTokens\":50}}}";
            try
            {
                var parsed = StatsCacheParser.Parse(json);
                var view = UsageAnalyticsCalculator.Calculate(
                    parsed.Document,
                    new UsageFilter("2026-08-02", "2026-08-02", new[] { "claude-test" }));
                return parsed.IsUsable &&
                       parsed.Document.DailyTokensIncludeCache &&
                       view.TotalTokens == 1280 &&
                       view.TotalMessages == 8 &&
                       view.TotalSessions == 2 &&
                       view.TotalToolCalls == 12;
            }
            catch
            {
                return false;
            }
        }
    }
}
