using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    public sealed class TranscriptScanProgress
    {
        internal TranscriptScanProgress(int filesCompleted, int totalFiles, long bytesParsed)
        {
            FilesCompleted = filesCompleted;
            TotalFiles = totalFiles;
            BytesParsed = bytesParsed;
        }

        public int FilesCompleted { get; }

        public int TotalFiles { get; }

        public long BytesParsed { get; }
    }

    /// <summary>What a single scan pass observed. Contains no paths, prompts, or identifiers.</summary>
    public sealed class ScanReport
    {
        internal ScanReport(
            int rootsSearched,
            int filesSeen,
            int filesParsed,
            int filesReused,
            long bytesParsed,
            IList<UsageWarning> warnings)
        {
            RootsSearched = rootsSearched;
            FilesSeen = filesSeen;
            FilesParsed = filesParsed;
            FilesReused = filesReused;
            BytesParsed = bytesParsed;
            Warnings = new ReadOnlyCollection<UsageWarning>(warnings.ToList());
        }

        public int RootsSearched { get; }

        public int FilesSeen { get; }

        /// <summary>Transcripts read from disk during this pass.</summary>
        public int FilesParsed { get; }

        /// <summary>Unchanged transcripts served from the incremental index.</summary>
        public int FilesReused { get; }

        public long BytesParsed { get; }

        public IReadOnlyList<UsageWarning> Warnings { get; }

        /// <summary>
        /// False when a transcript was being written mid-scan, meaning the caller should retry
        /// before replacing a previously published total.
        /// </summary>
        public bool IsComplete
        {
            get { return !Warnings.Any(warning => warning.IsTransient); }
        }

        public bool HasImportantWarning
        {
            get { return Warnings.Any(warning => warning.Severity != WarningSeverity.Information); }
        }
    }

    /// <summary>
    /// Reads token and activity metadata out of Claude Code's local JSONL transcripts, which are
    /// the only complete local record of usage. Prompt text, assistant text, thinking, tool
    /// arguments, tool results, project paths, and session identifiers are never retained.
    /// Files are opened read-only and never modified.
    /// </summary>
    public static class TranscriptScanner
    {
        private const int MaximumDetailedWarnings = 60;

        private static readonly HashSet<string> MessageEntryTypes = new HashSet<string>(
            new[] { "user", "assistant", "system" },
            StringComparer.Ordinal);

        /// <summary>
        /// Enumerates the transcript files a scan would read, newest change first.
        /// </summary>
        public static IList<TranscriptFile> FindTranscripts(
            IEnumerable<string> roots,
            IList<UsageWarning> warnings,
            CancellationToken cancellationToken)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            var sink = new WarningSink(MaximumDetailedWarnings);
            var found = new List<TranscriptFile>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var root in roots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(root)) continue;
                var projectsDirectory = Path.Combine(root, "projects");
                if (!SafeDirectoryExists(projectsDirectory)) continue;

                string[] projectDirectories;
                try
                {
                    projectDirectories = Directory.GetDirectories(projectsDirectory, "*", SearchOption.TopDirectoryOnly);
                }
                catch (Exception error) when (IsRecoverableIoException(error))
                {
                    sink.Add(
                        "transcripts.enumeration_failed",
                        "A Claude data location could not be enumerated during this scan.",
                        "data location",
                        WarningSeverity.Warning);
                    continue;
                }

                foreach (var projectDirectory in projectDirectories)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        // Main transcripts sit directly in each project directory. Tool-result
                        // sidecars live deeper and are deliberately never opened.
                        AddFiles(
                            Directory.GetFiles(projectDirectory, "*.jsonl", SearchOption.TopDirectoryOnly),
                            false,
                            found,
                            seenPaths,
                            sink);

                        foreach (var sessionDirectory in Directory.GetDirectories(
                            projectDirectory,
                            "*",
                            SearchOption.TopDirectoryOnly))
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var subagentsDirectory = Path.Combine(sessionDirectory, "subagents");
                            if (!SafeDirectoryExists(subagentsDirectory)) continue;
                            AddFiles(
                                Directory.GetFiles(subagentsDirectory, "agent-*.jsonl", SearchOption.TopDirectoryOnly),
                                true,
                                found,
                                seenPaths,
                                sink);
                        }
                    }
                    catch (Exception error) when (IsRecoverableIoException(error))
                    {
                        sink.Add(
                            "transcripts.project_enumeration_failed",
                            "One Claude project folder could not be fully enumerated during this scan.",
                            "project folder",
                            WarningSeverity.Warning);
                    }
                }
            }

            if (warnings != null)
            {
                foreach (var warning in sink.ToList()) warnings.Add(warning);
            }

            found.Sort((left, right) => StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path));
            return found;
        }

        /// <summary>
        /// Scans every transcript below the supplied roots. Files whose length and last-write
        /// time match <paramref name="previous"/> are served from that index instead of reread,
        /// so a steady-state refresh only parses the session that is actually being written.
        /// </summary>
        public static TranscriptScanResult Scan(
            IEnumerable<string> roots,
            TranscriptIndex previous,
            TimeZoneInfo timeZone,
            CancellationToken cancellationToken,
            IProgress<TranscriptScanProgress> progress = null)
        {
            if (roots == null) throw new ArgumentNullException(nameof(roots));
            var rootList = roots
                .Where(root => !string.IsNullOrWhiteSpace(root))
                .Select(root => root.Trim())
                .ToList();
            var zone = timeZone ?? TimeZoneInfo.Local;
            var sink = new WarningSink(MaximumDetailedWarnings);
            var enumerationWarnings = new List<UsageWarning>();
            var files = FindTranscripts(rootList, enumerationWarnings, cancellationToken);
            foreach (var warning in enumerationWarnings) sink.Add(warning);

            var index = new TranscriptIndex();
            var dedupKeys = new HashSet<ulong>();
            var pending = new List<TranscriptFile>();
            var reused = 0;

            // Pass one: keep every unchanged file's cached aggregate, and seed the de-duplication
            // set with the responses those aggregates already counted.
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cached = previous == null ? null : previous.Find(file.Key);
                if (cached != null && cached.Matches(file.Length, file.LastWriteUtcTicks))
                {
                    index.Put(cached);
                    foreach (var key in cached.DedupKeys) dedupKeys.Add(key);
                    reused++;
                }
                else
                {
                    pending.Add(file);
                }
            }

            // Pass two: read only what changed.
            var parsed = 0;
            long bytesParsed = 0;
            var completed = reused;
            progress?.Report(new TranscriptScanProgress(completed, files.Count, 0));
            foreach (var file in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    index.Put(ParseFile(file, zone, dedupKeys, sink, cancellationToken));
                    parsed++;
                    bytesParsed += file.Length;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception error) when (IsRecoverableIoException(error) || error is UnauthorizedAccessException)
                {
                    sink.Add(
                        "transcript.read_failed",
                        "A transcript changed or could not be read during this scan.",
                        file.IsSubagent ? "subagent transcript" : "session transcript",
                        WarningSeverity.Warning);
                }

                completed++;
                progress?.Report(new TranscriptScanProgress(completed, files.Count, bytesParsed));
            }

            var report = new ScanReport(
                rootList.Count,
                files.Count,
                parsed,
                reused,
                bytesParsed,
                sink.ToList());
            return new TranscriptScanResult(index, index.BuildHistory(), report);
        }

        private static TranscriptFileUsage ParseFile(
            TranscriptFile file,
            TimeZoneInfo zone,
            HashSet<ulong> dedupKeys,
            WarningSink warnings,
            CancellationToken cancellationToken)
        {
            var location = file.IsSubagent ? "subagent transcript" : "session transcript";
            var record = new TranscriptFileUsage(file.Key, file.Length, file.LastWriteUtcTicks);
            var fileKeys = new List<ulong>();
            DateTimeOffset? firstTimestamp = null;

            using (var stream = new FileStream(
                file.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true, 64 * 1024))
            {
                var lengthBeforeRead = stream.Length;
                var lineNumber = 0;
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if ((lineNumber & 127) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (line.Length == 0) continue;

                    IDictionary<string, object> entry;
                    try
                    {
                        entry = MiniJsonParser.Parse(line) as IDictionary<string, object>;
                    }
                    catch (MiniJsonException)
                    {
                        // Claude Code may have exposed a final, not-yet-complete line. Treat that
                        // as an incomplete scan so the caller retries and keeps its last total.
                        var isPartialFinalLine = reader.Peek() < 0
                                                 && !EndsWithLineBreak(stream, lengthBeforeRead);
                        warnings.Add(
                            isPartialFinalLine ? "transcript.partial_final_line" : "transcript.line_invalid",
                            isPartialFinalLine
                                ? "A transcript was still being written during this scan."
                                : "A malformed transcript line was skipped.",
                            location + " line " + lineNumber.ToString(CultureInfo.InvariantCulture),
                            isPartialFinalLine ? WarningSeverity.Warning : WarningSeverity.Information);
                        continue;
                    }

                    if (entry == null) continue;

                    DateTimeOffset timestamp;
                    if (!TryReadTimestamp(entry, out timestamp)) continue;
                    if (firstTimestamp == null) firstTimestamp = timestamp;

                    var type = ReadString(entry, "type");
                    if (type == null || !MessageEntryTypes.Contains(type)) continue;

                    var date = LocalDate(timestamp, zone);
                    var day = record.Day(date);
                    if (!string.Equals(type, "assistant", StringComparison.Ordinal))
                    {
                        day.MessageCount = Numbers.Add(day.MessageCount, 1);
                        continue;
                    }

                    var message = ReadObject(entry, "message");
                    if (message == null)
                    {
                        day.MessageCount = Numbers.Add(day.MessageCount, 1);
                        continue;
                    }

                    var model = ReadString(message, "model");
                    if (string.IsNullOrWhiteSpace(model)) model = "unknown";
                    if (IsSyntheticModel(model))
                    {
                        // Locally generated placeholder responses are real transcript messages
                        // but were never billed or processed by a model.
                        day.MessageCount = Numbers.Add(day.MessageCount, 1);
                        continue;
                    }

                    // Claude Code writes one transcript line per content block and repeats the
                    // same usage payload on each of them. Counting lines therefore multiplies
                    // token totals, so usage is credited once per distinct response.
                    var messageId = ReadString(message, "id");
                    var isFirstLineOfResponse = true;
                    if (!string.IsNullOrEmpty(messageId))
                    {
                        var key = Hashing.MessageKey(messageId, ReadString(entry, "requestId"));
                        isFirstLineOfResponse = dedupKeys.Add(key);
                        if (isFirstLineOfResponse) fileKeys.Add(key);
                    }

                    var bucket = day.Model(model);
                    bucket.ToolCalls = Numbers.Add(bucket.ToolCalls, CountToolUseBlocks(message));
                    if (!isFirstLineOfResponse) continue;

                    day.MessageCount = Numbers.Add(day.MessageCount, 1);
                    bucket.Responses = Numbers.Add(bucket.Responses, 1);

                    var usage = ReadObject(message, "usage");
                    if (usage == null) continue;

                    bucket.Input = Numbers.Add(
                        bucket.Input,
                        ReadCounter(usage, "input_tokens", location, lineNumber, warnings));
                    bucket.Output = Numbers.Add(
                        bucket.Output,
                        ReadCounter(usage, "output_tokens", location, lineNumber, warnings));
                    bucket.CacheRead = Numbers.Add(
                        bucket.CacheRead,
                        ReadCounter(usage, "cache_read_input_tokens", location, lineNumber, warnings));
                    bucket.CacheCreation = Numbers.Add(
                        bucket.CacheCreation,
                        ReadCounter(usage, "cache_creation_input_tokens", location, lineNumber, warnings));

                    var serverTools = ReadObject(usage, "server_tool_use");
                    if (serverTools != null)
                    {
                        bucket.WebSearches = Numbers.Add(
                            bucket.WebSearches,
                            ReadCounter(serverTools, "web_search_requests", location, lineNumber, warnings));
                    }
                }

                if (stream.Length != lengthBeforeRead)
                {
                    warnings.Add(
                        "transcript.changed_during_read",
                        "A transcript changed while it was being read during this scan.",
                        location,
                        WarningSeverity.Warning);
                }
            }

            // Every main transcript is one session, dated by its first entry.
            if (!file.IsSubagent && firstTimestamp.HasValue)
            {
                record.SessionDate = LocalDate(firstTimestamp.Value, zone);
            }

            record.SetDedupKeys(fileKeys);
            return record;
        }

        private static void AddFiles(
            IEnumerable<string> paths,
            bool isSubagent,
            IList<TranscriptFile> found,
            HashSet<string> seenPaths,
            WarningSink warnings)
        {
            foreach (var path in paths)
            {
                try
                {
                    var fullPath = Path.GetFullPath(path);
                    if (!seenPaths.Add(fullPath)) continue;
                    var info = new FileInfo(fullPath);
                    if (!info.Exists) continue;
                    found.Add(new TranscriptFile(
                        fullPath,
                        info.Length,
                        info.LastWriteTimeUtc.Ticks,
                        isSubagent));
                }
                catch (Exception error) when (IsRecoverableIoException(error))
                {
                    warnings.Add(
                        "transcript.read_failed",
                        "A transcript could not be inspected during this scan.",
                        isSubagent ? "subagent transcript" : "session transcript",
                        WarningSeverity.Warning);
                }
            }
        }

        private static string LocalDate(DateTimeOffset timestamp, TimeZoneInfo zone)
        {
            var local = TimeZoneInfo.ConvertTime(timestamp, zone);
            return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        private static bool IsSyntheticModel(string model)
        {
            return string.Equals(model, "<synthetic>", StringComparison.OrdinalIgnoreCase)
                || string.Equals(model, "synthetic", StringComparison.OrdinalIgnoreCase);
        }

        private static long CountToolUseBlocks(IDictionary<string, object> message)
        {
            object contentValue;
            var content = message.TryGetValue("content", out contentValue) ? contentValue as IList<object> : null;
            if (content == null) return 0;

            long count = 0;
            foreach (var value in content)
            {
                var block = value as IDictionary<string, object>;
                if (block == null) continue;
                if (string.Equals(ReadString(block, "type"), "tool_use", StringComparison.Ordinal))
                {
                    count = Numbers.Add(count, 1);
                }
            }

            return count;
        }

        private static bool TryReadTimestamp(IDictionary<string, object> entry, out DateTimeOffset timestamp)
        {
            var raw = ReadString(entry, "timestamp");
            if (raw == null)
            {
                timestamp = default(DateTimeOffset);
                return false;
            }

            return DateTimeOffset.TryParse(
                raw,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out timestamp);
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

        private static long ReadCounter(
            IDictionary<string, object> source,
            string property,
            string location,
            int lineNumber,
            WarningSink warnings)
        {
            object value;
            if (!source.TryGetValue(property, out value) || value == null) return 0;

            var number = value as JsonNumber;
            var raw = number == null ? value as string : number.Raw;
            long parsed;
            if (raw == null
                || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                || parsed < 0)
            {
                warnings.Add(
                    "transcript.usage_invalid",
                    property + " was not a non-negative whole number and was treated as zero.",
                    location + " line " + lineNumber.ToString(CultureInfo.InvariantCulture),
                    WarningSeverity.Information);
                return 0;
            }

            return parsed;
        }

        private static bool EndsWithLineBreak(FileStream stream, long originalLength)
        {
            if (originalLength <= 0) return true;
            var readPosition = stream.Position;
            try
            {
                stream.Seek(originalLength - 1, SeekOrigin.Begin);
                var lastByte = stream.ReadByte();
                return lastByte == '\n' || lastByte == '\r';
            }
            finally
            {
                stream.Seek(readPosition, SeekOrigin.Begin);
            }
        }

        private static bool SafeDirectoryExists(string path)
        {
            try
            {
                return Directory.Exists(path);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsRecoverableIoException(Exception error)
        {
            return error is IOException
                || error is UnauthorizedAccessException
                || error is DirectoryNotFoundException
                || error is PathTooLongException
                || error is NotSupportedException
                || error is System.Security.SecurityException;
        }

        private sealed class WarningSink
        {
            private readonly int _maximum;
            private readonly List<UsageWarning> _warnings = new List<UsageWarning>();
            private int _suppressed;

            internal WarningSink(int maximum)
            {
                _maximum = maximum;
            }

            internal void Add(string code, string message, string location, WarningSeverity severity)
            {
                Add(new UsageWarning(code, message, location, severity));
            }

            internal void Add(UsageWarning warning)
            {
                if (_warnings.Count < _maximum)
                {
                    _warnings.Add(warning);
                }
                else
                {
                    _suppressed++;
                }
            }

            internal IList<UsageWarning> ToList()
            {
                var result = _warnings.ToList();
                if (_suppressed > 0)
                {
                    result.Add(new UsageWarning(
                        "scan.warnings_suppressed",
                        _suppressed.ToString(CultureInfo.InvariantCulture) + " additional scan warnings were suppressed.",
                        string.Empty,
                        WarningSeverity.Information));
                }

                return result;
            }
        }
    }

    /// <summary>One transcript file a scan can read.</summary>
    public sealed class TranscriptFile
    {
        internal TranscriptFile(string path, long length, long lastWriteUtcTicks, bool isSubagent)
        {
            Path = path;
            Length = length;
            LastWriteUtcTicks = lastWriteUtcTicks;
            IsSubagent = isSubagent;
            Key = Hashing.PathKey(path);
        }

        public string Path { get; }

        public long Length { get; }

        public long LastWriteUtcTicks { get; }

        public bool IsSubagent { get; }

        /// <summary>Opaque index key derived from the path.</summary>
        public string Key { get; }
    }

    public sealed class TranscriptScanResult
    {
        internal TranscriptScanResult(TranscriptIndex index, UsageHistory history, ScanReport report)
        {
            Index = index;
            History = history;
            Report = report;
        }

        /// <summary>The refreshed incremental index. Pass it to the next scan.</summary>
        public TranscriptIndex Index { get; }

        /// <summary>Usage found in transcripts that still exist on disk.</summary>
        public UsageHistory History { get; }

        public ScanReport Report { get; }
    }
}
