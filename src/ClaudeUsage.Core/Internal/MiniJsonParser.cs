using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ClaudeUsage.Core.Internal
{
    internal sealed class JsonNumber
    {
        internal JsonNumber(string raw)
        {
            Raw = raw;
        }

        internal string Raw { get; }
    }

    internal sealed class MiniJsonException : Exception
    {
        internal MiniJsonException(string message, int position)
            : base(message + " (character " + position.ToString(CultureInfo.InvariantCulture) + ")")
        {
            Position = position;
        }

        internal int Position { get; }
    }

    /// <summary>
    /// Small, allocation-conscious JSON reader used to keep the netstandard2.0 core free of
    /// package dependencies. Schema interpretation remains in StatsCacheParser.
    /// </summary>
    internal sealed class MiniJsonParser
    {
        private const int MaximumDepth = 128;

        private readonly string _text;
        private int _position;

        private MiniJsonParser(string text)
        {
            _text = text;
        }

        internal static object Parse(string text)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            var parser = new MiniJsonParser(text);
            parser.SkipWhiteSpace();
            var value = parser.ReadValue(0);
            parser.SkipWhiteSpace();
            if (!parser.IsAtEnd)
            {
                throw parser.Error("Unexpected content after the root JSON value");
            }

            return value;
        }

        private bool IsAtEnd => _position >= _text.Length;

        private char Current => IsAtEnd ? '\0' : _text[_position];

        private object ReadValue(int depth)
        {
            if (depth > MaximumDepth)
            {
                throw Error("JSON nesting exceeds the supported depth");
            }

            if (IsAtEnd)
            {
                throw Error("Expected a JSON value");
            }

            switch (Current)
            {
                case '{':
                    return ReadObject(depth + 1);
                case '[':
                    return ReadArray(depth + 1);
                case '"':
                    return ReadString();
                case 't':
                    ReadLiteral("true");
                    return true;
                case 'f':
                    ReadLiteral("false");
                    return false;
                case 'n':
                    ReadLiteral("null");
                    return null;
                default:
                    if (Current == '-' || IsDigit(Current))
                    {
                        return ReadNumber();
                    }

                    throw Error("Unexpected character while reading a JSON value");
            }
        }

        private IDictionary<string, object> ReadObject(int depth)
        {
            Expect('{');
            SkipWhiteSpace();
            var result = new Dictionary<string, object>(StringComparer.Ordinal);

            if (TryConsume('}'))
            {
                return result;
            }

            while (true)
            {
                if (Current != '"')
                {
                    throw Error("Expected a quoted object property name");
                }

                var name = ReadString();
                SkipWhiteSpace();
                Expect(':');
                SkipWhiteSpace();
                result[name] = ReadValue(depth);
                SkipWhiteSpace();

                if (TryConsume('}'))
                {
                    return result;
                }

                Expect(',');
                SkipWhiteSpace();
            }
        }

        private IList<object> ReadArray(int depth)
        {
            Expect('[');
            SkipWhiteSpace();
            var result = new List<object>();

            if (TryConsume(']'))
            {
                return result;
            }

            while (true)
            {
                result.Add(ReadValue(depth));
                SkipWhiteSpace();

                if (TryConsume(']'))
                {
                    return result;
                }

                Expect(',');
                SkipWhiteSpace();
            }
        }

        private string ReadString()
        {
            Expect('"');
            var builder = new StringBuilder();

            while (!IsAtEnd)
            {
                var value = _text[_position++];
                if (value == '"')
                {
                    return builder.ToString();
                }

                if (value == '\\')
                {
                    if (IsAtEnd)
                    {
                        throw Error("Unterminated escape sequence in JSON string");
                    }

                    var escaped = _text[_position++];
                    switch (escaped)
                    {
                        case '"':
                        case '\\':
                        case '/':
                            builder.Append(escaped);
                            break;
                        case 'b':
                            builder.Append('\b');
                            break;
                        case 'f':
                            builder.Append('\f');
                            break;
                        case 'n':
                            builder.Append('\n');
                            break;
                        case 'r':
                            builder.Append('\r');
                            break;
                        case 't':
                            builder.Append('\t');
                            break;
                        case 'u':
                            builder.Append(ReadUnicodeEscape());
                            break;
                        default:
                            throw Error("Invalid JSON string escape sequence");
                    }
                }
                else
                {
                    if (value < 0x20)
                    {
                        throw Error("Unescaped control character in JSON string");
                    }

                    builder.Append(value);
                }
            }

            throw Error("Unterminated JSON string");
        }

        private char ReadUnicodeEscape()
        {
            if (_position + 4 > _text.Length)
            {
                throw Error("Incomplete Unicode escape sequence");
            }

            var value = 0;
            for (var index = 0; index < 4; index++)
            {
                var digit = HexValue(_text[_position++]);
                if (digit < 0)
                {
                    throw Error("Invalid hexadecimal digit in Unicode escape sequence");
                }

                value = (value * 16) + digit;
            }

            return (char)value;
        }

        private JsonNumber ReadNumber()
        {
            var start = _position;
            TryConsume('-');

            if (TryConsume('0'))
            {
                if (!IsAtEnd && IsDigit(Current))
                {
                    throw Error("Leading zero is not valid in a JSON number");
                }
            }
            else
            {
                ReadDigits("Expected a digit in JSON number");
            }

            if (TryConsume('.'))
            {
                ReadDigits("Expected digits after the decimal point");
            }

            if (TryConsume('e') || TryConsume('E'))
            {
                if (!TryConsume('+'))
                {
                    TryConsume('-');
                }

                ReadDigits("Expected digits in the JSON number exponent");
            }

            return new JsonNumber(_text.Substring(start, _position - start));
        }

        private void ReadDigits(string errorMessage)
        {
            var start = _position;
            while (!IsAtEnd && IsDigit(Current))
            {
                _position++;
            }

            if (_position == start)
            {
                throw Error(errorMessage);
            }
        }

        private void ReadLiteral(string literal)
        {
            for (var index = 0; index < literal.Length; index++)
            {
                if (IsAtEnd || Current != literal[index])
                {
                    throw Error("Invalid JSON literal");
                }

                _position++;
            }
        }

        private void SkipWhiteSpace()
        {
            while (!IsAtEnd)
            {
                switch (Current)
                {
                    case ' ':
                    case '\t':
                    case '\r':
                    case '\n':
                        _position++;
                        continue;
                    default:
                        return;
                }
            }
        }

        private void Expect(char expected)
        {
            if (IsAtEnd || Current != expected)
            {
                throw Error("Expected '" + expected + "'");
            }

            _position++;
        }

        private bool TryConsume(char expected)
        {
            if (IsAtEnd || Current != expected)
            {
                return false;
            }

            _position++;
            return true;
        }

        private MiniJsonException Error(string message)
        {
            return new MiniJsonException(message, _position);
        }

        private static bool IsDigit(char value)
        {
            return value >= '0' && value <= '9';
        }

        private static int HexValue(char value)
        {
            if (value >= '0' && value <= '9')
            {
                return value - '0';
            }

            if (value >= 'a' && value <= 'f')
            {
                return value - 'a' + 10;
            }

            if (value >= 'A' && value <= 'F')
            {
                return value - 'A' + 10;
            }

            return -1;
        }
    }
}
