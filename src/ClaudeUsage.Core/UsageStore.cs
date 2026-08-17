using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    public sealed class UsageRefreshResult
    {
        internal UsageRefreshResult(
            UsageHistory history,
            UsageHistory scanned,
            ScanReport report,
            int archivedOnlyDays)
        {
            History = history;
            Scanned = scanned;
            Report = report;
            ArchivedOnlyDays = archivedOnlyDays;
        }

        /// <summary>Archive merged with this pass: the full history the app can show.</summary>
        public UsageHistory History { get; }

        /// <summary>Only what the transcripts on disk still contain.</summary>
        public UsageHistory Scanned { get; }

        public ScanReport Report { get; }

        /// <summary>
        /// Days that exist only in this app's archive, because Claude Code has since deleted the
        /// transcripts they came from.
        /// </summary>
        public int ArchivedOnlyDays { get; }
    }

    /// <summary>
    /// Keeps a durable local record of daily usage, plus the incremental scan index.
    ///
    /// Two problems make this necessary. Claude Code deletes transcripts once they age past its
    /// cleanup period, so transcripts alone cannot answer questions about older dates; and
    /// reparsing every transcript on every refresh is wasteful. The store solves both: daily
    /// totals accumulate permanently, and unchanged transcripts are never reread.
    ///
    /// Only aggregates are stored: dates, model identifiers, counters, opaque path hashes, and
    /// opaque response hashes. No prompt text, project path, session identifier, or message
    /// identifier is written.
    /// </summary>
    public sealed class UsageStore
    {
        private const int CurrentSchema = 1;

        private readonly string _path;
        private readonly Dictionary<string, DayBuilder> _archive =
            new Dictionary<string, DayBuilder>(StringComparer.Ordinal);

        private TranscriptIndex _index = new TranscriptIndex();
        private bool _dirty;

        public UsageStore(string storePath)
        {
            if (string.IsNullOrWhiteSpace(storePath)) throw new ArgumentException("A store path is required.", nameof(storePath));
            _path = storePath;
        }

        /// <summary>Time zone the archive's day boundaries were recorded in.</summary>
        public string ArchiveTimeZoneId { get; private set; }

        /// <summary>True when the archive was recorded in a different time zone than the current one.</summary>
        public bool TimeZoneChanged { get; private set; }

        public int ArchivedDayCount
        {
            get { return _archive.Count; }
        }

        public bool HasUnsavedChanges
        {
            get { return _dirty; }
        }

        /// <summary>
        /// Reads a previously saved store. Never throws: an unreadable or future-schema store is
        /// treated as an empty one, which only costs a full rescan.
        /// </summary>
        public void Load(TimeZoneInfo timeZone)
        {
            var zoneId = (timeZone ?? TimeZoneInfo.Local).Id;
            _archive.Clear();
            _index = new TranscriptIndex();
            ArchiveTimeZoneId = zoneId;
            TimeZoneChanged = false;
            _dirty = false;

            string json;
            try
            {
                if (!File.Exists(_path)) return;
                json = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch
            {
                return;
            }

            IDictionary<string, object> root;
            try
            {
                root = MiniJsonParser.Parse(json) as IDictionary<string, object>;
            }
            catch (MiniJsonException)
            {
                return;
            }

            if (root == null || ReadLong(root, "schema") != CurrentSchema) return;

            var storedZone = ReadString(root, "timeZoneId");
            if (!string.IsNullOrEmpty(storedZone))
            {
                ArchiveTimeZoneId = storedZone;
                TimeZoneChanged = !string.Equals(storedZone, zoneId, StringComparison.Ordinal);
            }

            var days = ReadObject(root, "days");
            if (days != null)
            {
                foreach (var pair in days)
                {
                    var day = ReadDay(pair.Key, pair.Value as IDictionary<string, object>);
                    if (day != null) _archive[pair.Key] = day;
                }
            }

            // A different time zone re-buckets every day boundary, so the cached per-file
            // aggregates cannot be reused. The archive is kept; transcripts are read again.
            if (TimeZoneChanged)
            {
                ArchiveTimeZoneId = zoneId;
                _dirty = true;
                return;
            }

            var files = ReadObject(root, "files");
            if (files == null) return;
            foreach (var pair in files)
            {
                var record = ReadFile(pair.Key, pair.Value as IDictionary<string, object>);
                if (record != null) _index.Put(record);
            }
        }

        /// <summary>
        /// Scans the transcripts under <paramref name="roots"/>, folds the result into the
        /// archive, and returns the merged history.
        /// </summary>
        public UsageRefreshResult Refresh(
            IEnumerable<string> roots,
            TimeZoneInfo timeZone,
            CancellationToken cancellationToken,
            IProgress<TranscriptScanProgress> progress = null)
        {
            var zone = timeZone ?? TimeZoneInfo.Local;
            var scan = TranscriptScanner.Scan(roots, _index, zone, cancellationToken, progress);
            _index = scan.Index;
            ArchiveTimeZoneId = zone.Id;

            var archivedOnly = 0;
            var scannedDates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var day in scan.History.Days)
            {
                scannedDates.Add(day.Date);
                if (MergeIntoArchive(day)) _dirty = true;
            }

            foreach (var date in _archive.Keys)
            {
                if (!scannedDates.Contains(date)) archivedOnly++;
            }

            var merged = new UsageHistory(_archive.Values.Select(day => day.Build()));
            return new UsageRefreshResult(merged, scan.History, scan.Report, archivedOnly);
        }

        /// <summary>Discards the archive and the scan index so the next refresh rebuilds both.</summary>
        public void Clear()
        {
            _archive.Clear();
            _index = new TranscriptIndex();
            _dirty = true;
        }

        /// <summary>Writes the store if anything changed. Failures are non-fatal by design.</summary>
        public bool Save()
        {
            if (!_dirty) return true;
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var temporary = _path + ".tmp";
                File.WriteAllText(temporary, Serialize(), new UTF8Encoding(false));
                if (File.Exists(_path))
                {
                    File.Replace(temporary, _path, null);
                }
                else
                {
                    File.Move(temporary, _path);
                }

                _dirty = false;
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Daily totals only ever grow, so merging by element-wise maximum keeps the archive
        /// correct even if Claude Code has already deleted part of a day's transcripts.
        /// </summary>
        private bool MergeIntoArchive(UsageDay day)
        {
            DayBuilder existing;
            if (!_archive.TryGetValue(day.Date, out existing))
            {
                existing = new DayBuilder(day.Date);
                _archive.Add(day.Date, existing);
            }

            var changed = false;
            if (day.MessageCount > existing.MessageCount)
            {
                existing.MessageCount = day.MessageCount;
                changed = true;
            }

            if (day.SessionCount > existing.SessionCount)
            {
                existing.SessionCount = day.SessionCount;
                changed = true;
            }

            foreach (var pair in day.Models)
            {
                var model = existing.Model(pair.Key);
                var scanned = pair.Value;
                changed |= Raise(model, scanned);
            }

            return changed;
        }

        private static bool Raise(ModelBuilder target, ModelUsage scanned)
        {
            var changed = false;
            if (scanned.Tokens.InputTokens > target.Input) { target.Input = scanned.Tokens.InputTokens; changed = true; }
            if (scanned.Tokens.OutputTokens > target.Output) { target.Output = scanned.Tokens.OutputTokens; changed = true; }
            if (scanned.Tokens.CacheReadTokens > target.CacheRead) { target.CacheRead = scanned.Tokens.CacheReadTokens; changed = true; }
            if (scanned.Tokens.CacheCreationTokens > target.CacheCreation) { target.CacheCreation = scanned.Tokens.CacheCreationTokens; changed = true; }
            if (scanned.ResponseCount > target.Responses) { target.Responses = scanned.ResponseCount; changed = true; }
            if (scanned.ToolCallCount > target.ToolCalls) { target.ToolCalls = scanned.ToolCallCount; changed = true; }
            if (scanned.WebSearchRequests > target.WebSearches) { target.WebSearches = scanned.WebSearchRequests; changed = true; }
            return changed;
        }

        private string Serialize()
        {
            var writer = new JsonWriter();
            writer.StartObject();
            writer.Property("schema", CurrentSchema);
            writer.Property("updatedUtc", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
            writer.Property("timeZoneId", ArchiveTimeZoneId ?? TimeZoneInfo.Local.Id);

            writer.Name("days").StartObject();
            foreach (var date in _archive.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                var day = _archive[date];
                writer.Name(date).StartObject();
                writer.Property("sessions", day.SessionCount);
                writer.Property("messages", day.MessageCount);
                WriteModels(writer, day.Models);
                writer.EndObject();
            }

            writer.EndObject();

            writer.Name("files").StartObject();
            foreach (var file in _index.Files.OrderBy(value => value.Key, StringComparer.Ordinal))
            {
                writer.Name(file.Key).StartObject();
                writer.Property("len", file.Length);
                writer.Property("ticks", file.LastWriteUtcTicks);
                if (file.SessionDate != null) writer.Property("session", file.SessionDate);
                writer.Name("days").StartObject();
                foreach (var date in file.Days.Keys.OrderBy(value => value, StringComparer.Ordinal))
                {
                    var day = file.Days[date];
                    writer.Name(date).StartObject();
                    writer.Property("messages", day.MessageCount);
                    WriteModels(writer, day.Models);
                    writer.EndObject();
                }

                writer.EndObject();
                writer.Property("keys", PackKeys(file.DedupKeys));
                writer.EndObject();
            }

            writer.EndObject();
            writer.EndObject();
            return writer.ToString();
        }

        private static void WriteModels(JsonWriter writer, IDictionary<string, ModelBuilder> models)
        {
            writer.Name("models").StartObject();
            foreach (var modelId in models.Keys.OrderBy(value => value, StringComparer.Ordinal))
            {
                var model = models[modelId];
                writer.Name(modelId).StartObject();
                writer.Property("in", model.Input);
                writer.Property("out", model.Output);
                writer.Property("cr", model.CacheRead);
                writer.Property("cc", model.CacheCreation);
                writer.Property("resp", model.Responses);
                writer.Property("tools", model.ToolCalls);
                writer.Property("web", model.WebSearches);
                writer.EndObject();
            }

            writer.EndObject();
        }

        private static DayBuilder ReadDay(string date, IDictionary<string, object> source)
        {
            if (source == null || !IsDate(date)) return null;
            var day = new DayBuilder(date);
            day.SessionCount = ReadLong(source, "sessions");
            day.MessageCount = ReadLong(source, "messages");
            ReadModels(source, day);
            return day;
        }

        private static TranscriptFileUsage ReadFile(string key, IDictionary<string, object> source)
        {
            if (source == null || string.IsNullOrEmpty(key)) return null;
            var record = new TranscriptFileUsage(key, ReadLong(source, "len"), ReadLong(source, "ticks"));
            var session = ReadString(source, "session");
            if (IsDate(session)) record.SessionDate = session;

            var days = ReadObject(source, "days");
            if (days != null)
            {
                foreach (var pair in days)
                {
                    if (!IsDate(pair.Key)) continue;
                    var dayObject = pair.Value as IDictionary<string, object>;
                    if (dayObject == null) continue;
                    var day = record.Day(pair.Key);
                    day.MessageCount = ReadLong(dayObject, "messages");
                    ReadModels(dayObject, day);
                }
            }

            record.SetDedupKeys(UnpackKeys(ReadString(source, "keys")));
            return record;
        }

        private static void ReadModels(IDictionary<string, object> source, DayBuilder day)
        {
            var models = ReadObject(source, "models");
            if (models == null) return;
            foreach (var pair in models)
            {
                var modelObject = pair.Value as IDictionary<string, object>;
                if (modelObject == null || string.IsNullOrEmpty(pair.Key)) continue;
                var model = day.Model(pair.Key);
                model.Input = ReadLong(modelObject, "in");
                model.Output = ReadLong(modelObject, "out");
                model.CacheRead = ReadLong(modelObject, "cr");
                model.CacheCreation = ReadLong(modelObject, "cc");
                model.Responses = ReadLong(modelObject, "resp");
                model.ToolCalls = ReadLong(modelObject, "tools");
                model.WebSearches = ReadLong(modelObject, "web");
            }
        }

        private static string PackKeys(ulong[] keys)
        {
            if (keys == null || keys.Length == 0) return string.Empty;
            var ordered = keys.OrderBy(value => value).ToArray();
            var bytes = new byte[ordered.Length * 8];
            for (var index = 0; index < ordered.Length; index++)
            {
                var value = ordered[index];
                for (var offset = 0; offset < 8; offset++)
                {
                    bytes[(index * 8) + offset] = (byte)(value >> (offset * 8));
                }
            }

            return Convert.ToBase64String(bytes);
        }

        private static IEnumerable<ulong> UnpackKeys(string packed)
        {
            var result = new List<ulong>();
            if (string.IsNullOrEmpty(packed)) return result;
            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(packed);
            }
            catch (FormatException)
            {
                return result;
            }

            for (var index = 0; index + 8 <= bytes.Length; index += 8)
            {
                ulong value = 0;
                for (var offset = 7; offset >= 0; offset--)
                {
                    value = (value << 8) | bytes[index + offset];
                }

                result.Add(value);
            }

            return result;
        }

        private static bool IsDate(string value)
        {
            return DateKey.IsValid(value);
        }

        private static string ReadString(IDictionary<string, object> source, string property)
        {
            object value;
            return source.TryGetValue(property, out value) ? value as string : null;
        }

        private static IDictionary<string, object> ReadObject(IDictionary<string, object> source, string property)
        {
            object value;
            return source.TryGetValue(property, out value) ? value as IDictionary<string, object> : null;
        }

        private static long ReadLong(IDictionary<string, object> source, string property)
        {
            object value;
            if (!source.TryGetValue(property, out value)) return 0;
            var number = value as JsonNumber;
            var raw = number == null ? value as string : number.Raw;
            long parsed;
            return raw != null && long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) && parsed >= 0
                ? parsed
                : 0;
        }
    }
}
