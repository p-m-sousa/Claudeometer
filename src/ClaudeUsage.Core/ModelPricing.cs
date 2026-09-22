using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using ClaudeUsage.Core.Internal;

namespace ClaudeUsage.Core
{
    /// <summary>USD per million tokens, starting on an inclusive local calendar date.</summary>
    public sealed class ModelPrice
    {
        public ModelPrice(string modelId, string effectiveDate, decimal input, decimal output,
            decimal cacheWrite, decimal cacheRead)
        {
            if (string.IsNullOrWhiteSpace(modelId) || modelId.Any(char.IsControl))
                throw new ArgumentException("Enter a model identifier without control characters.", nameof(modelId));
            ModelId = modelId.Trim();
            EffectiveDate = DateKey.ValidateBound(effectiveDate, nameof(effectiveDate));
            Input = ValidateRate(input);
            Output = ValidateRate(output);
            CacheWrite = ValidateRate(cacheWrite);
            CacheRead = ValidateRate(cacheRead);
        }

        public string ModelId { get; }
        public string EffectiveDate { get; }
        public decimal Input { get; }
        public decimal Output { get; }
        public decimal CacheWrite { get; }
        public decimal CacheRead { get; }

        private static decimal ValidateRate(decimal value)
        {
            if (value < 0 || value > 1000000M || decimal.Round(value, 6) != value)
                throw new ArgumentOutOfRangeException(nameof(value), "Prices must be between 0 and 1,000,000 USD with at most six decimal places.");
            return value;
        }
    }

    public sealed class PricingCatalog
    {
        public static readonly PricingCatalog Empty = new PricingCatalog(new ModelPrice[0]);
        private readonly Dictionary<string, List<ModelPrice>> _models;

