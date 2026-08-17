using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUsage.Core
{
    /// <summary>
    /// Per-transcript scan results, keyed by an opaque hash of the file path. Holding these
    /// between refreshes lets a scan skip every session that has not changed, which keeps a
    /// 30-second refresh cheap even with a large transcript history.
    /// </summary>
    public sealed class TranscriptIndex
    {
        private readonly Dictionary<string, TranscriptFileUsage> _files =
            new Dictionary<string, TranscriptFileUsage>(StringComparer.Ordinal);

        /// <summary>Number of transcripts described by this index.</summary>
        public int Count
        {
            get { return _files.Count; }
        }

        internal IEnumerable<TranscriptFileUsage> Files
        {
            get { return _files.Values; }
        }

        internal void Put(TranscriptFileUsage record)
        {
            if (record == null) return;
            _files[record.Key] = record;
        }

        internal TranscriptFileUsage Find(string key)
        {
            TranscriptFileUsage record;
            return key != null && _files.TryGetValue(key, out record) ? record : null;
        }

        /// <summary>Folds every indexed transcript into one day-by-day history.</summary>
        internal UsageHistory BuildHistory()
        {
            var days = new Dictionary<string, DayBuilder>(StringComparer.Ordinal);
            foreach (var file in _files.Values)
            {
                foreach (var pair in file.Days)
                {
                    var day = GetDay(days, pair.Key);
                    day.MessageCount = Numbers.Add(day.MessageCount, pair.Value.MessageCount);
                    foreach (var modelPair in pair.Value.Models)
                    {
                        day.Model(modelPair.Key).AddFrom(modelPair.Value);
                    }
                }

                if (file.SessionDate != null)
                {
                    var sessionDay = GetDay(days, file.SessionDate);
                    sessionDay.SessionCount = Numbers.Add(sessionDay.SessionCount, 1);
                }
            }

            return new UsageHistory(days.Values.Select(day => day.Build()));
        }

        private static DayBuilder GetDay(IDictionary<string, DayBuilder> days, string date)
        {
            DayBuilder day;
            if (!days.TryGetValue(date, out day))
            {
                day = new DayBuilder(date);
                days.Add(date, day);
            }

            return day;
        }
    }

    /// <summary>Everything one transcript file contributed, as of a known length and write time.</summary>
    internal sealed class TranscriptFileUsage
    {
        private static readonly ulong[] NoKeys = new ulong[0];

        private readonly Dictionary<string, DayBuilder> _days =
            new Dictionary<string, DayBuilder>(StringComparer.Ordinal);

        internal TranscriptFileUsage(string key, long length, long lastWriteUtcTicks)
        {
            Key = key;
            Length = length;
            LastWriteUtcTicks = lastWriteUtcTicks;
            DedupKeys = NoKeys;
        }

        internal string Key { get; }

        internal long Length { get; }

        internal long LastWriteUtcTicks { get; }

        /// <summary>Local date of the transcript's first entry, or null for subagent transcripts.</summary>
        internal string SessionDate { get; set; }

        internal IDictionary<string, DayBuilder> Days
        {
            get { return _days; }
        }

        /// <summary>
        /// Response keys whose usage this file's totals include. They let a later scan skip a
        /// response that a different transcript already accounted for.
        /// </summary>
        internal ulong[] DedupKeys { get; private set; }

        internal void SetDedupKeys(IEnumerable<ulong> keys)
        {
            DedupKeys = keys == null ? NoKeys : keys.ToArray();
        }

        internal bool Matches(long length, long lastWriteUtcTicks)
        {
            return Length == length && LastWriteUtcTicks == lastWriteUtcTicks;
        }

        internal DayBuilder Day(string date)
        {
            DayBuilder day;
            if (!_days.TryGetValue(date, out day))
            {
                day = new DayBuilder(date);
                _days.Add(date, day);
            }

            return day;
        }
    }

    internal sealed class DayBuilder
    {
        private readonly Dictionary<string, ModelBuilder> _models =
            new Dictionary<string, ModelBuilder>(StringComparer.Ordinal);

        internal DayBuilder(string date)
        {
            Date = date;
        }

        internal string Date { get; }

        internal long MessageCount { get; set; }

        internal long SessionCount { get; set; }

        internal IDictionary<string, ModelBuilder> Models
        {
            get { return _models; }
        }

        internal ModelBuilder Model(string modelId)
        {
            ModelBuilder model;
            if (!_models.TryGetValue(modelId, out model))
            {
                model = new ModelBuilder(modelId);
                _models.Add(modelId, model);
            }

            return model;
        }

        internal UsageDay Build()
        {
            var models = new Dictionary<string, ModelUsage>(StringComparer.Ordinal);
            foreach (var pair in _models)
            {
                models[pair.Key] = pair.Value.Build();
            }

            return new UsageDay(Date, models, SessionCount, MessageCount);
        }
    }

    internal sealed class ModelBuilder
    {
        internal ModelBuilder(string modelId)
        {
            ModelId = modelId;
        }

        internal string ModelId { get; }

        internal long Input { get; set; }

        internal long Output { get; set; }

        internal long CacheRead { get; set; }

        internal long CacheCreation { get; set; }

        internal long Responses { get; set; }

        internal long ToolCalls { get; set; }

        internal long WebSearches { get; set; }

        internal void AddFrom(ModelBuilder other)
        {
            Input = Numbers.Add(Input, other.Input);
            Output = Numbers.Add(Output, other.Output);
            CacheRead = Numbers.Add(CacheRead, other.CacheRead);
            CacheCreation = Numbers.Add(CacheCreation, other.CacheCreation);
            Responses = Numbers.Add(Responses, other.Responses);
            ToolCalls = Numbers.Add(ToolCalls, other.ToolCalls);
            WebSearches = Numbers.Add(WebSearches, other.WebSearches);
        }

        internal ModelUsage Build()
        {
            return new ModelUsage(
                ModelId,
                new TokenTotals(Input, Output, CacheRead, CacheCreation),
                Responses,
                ToolCalls,
                WebSearches);
        }
    }
}
