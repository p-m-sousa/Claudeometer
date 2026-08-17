using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ClaudeUsage.Core.Internal
{
    internal struct PdfColor
    {
        internal PdfColor(int red, int green, int blue)
        {
            Red = red / 255D;
            Green = green / 255D;
            Blue = blue / 255D;
        }

        internal double Red { get; }

        internal double Green { get; }

        internal double Blue { get; }

        internal string ToOperand()
        {
            return Number(Red) + " " + Number(Green) + " " + Number(Blue);
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// A single page's content stream. Coordinates are in points measured from the top-left
    /// corner, which keeps report layout code reading top to bottom; the PDF's bottom-left
    /// origin is applied on the way out.
    /// </summary>
    internal sealed class PdfPage
    {
        private readonly StringBuilder _content = new StringBuilder();
        private readonly double _height;

        internal PdfPage(double width, double height)
        {
            Width = width;
            _height = height;
        }

        internal double Width { get; }

        internal double Height
        {
            get { return _height; }
        }

        /// <summary>Draws text with its baseline at <paramref name="baselineY"/> below the page top.</summary>
        internal void Text(double x, double baselineY, string text, PdfFont font, double size, PdfColor color)
        {
            var normalized = PdfFontMetrics.Normalize(text);
            if (normalized.Length == 0) return;
            _content.Append("BT ")
                .Append(color.ToOperand())
                .Append(" rg /")
                .Append(font == PdfFont.Bold ? "F2 " : "F1 ")
                .Append(Number(size))
                .Append(" Tf 1 0 0 1 ")
                .Append(Number(x))
                .Append(' ')
                .Append(Number(_height - baselineY))
                .Append(" Tm (")
                .Append(Escape(normalized))
                .Append(") Tj ET\n");
        }

        internal void TextRight(double right, double baselineY, string text, PdfFont font, double size, PdfColor color)
        {
            var normalized = PdfFontMetrics.Normalize(text);
            Text(right - PdfFontMetrics.Measure(normalized, font, size), baselineY, normalized, font, size, color);
        }

        internal void TextCenter(double center, double baselineY, string text, PdfFont font, double size, PdfColor color)
        {
            var normalized = PdfFontMetrics.Normalize(text);
            Text(center - (PdfFontMetrics.Measure(normalized, font, size) / 2D), baselineY, normalized, font, size, color);
        }

        internal void FillRect(double x, double y, double width, double height, PdfColor color)
        {
            if (width <= 0 || height <= 0) return;
            _content.Append(color.ToOperand())
                .Append(" rg ")
                .Append(Number(x))
                .Append(' ')
                .Append(Number(_height - y - height))
                .Append(' ')
                .Append(Number(width))
                .Append(' ')
                .Append(Number(height))
                .Append(" re f\n");
        }

        internal void Line(double x1, double y1, double x2, double y2, double width, PdfColor color)
        {
            _content.Append(color.ToOperand())
                .Append(" RG ")
                .Append(Number(width))
                .Append(" w ")
                .Append(Number(x1))
                .Append(' ')
                .Append(Number(_height - y1))
                .Append(" m ")
                .Append(Number(x2))
                .Append(' ')
                .Append(Number(_height - y2))
                .Append(" l S\n");
        }

        internal string Content
        {
            get { return _content.ToString(); }
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Escape(string text)
        {
            var builder = new StringBuilder(text.Length + 8);
            foreach (var character in text)
            {
                if (character == '(' || character == ')' || character == '\\') builder.Append('\\');
                builder.Append(character);
            }

            return builder.ToString();
        }
    }

    /// <summary>
    /// Writes a PDF 1.4 file containing text, rules, and filled rectangles using the standard
    /// Helvetica fonts. Hand-rolled so the app stays a dependency-free, no-installer executable.
    /// </summary>
    internal sealed class PdfBuilder
    {
        private readonly List<PdfPage> _pages = new List<PdfPage>();
        private readonly double _width;
        private readonly double _height;

        internal PdfBuilder(double width, double height)
        {
            _width = width;
            _height = height;
        }

        internal string Title { get; set; }

        internal IReadOnlyList<PdfPage> Pages
        {
            get { return _pages; }
        }

        internal PdfPage AddPage()
        {
            var page = new PdfPage(_width, _height);
            _pages.Add(page);
            return page;
        }

        internal void Save(Stream stream)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (_pages.Count == 0) AddPage();

            // Object numbering: 1 catalog, 2 page tree, 3 and 4 fonts, 5 document information,
            // then a page object and a content stream object for each page.
            const int Catalog = 1;
            const int PageTree = 2;
            const int RegularFont = 3;
            const int BoldFont = 4;
            const int Information = 5;
            var firstPageObject = 6;

            var bodies = new List<byte[]>();
            var kids = new StringBuilder();
            for (var index = 0; index < _pages.Count; index++)
            {
                if (index > 0) kids.Append(' ');
                kids.Append((firstPageObject + (index * 2)).ToString(CultureInfo.InvariantCulture)).Append(" 0 R");
            }

            AddObject(bodies, Catalog, "<< /Type /Catalog /Pages " + PageTree + " 0 R >>");
            AddObject(
                bodies,
                PageTree,
                "<< /Type /Pages /Count " + _pages.Count.ToString(CultureInfo.InvariantCulture) +
                " /Kids [" + kids + "] >>");
            AddObject(
                bodies,
                RegularFont,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            AddObject(
                bodies,
                BoldFont,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>");
            AddObject(
                bodies,
                Information,
                "<< /Title (" + EscapeMetadata(Title ?? "Usage report") + ") /Producer (Claudeometer) /Creator (Claudeometer) /CreationDate (" +
                FormatPdfDate(DateTimeOffset.Now) + ") >>");

            for (var index = 0; index < _pages.Count; index++)
            {
                var pageObject = firstPageObject + (index * 2);
                var contentObject = pageObject + 1;
                AddObject(
                    bodies,
                    pageObject,
                    "<< /Type /Page /Parent " + PageTree + " 0 R /MediaBox [0 0 " +
                    Number(_width) + " " + Number(_height) + "] /Resources << /Font << /F1 " +
                    RegularFont + " 0 R /F2 " + BoldFont + " 0 R >> >> /Contents " +
                    contentObject + " 0 R >>");

                var contentBytes = Latin1(_pages[index].Content);
                AddStreamObject(bodies, contentObject, contentBytes);
            }

            WriteDocument(stream, bodies, Catalog, Information);
        }

        private static void WriteDocument(Stream stream, IList<byte[]> bodies, int catalogObject, int informationObject)
        {
            var header = Latin1("%PDF-1.4\n%âãÏÓ\n");
            stream.Write(header, 0, header.Length);
            var position = (long)header.Length;
            var offsets = new long[bodies.Count + 1];

            for (var index = 0; index < bodies.Count; index++)
            {
                offsets[index + 1] = position;
                stream.Write(bodies[index], 0, bodies[index].Length);
                position += bodies[index].Length;
            }

            var xrefPosition = position;
            var xref = new StringBuilder();
            xref.Append("xref\n0 ")
                .Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
                .Append('\n')
                .Append("0000000000 65535 f \n");
            for (var index = 1; index <= bodies.Count; index++)
            {
                xref.Append(offsets[index].ToString("D10", CultureInfo.InvariantCulture)).Append(" 00000 n \n");
            }

            xref.Append("trailer\n<< /Size ")
                .Append((bodies.Count + 1).ToString(CultureInfo.InvariantCulture))
                .Append(" /Root ")
                .Append(catalogObject.ToString(CultureInfo.InvariantCulture))
                .Append(" 0 R /Info ")
                .Append(informationObject.ToString(CultureInfo.InvariantCulture))
                .Append(" 0 R >>\nstartxref\n")
                .Append(xrefPosition.ToString(CultureInfo.InvariantCulture))
                .Append("\n%%EOF\n");
            var trailerBytes = Latin1(xref.ToString());
            stream.Write(trailerBytes, 0, trailerBytes.Length);
        }

        private static void AddObject(IList<byte[]> bodies, int number, string body)
        {
            bodies.Add(Latin1(
                number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n" + body + "\nendobj\n"));
        }

        private static void AddStreamObject(IList<byte[]> bodies, int number, byte[] content)
        {
            var prefix = Latin1(
                number.ToString(CultureInfo.InvariantCulture) + " 0 obj\n<< /Length " +
                content.Length.ToString(CultureInfo.InvariantCulture) + " >>\nstream\n");
            var suffix = Latin1("\nendstream\nendobj\n");
            var combined = new byte[prefix.Length + content.Length + suffix.Length];
            Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
            Buffer.BlockCopy(content, 0, combined, prefix.Length, content.Length);
            Buffer.BlockCopy(suffix, 0, combined, prefix.Length + content.Length, suffix.Length);
            bodies.Add(combined);
        }

        /// <summary>
        /// WinAnsi is a superset of Latin-1 for the characters this writer emits, so one byte per
        /// character keeps stream lengths exact.
        /// </summary>
        private static byte[] Latin1(string text)
        {
            var bytes = new byte[text.Length];
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                bytes[index] = character <= 0xFF ? (byte)character : (byte)'?';
            }

            return bytes;
        }

        private static string EscapeMetadata(string text)
        {
            var normalized = PdfFontMetrics.Normalize(text);
            var builder = new StringBuilder(normalized.Length);
            foreach (var character in normalized)
            {
                if (character == '(' || character == ')' || character == '\\') builder.Append('\\');
                builder.Append(character);
            }

            return builder.ToString();
        }

        private static string FormatPdfDate(DateTimeOffset value)
        {
            var offset = value.Offset;
            var sign = offset.Ticks < 0 ? "-" : "+";
            return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + sign +
                   Math.Abs(offset.Hours).ToString("00", CultureInfo.InvariantCulture) + "'" +
                   Math.Abs(offset.Minutes).ToString("00", CultureInfo.InvariantCulture) + "'";
        }

        private static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