        public PricingCatalog(IEnumerable<ModelPrice> prices)
        {
            var entries = (prices ?? throw new ArgumentNullException(nameof(prices))).ToList();
            if (entries.Any(value => value == null)) throw new ArgumentException("A price cannot be null.", nameof(prices));
            _models = entries.GroupBy(value => value.ModelId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => group.OrderByDescending(value => value.EffectiveDate, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
            foreach (var model in _models.Values)
                if (model.GroupBy(value => value.EffectiveDate).Any(group => group.Count() > 1))
                    throw new ArgumentException("Each model can have only one price for each effective date (including an undated baseline).");
            Prices = new ReadOnlyCollection<ModelPrice>(entries.OrderBy(value => value.ModelId, StringComparer.Ordinal)
                .ThenBy(value => value.EffectiveDate, StringComparer.Ordinal).ToList());
        }

        public IReadOnlyList<ModelPrice> Prices { get; }

        public ModelPrice Find(string modelId, string date)
        {
            if (!DateKey.IsValid(date)) throw new ArgumentException("Enter a valid usage date.", nameof(date));
            List<ModelPrice> prices;
            return modelId != null && _models.TryGetValue(modelId, out prices)
                ? prices.FirstOrDefault(price => price.EffectiveDate == null || string.CompareOrdinal(price.EffectiveDate, date) <= 0)
                : null;
        }

        public SpendTotals Calculate(string modelId, string date, TokenTotals tokens)
        {
            if (tokens == null) throw new ArgumentNullException(nameof(tokens));
            var price = Find(modelId, date);
            return new SpendTotals(
                SpendAmount.Calculate(tokens.InputTokens, price == null ? (decimal?)null : price.Input),
                SpendAmount.Calculate(tokens.OutputTokens, price == null ? (decimal?)null : price.Output),
                SpendAmount.Calculate(tokens.CacheReadTokens, price == null ? (decimal?)null : price.CacheRead),
                SpendAmount.Calculate(tokens.CacheCreationTokens, price == null ? (decimal?)null : price.CacheWrite));
        }
    }

    /// <summary>A known estimate plus explicit coverage; missing prices are never counted as free.</summary>
    public sealed class SpendAmount
    {
        public static readonly SpendAmount Zero = new SpendAmount(0, 0, 0);
        private SpendAmount(decimal knownUsd, long pricedTokens, long unpricedTokens)
        {
            KnownUsd = knownUsd;
            PricedTokens = pricedTokens;
            UnpricedTokens = unpricedTokens;
        }

        public decimal KnownUsd { get; }
        public long PricedTokens { get; }
        public long UnpricedTokens { get; }
        public bool IsPartial { get { return UnpricedTokens > 0 && PricedTokens > 0; } }

        internal static SpendAmount Calculate(long tokens, decimal? rate)
        {
            return rate.HasValue
                ? new SpendAmount(tokens / 1000000M * rate.Value, tokens, 0)
                : new SpendAmount(0, 0, tokens);
        }

        public SpendAmount Add(SpendAmount other)
        {
            return new SpendAmount(KnownUsd + other.KnownUsd,
                Numbers.Add(PricedTokens, other.PricedTokens), Numbers.Add(UnpricedTokens, other.UnpricedTokens));
        }
    }

    public sealed class SpendTotals
    {
        public static readonly SpendTotals Zero = new SpendTotals(SpendAmount.Zero, SpendAmount.Zero, SpendAmount.Zero, SpendAmount.Zero);
        internal SpendTotals(SpendAmount input, SpendAmount output, SpendAmount cacheRead, SpendAmount cacheWrite)
        {
            Input = input;
            Output = output;
            CacheRead = cacheRead;
            CacheWrite = cacheWrite;
        }

        public SpendAmount Input { get; }
        public SpendAmount Output { get; }
        public SpendAmount CacheRead { get; }
        public SpendAmount CacheWrite { get; }
        public SpendAmount InputOutput { get { return Input.Add(Output); } }
        public SpendAmount Total { get { return InputOutput.Add(CacheRead).Add(CacheWrite); } }

        public SpendTotals Add(SpendTotals other)
        {
            return new SpendTotals(Input.Add(other.Input), Output.Add(other.Output),
                CacheRead.Add(other.CacheRead), CacheWrite.Add(other.CacheWrite));
        }
    }

    /// <summary>Separate from the token archive: saving prices never rewrites recorded usage.</summary>
    public static class PricingStore
    {
        public static PricingCatalog Load(string path)
        {
            if (!File.Exists(path)) return PricingCatalog.Empty;
            // Disable DTDs and external resources; loading prices must remain entirely offline.
            using (var reader = System.Xml.XmlReader.Create(path, new System.Xml.XmlReaderSettings
            {
                DtdProcessing = System.Xml.DtdProcessing.Prohibit,
                XmlResolver = null
            }))
            {
                var root = XDocument.Load(reader).Root;
                if (root == null || root.Name != "pricing" || (string)root.Attribute("schema") != "1"
                    || (string)root.Attribute("currency") != "USD" || (string)root.Attribute("unit") != "1000000")
                    throw new FormatException("Unsupported pricing file format.");
                return new PricingCatalog(root.Elements().Select(element =>
                {
                    if (element.Name != "price") throw new FormatException("Unknown pricing entry.");
                    return new ModelPrice((string)element.Attribute("model"), (string)element.Attribute("from"),
                        ReadRate(element, "input"), ReadRate(element, "output"),
                        ReadRate(element, "cacheWrite"), ReadRate(element, "cacheRead"));
                }));
            }
        }

        public static void Save(string path, PricingCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var root = new XElement("pricing", new XAttribute("schema", "1"),
                new XAttribute("currency", "USD"), new XAttribute("unit", "1000000"));
            foreach (var price in catalog.Prices)
            {
                var element = new XElement("price", new XAttribute("model", price.ModelId),
                    new XAttribute("input", price.Input), new XAttribute("output", price.Output),
                    new XAttribute("cacheWrite", price.CacheWrite), new XAttribute("cacheRead", price.CacheRead));
                if (price.EffectiveDate != null) element.Add(new XAttribute("from", price.EffectiveDate));
                root.Add(element);
            }
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                new XDocument(root).Save(temporary);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static decimal ReadRate(XElement element, string name)
        {
            decimal value;
            if (!decimal.TryParse((string)element.Attribute(name), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value)) throw new FormatException("Invalid or missing " + name + " price.");
            return value;
        }
    }
}
