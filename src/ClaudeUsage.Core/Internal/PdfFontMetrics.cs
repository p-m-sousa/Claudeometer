using System.Text;

namespace ClaudeUsage.Core.Internal
{
    internal enum PdfFont
    {
        Regular,
        Bold
    }

    /// <summary>
    /// Advance widths for the two standard Type 1 fonts the report uses, in 1/1000 em. Every PDF
    /// reader ships Helvetica, so the report needs no embedded font file and stays small.
    /// </summary>
    internal static class PdfFontMetrics
    {
        private const int FirstChar = 32;
        private const int FallbackWidth = 556;

        private static readonly int[] Helvetica =
        {
            278, 278, 355, 556, 556, 889, 667, 191, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            278, 278, 584, 584, 584, 556, 1015,
            667, 667, 722, 722, 667, 611, 778, 722, 278, 500, 667, 556, 833, 722, 778, 667,
            778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            278, 278, 278, 469, 556, 333,
            556, 556, 500, 556, 556, 278, 556, 556, 222, 222, 500, 222, 833, 556, 556, 556,
            556, 333, 500, 278, 556, 500, 722, 500, 500, 500,
            334, 260, 334, 584
        };

        private static readonly int[] HelveticaBold =
        {
            278, 333, 474, 556, 556, 889, 722, 238, 333, 333, 389, 584, 278, 333, 278, 278,
            556, 556, 556, 556, 556, 556, 556, 556, 556, 556,
            333, 333, 584, 584, 584, 611, 975,
            722, 722, 722, 722, 667, 611, 778, 722, 278, 556, 722, 611, 833, 722, 778, 667,
            778, 722, 667, 611, 722, 667, 944, 667, 667, 611,
            333, 278, 333, 584, 556, 333,
            556, 611, 556, 611, 556, 333, 611, 611, 278, 278, 556, 278, 889, 611, 611, 611,
            611, 389, 556, 333, 611, 556, 778, 556, 556, 500,
            389, 280, 389, 584
        };

        /// <summary>
        /// Replaces characters that would break WinAnsi text or width measurement. Cultures that
        /// group digits with a non-breaking space are normalised so numeric columns still align.
        /// </summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            var builder = new StringBuilder(text.Length);
            foreach (var character in text)
            {
                switch (character)
                {
                    case ' ':
                    case ' ':
                    case ' ':
                    case '\t':
                        builder.Append(' ');
                        break;
                    case '‘':
                    case '’':
                        builder.Append('\'');
                        break;
                    case '“':
                    case '”':
                        builder.Append('"');
                        break;
                    case '–':
                    case '—':
                        builder.Append('-');
                        break;
                    case '·':
                    case '•':
                        builder.Append('-');
                        break;
                    default:
                        if (character < 0x20)
                        {
                            builder.Append(' ');
                        }
                        else if (character > 0xFF)
                        {
                            builder.Append('?');
                        }
                        else
                        {
                            builder.Append(character);
                        }

                        break;
                }
            }

            return builder.ToString();
        }

        /// <summary>Width of already-normalised text at the given point size.</summary>
        internal static double Measure(string text, PdfFont font, double size)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            var widths = font == PdfFont.Bold ? HelveticaBold : Helvetica;
            var total = 0;
            foreach (var character in text)
            {
                var index = character - FirstChar;
                total += index >= 0 && index < widths.Length ? widths[index] : FallbackWidth;
            }

            return total * size / 1000D;
        }

        /// <summary>Trims text with a trailing ellipsis so it fits the available width.</summary>
        internal static string Fit(string text, PdfFont font, double size, double maximumWidth)
        {
            var normalized = Normalize(text);
            if (Measure(normalized, font, size) <= maximumWidth) return normalized;
            const string Ellipsis = "...";
            var ellipsisWidth = Measure(Ellipsis, font, size);
            var builder = new StringBuilder();
            double width = 0;
            foreach (var character in normalized)
            {
                var next = Measure(character.ToString(), font, size);
                if (width + next + ellipsisWidth > maximumWidth) break;
                builder.Append(character);
                width += next;
            }

            return builder.ToString() + Ellipsis;
        }
    }
}
