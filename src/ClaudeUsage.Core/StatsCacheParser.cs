using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    public static class StatsCacheParser
    {
        public const int LatestObservedSchemaVersion = 5;

        public static StatsCacheParseResult Parse(string json)
        {
            var warnings = new List<ParseWarning>();
            if (string.IsNullOrWhiteSpace(json))
            {
                warnings.Add(Warning(
                    "json.empty",
                    "The stats cache is empty.",
                    "$",
                    WarningSeverity.Error));
                return FatalResult(warnings);
            }

            object rootValue;
            try
            {
                rootValue = MiniJsonParser.Parse(json);
            }
            catch (MiniJsonException exception)
            {
                warnings.Add(Warning(
                    "json.invalid",
                    "The stats cache is not complete or contains invalid JSON: " + exception.Message,
                    "$",
                    WarningSeverity.Error));
                return FatalResult(warnings);
            }

            var root = rootValue as IDictionary<string, object>;
            if (root == null)
            {
                warnings.Add(Warning(
                    "root.invalid_type",
                    "The stats cache root must be a JSON object.",
                    "$",
                    WarningSeverity.Error));
                return FatalResult(warnings);
            }

            var version = ReadVersion(root, warnings);
            var dailyModelTokensVersionValue = ReadOptionalCounter(
                root,
                "dailyModelTokensVersion",
                "$",
                warnings);
            var dailyModelTokensVersion = dailyModelTokensVersionValue.HasValue
                ? (int)Math.Min(int.MaxValue, dailyModelTokensVersionValue.Value)
                : 0;
            var lastComputedDate = ReadOptionalDate(root, "lastComputedDate", "$", warnings);
            var dailyActivity = ReadDailyActivity(root, warnings);
            var dailyModelTokens = ReadDailyModelTokens(root, warnings);
            var modelUsage = ReadModelUsage(root, warnings);
            var totalSessions = ReadCounter(root, "totalSessions", "$", warnings, true);
            var totalMessages = ReadCounter(root, "totalMessages", "$", warnings, true);
            var longestSession = ReadLongestSession(root, warnings);
            var firstSessionDate = ReadOptionalTimestamp(root, "firstSessionDate", "$", warnings);
            var hourCounts = ReadHourCounts(root, warnings);
            var totalSpeculationTimeSavedMs = ReadCounter(
                root,
                "totalSpeculationTimeSavedMs",
                "$",
                warnings,
                false);
            var shotDistribution = ReadShotDistribution(root, warnings);

            var document = new StatsCacheDocument(
                version,
                dailyModelTokensVersion,
                lastComputedDate,
                dailyActivity,
                dailyModelTokens,
                modelUsage,
                totalSessions,
                totalMessages,
                longestSession,
                firstSessionDate,
                hourCounts,
                totalSpeculationTimeSavedMs,
                shotDistribution);

            return new StatsCacheParseResult(document, warnings, true);
        }

        private static StatsCacheParseResult FatalResult(IList<ParseWarning> warnings)
        {
            return new StatsCacheParseResult(EmptyDocument(), warnings, false);
        }

        private static StatsCacheDocument EmptyDocument()
        {
            return new StatsCacheDocument(
                0,
                0,
                null,
                new List<DailyActivity>(),
                new List<DailyModelTokens>(),
                new Dictionary<string, ModelUsage>(StringComparer.Ordinal),
                0,
                0,
                null,
                null,
                new Dictionary<int, long>(),
                0,
                new Dictionary<int, long>());
        }

        private static int ReadVersion(IDictionary<string, object> root, IList<ParseWarning> warnings)
        {
            object value;
            if (!root.TryGetValue("version", out value) || value == null)
            {
                warnings.Add(Warning(
                    "schema.version_missing",
                    "The cache has no schema version. Compatible fields will still be read.",
                    "$.version",
                    WarningSeverity.Warning));
                return 0;
            }

            long parsed;
            if (!TryReadNonNegativeInt64(value, out parsed) || parsed > int.MaxValue)
            {
                warnings.Add(Warning(
                    "schema.version_invalid",
                    "The cache schema version is not a supported integer value.",
                    "$.version",
                    WarningSeverity.Warning));
                return 0;
            }

            var version = (int)parsed;
            if (version > LatestObservedSchemaVersion)
            {
                warnings.Add(Warning(
                    "schema.future_version",
                    "This cache was written by a newer schema version. Known compatible fields were loaded.",
                    "$.version",
                    WarningSeverity.Warning));
            }

            return version;
        }

        private static IList<DailyActivity> ReadDailyActivity(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            var array = ReadArray(root, "dailyActivity", "$", warnings, true);
            var byDate = new Dictionary<string, MutableActivity>(StringComparer.Ordinal);
            for (var index = 0; index < array.Count; index++)
            {
                var path = "$.dailyActivity[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                var item = array[index] as IDictionary<string, object>;
                if (item == null)
                {
                    warnings.Add(Warning(
                        "daily_activity.row_invalid",
                        "The daily activity row is not an object and was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                var date = ReadRequiredDate(item, "date", path, warnings);
                if (date == null)
                {
                    continue;
                }

                var activity = new MutableActivity(
                    ReadCounter(item, "messageCount", path, warnings, true),
                    ReadCounter(item, "sessionCount", path, warnings, true),
                    ReadCounter(item, "toolCallCount", path, warnings, true));

                MutableActivity existing;
                if (byDate.TryGetValue(date, out existing))
                {
                    warnings.Add(Warning(
                        "daily_activity.duplicate_date",
                        "Duplicate daily activity rows were merged.",
                        path + ".date",
                        WarningSeverity.Information));
                    existing.MessageCount = SafeAdd(existing.MessageCount, activity.MessageCount, path, warnings);
                    existing.SessionCount = SafeAdd(existing.SessionCount, activity.SessionCount, path, warnings);
                    existing.ToolCallCount = SafeAdd(existing.ToolCallCount, activity.ToolCallCount, path, warnings);
                }
                else
                {
                    byDate.Add(date, activity);
                }
            }

            return byDate
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DailyActivity(
                    pair.Key,
                    pair.Value.MessageCount,
                    pair.Value.SessionCount,
                    pair.Value.ToolCallCount))
                .ToList();
        }

        private static IList<DailyModelTokens> ReadDailyModelTokens(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            var array = ReadArray(root, "dailyModelTokens", "$", warnings, true);
            var byDate = new Dictionary<string, Dictionary<string, long>>(StringComparer.Ordinal);
            for (var index = 0; index < array.Count; index++)
            {
                var path = "$.dailyModelTokens[" + index.ToString(CultureInfo.InvariantCulture) + "]";
                var item = array[index] as IDictionary<string, object>;
                if (item == null)
                {
                    warnings.Add(Warning(
                        "daily_tokens.row_invalid",
                        "The daily model token row is not an object and was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                var date = ReadRequiredDate(item, "date", path, warnings);
                if (date == null)
                {
                    continue;
                }

                object tokensValue;
                var tokenObject = item.TryGetValue("tokensByModel", out tokensValue)
                    ? tokensValue as IDictionary<string, object>
                    : null;
                if (tokenObject == null)
                {
                    warnings.Add(Warning(
                        "daily_tokens.models_invalid",
                        "tokensByModel is missing or is not an object; the row was skipped.",
                        path + ".tokensByModel",
                        WarningSeverity.Warning));
                    continue;
                }

                Dictionary<string, long> dateTokens;
                if (!byDate.TryGetValue(date, out dateTokens))
                {
                    dateTokens = new Dictionary<string, long>(StringComparer.Ordinal);
                    byDate.Add(date, dateTokens);
                }
                else
                {
                    warnings.Add(Warning(
                        "daily_tokens.duplicate_date",
                        "Duplicate daily model token rows were merged.",
                        path + ".date",
                        WarningSeverity.Information));
                }

                foreach (var modelPair in tokenObject)
                {
                    var modelPath = path + ".tokensByModel[" + QuotePathPart(modelPair.Key) + "]";
                    if (string.IsNullOrWhiteSpace(modelPair.Key))
                    {
                        warnings.Add(Warning(
                            "model.invalid_id",
                            "An empty model identifier was skipped.",
                            modelPath,
                            WarningSeverity.Warning));
                        continue;
                    }

                    long tokens;
                    if (!TryReadNonNegativeInt64(modelPair.Value, out tokens))
                    {
                        warnings.Add(Warning(
                            "counter.invalid",
                            "The token count must be a non-negative 64-bit integer and was treated as zero.",
                            modelPath,
                            WarningSeverity.Warning));
                        continue;
                    }

                    if (modelPair.Value is string)
                    {
                        warnings.Add(CoercionWarning(modelPath));
                    }

                    long existing;
                    dateTokens.TryGetValue(modelPair.Key, out existing);
                    dateTokens[modelPair.Key] = SafeAdd(existing, tokens, modelPath, warnings);
                }
            }

            return byDate
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new DailyModelTokens(pair.Key, pair.Value))
                .ToList();
        }

        private static IDictionary<string, ModelUsage> ReadModelUsage(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!root.TryGetValue("modelUsage", out value) || value == null)
            {
                warnings.Add(Warning(
                    "model_usage.missing",
                    "All-time model usage is missing. Daily model data can still be displayed.",
                    "$.modelUsage",
                    WarningSeverity.Warning));
                return new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            }

            var usageObject = value as IDictionary<string, object>;
            if (usageObject == null)
            {
                warnings.Add(Warning(
                    "model_usage.invalid",
                    "All-time model usage is not an object and was ignored.",
                    "$.modelUsage",
                    WarningSeverity.Warning));
                return new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            }

            var result = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            foreach (var pair in usageObject)
            {
                var path = "$.modelUsage[" + QuotePathPart(pair.Key) + "]";
                if (string.IsNullOrWhiteSpace(pair.Key))
                {
                    warnings.Add(Warning(
                        "model.invalid_id",
                        "An empty model identifier was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                var item = pair.Value as IDictionary<string, object>;
                if (item == null)
                {
                    warnings.Add(Warning(
                        "model_usage.row_invalid",
                        "The model usage value is not an object and was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                result[pair.Key] = new ModelUsage(
                    pair.Key,
                    ReadCounter(item, "inputTokens", path, warnings, false),
                    ReadCounter(item, "outputTokens", path, warnings, false),
                    ReadCounter(item, "cacheReadInputTokens", path, warnings, false),
                    ReadCounter(item, "cacheCreationInputTokens", path, warnings, false),
                    ReadCounter(item, "webSearchRequests", path, warnings, false),
                    ReadOptionalDouble(item, "costUSD", path, warnings),
                    ReadOptionalCounter(item, "contextWindow", path, warnings),
                    ReadOptionalCounter(item, "maxOutputTokens", path, warnings));
            }

            return result;
        }

        private static LongestSession ReadLongestSession(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!root.TryGetValue("longestSession", out value) || value == null)
            {
                return null;
            }

            var item = value as IDictionary<string, object>;
            if (item == null)
            {
                warnings.Add(Warning(
                    "longest_session.invalid",
                    "Longest session data is not an object and was ignored.",
                    "$.longestSession",
                    WarningSeverity.Warning));
                return null;
            }

            return new LongestSession(
                ReadOptionalString(item, "sessionId", "$.longestSession", warnings),
                ReadCounter(item, "duration", "$.longestSession", warnings, true),
                ReadCounter(item, "messageCount", "$.longestSession", warnings, true),
                ReadOptionalTimestamp(item, "timestamp", "$.longestSession", warnings));
        }

        private static IDictionary<int, long> ReadHourCounts(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!root.TryGetValue("hourCounts", out value) || value == null)
            {
                return new Dictionary<int, long>();
            }

            var item = value as IDictionary<string, object>;
            if (item == null)
            {
                warnings.Add(Warning(
                    "hour_counts.invalid",
                    "Hour counts are not an object and were ignored.",
                    "$.hourCounts",
                    WarningSeverity.Warning));
                return new Dictionary<int, long>();
            }

            var result = new Dictionary<int, long>();
            foreach (var pair in item)
            {
                int hour;
                var path = "$.hourCounts[" + QuotePathPart(pair.Key) + "]";
                if (!int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out hour)
                    || hour < 0
                    || hour > 23)
                {
                    warnings.Add(Warning(
                        "hour_counts.invalid_hour",
                        "Hour keys must be integers from 0 through 23; this entry was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                long count;
                if (!TryReadNonNegativeInt64(pair.Value, out count))
                {
                    warnings.Add(Warning(
                        "counter.invalid",
                        "The hour count must be a non-negative 64-bit integer and was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                result[hour] = count;
            }

            return result;
        }

        private static IDictionary<int, long> ReadShotDistribution(
            IDictionary<string, object> root,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!root.TryGetValue("shotDistribution", out value) || value == null)
            {
                return new Dictionary<int, long>();
            }

            var item = value as IDictionary<string, object>;
            if (item == null)
            {
                warnings.Add(Warning(
                    "shot_distribution.invalid",
                    "Shot distribution is not an object and was ignored.",
                    "$.shotDistribution",
                    WarningSeverity.Warning));
                return new Dictionary<int, long>();
            }

            var result = new Dictionary<int, long>();
            foreach (var pair in item)
            {
                int shotCount;
                var path = "$.shotDistribution[" + QuotePathPart(pair.Key) + "]";
                if (!int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out shotCount)
                    || shotCount < 0)
                {
                    warnings.Add(Warning(
                        "shot_distribution.invalid_key",
                        "Shot distribution keys must be non-negative integers; this entry was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                long count;
                if (!TryReadNonNegativeInt64(pair.Value, out count))
                {
                    warnings.Add(Warning(
                        "counter.invalid",
                        "The shot count must be a non-negative 64-bit integer and was skipped.",
                        path,
                        WarningSeverity.Warning));
                    continue;
                }

                result[shotCount] = count;
            }

            return result;
        }

        private static IList<object> ReadArray(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings,
            bool warnWhenMissing)
        {
            object value;
            var path = parentPath + "." + propertyName;
            if (!source.TryGetValue(propertyName, out value) || value == null)
            {
                if (warnWhenMissing)
                {
                    warnings.Add(Warning(
                        "field.missing",
                        propertyName + " is missing; it was treated as empty.",
                        path,
                        WarningSeverity.Warning));
                }

                return new List<object>();
            }

            var array = value as IList<object>;
            if (array != null)
            {
                return array;
            }

            warnings.Add(Warning(
                "field.invalid_type",
                propertyName + " is not an array; it was treated as empty.",
                path,
                WarningSeverity.Warning));
            return new List<object>();
        }

        private static long ReadCounter(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings,
            bool warnWhenMissing)
        {
            object value;
            var path = parentPath + "." + propertyName;
            if (!source.TryGetValue(propertyName, out value) || value == null)
            {
                if (warnWhenMissing)
                {
                    warnings.Add(Warning(
                        "field.missing",
                        propertyName + " is missing and was treated as zero.",
                        path,
                        WarningSeverity.Warning));
                }

                return 0;
            }

            long parsed;
            if (!TryReadNonNegativeInt64(value, out parsed))
            {
                warnings.Add(Warning(
                    "counter.invalid",
                    propertyName + " must be a non-negative 64-bit integer and was treated as zero.",
                    path,
                    WarningSeverity.Warning));
                return 0;
            }

            if (value is string)
            {
                warnings.Add(CoercionWarning(path));
            }

            return parsed;
        }

        private static long? ReadOptionalCounter(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!source.TryGetValue(propertyName, out value) || value == null)
            {
                return null;
            }

            var path = parentPath + "." + propertyName;
            long parsed;
            if (!TryReadNonNegativeInt64(value, out parsed))
            {
                warnings.Add(Warning(
                    "counter.invalid",
                    propertyName + " must be a non-negative 64-bit integer and was ignored.",
                    path,
                    WarningSeverity.Warning));
                return null;
            }

            if (value is string)
            {
                warnings.Add(CoercionWarning(path));
            }

            return parsed;
        }

        private static double? ReadOptionalDouble(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            object value;
            if (!source.TryGetValue(propertyName, out value) || value == null)
            {
                return null;
            }

            var number = value as JsonNumber;
            var raw = number == null ? value as string : number.Raw;
            double parsed;
            if (raw == null
                || !double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)
                || double.IsNaN(parsed)
                || double.IsInfinity(parsed)
                || parsed < 0)
            {
                warnings.Add(Warning(
                    "number.invalid",
                    propertyName + " must be a finite non-negative number and was ignored.",
                    parentPath + "." + propertyName,
                    WarningSeverity.Warning));
                return null;
            }

            if (value is string)
            {
                warnings.Add(CoercionWarning(parentPath + "." + propertyName));
            }

            return parsed;
        }

        private static string ReadRequiredDate(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            var value = ReadOptionalString(source, propertyName, parentPath, warnings);
            if (value == null)
            {
                warnings.Add(Warning(
                    "date.missing",
                    "The row has no date and was skipped.",
                    parentPath + "." + propertyName,
                    WarningSeverity.Warning));
                return null;
            }

            if (!DateKey.IsValid(value))
            {
                warnings.Add(Warning(
                    "date.invalid",
                    "The date must be a real calendar date in YYYY-MM-DD format; the row was skipped.",
                    parentPath + "." + propertyName,
                    WarningSeverity.Warning));
                return null;
            }

            return value;
        }

        private static string ReadOptionalDate(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            object raw;
            if (!source.TryGetValue(propertyName, out raw) || raw == null)
            {
                return null;
            }

            var value = raw as string;
            if (value != null && DateKey.IsValid(value))
            {
                return value;
            }

            warnings.Add(Warning(
                "date.invalid",
                propertyName + " is not a valid YYYY-MM-DD date and was ignored.",
                parentPath + "." + propertyName,
                WarningSeverity.Warning));
            return null;
        }

        private static string ReadOptionalTimestamp(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            object raw;
            if (!source.TryGetValue(propertyName, out raw) || raw == null)
            {
                return null;
            }

            var value = raw as string;
            DateTimeOffset parsed;
            if (value != null
                && DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out parsed))
            {
                return value;
            }

            warnings.Add(Warning(
                "timestamp.invalid",
                propertyName + " is not a valid ISO timestamp and was ignored.",
                parentPath + "." + propertyName,
                WarningSeverity.Warning));
            return null;
        }

        private static string ReadOptionalString(
            IDictionary<string, object> source,
            string propertyName,
            string parentPath,
            IList<ParseWarning> warnings)
        {
            object raw;
            if (!source.TryGetValue(propertyName, out raw) || raw == null)
            {
                return null;
            }

            var value = raw as string;
            if (value != null)
            {
                return value;
            }

            warnings.Add(Warning(
                "string.invalid",
                propertyName + " is not a string and was ignored.",
                parentPath + "." + propertyName,
                WarningSeverity.Warning));
            return null;
        }

        private static bool TryReadNonNegativeInt64(object value, out long result)
        {
            result = 0;
            var number = value as JsonNumber;
            var raw = number == null ? value as string : number.Raw;
            if (raw == null)
            {
                return false;
            }

            long integer;
            if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out integer))
            {
                if (integer < 0)
                {
                    return false;
                }

                result = integer;
                return true;
            }

            decimal decimalValue;
            if (!decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out decimalValue)
                || decimalValue < 0
                || decimal.Truncate(decimalValue) != decimalValue
                || decimalValue > long.MaxValue)
            {
                return false;
            }

            result = (long)decimalValue;
            return true;
        }

        private static long SafeAdd(
            long left,
            long right,
            string path,
            IList<ParseWarning> warnings)
        {
            if (long.MaxValue - left < right)
            {
                warnings.Add(Warning(
                    "counter.overflow",
                    "Merged values exceed Int64.MaxValue and were clamped.",
                    path,
                    WarningSeverity.Warning));
                return long.MaxValue;
            }

            return left + right;
        }

        private static ParseWarning CoercionWarning(string path)
        {
            return Warning(
                "number.coerced_from_string",
                "A numeric string was accepted for compatibility.",
                path,
                WarningSeverity.Information);
        }

        private static ParseWarning Warning(
            string code,
            string message,
            string path,
            WarningSeverity severity)
        {
            return new ParseWarning(code, message, path, severity);
        }

        private static string QuotePathPart(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        private sealed class MutableActivity
        {
            internal MutableActivity(long messageCount, long sessionCount, long toolCallCount)
            {
                MessageCount = messageCount;
                SessionCount = sessionCount;
                ToolCallCount = toolCallCount;
            }

            internal long MessageCount { get; set; }

            internal long SessionCount { get; set; }

            internal long ToolCallCount { get; set; }
        }
    }
}
