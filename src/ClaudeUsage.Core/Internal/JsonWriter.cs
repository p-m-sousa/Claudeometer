using System;
using System.Globalization;
using System.Text;

namespace ClaudeUsage.Core.Internal
{
    /// <summary>
    /// Minimal JSON writer, so the core keeps no package dependencies. Emits compact,
    /// deterministic output suitable for a small local store file.
    /// </summary>
    internal sealed class JsonWriter
    {
        private readonly StringBuilder _builder = new StringBuilder();
        private bool _needsComma;

        internal JsonWriter StartObject()
        {
            Separate();
            _builder.Append('{');
            _needsComma = false;
            return this;
        }

        internal JsonWriter EndObject()
        {
            _builder.Append('}');
            _needsComma = true;
            return this;
        }

        internal JsonWriter Name(string name)
        {
            Separate();
            WriteString(name);
            _builder.Append(':');
            _needsComma = false;
            return this;
        }

        internal JsonWriter Value(string value)
        {
            Separate();
            if (value == null)
            {
                _builder.Append("null");
            }
            else
            {
                WriteString(value);
            }

            _needsComma = true;
            return this;
        }

        internal JsonWriter Value(long value)
        {
            Separate();
            _builder.Append(value.ToString(CultureInfo.InvariantCulture));
            _needsComma = true;
            return this;
        }

        internal JsonWriter Property(string name, string value)
        {
            return Name(name).Value(value);
        }

        internal JsonWriter Property(string name, long value)
        {
            return Name(name).Value(value);
        }

        public override string ToString()
        {
            return _builder.ToString();
        }

        private void Separate()
        {
            if (_needsComma) _builder.Append(',');
        }

        private void WriteString(string value)
        {
            _builder.Append('"');
            foreach (var character in value)
            {
                switch (character)
                {
                    case '"':
                        _builder.Append("\\\"");
                        break;
                    case '\\':
                        _builder.Append("\\\\");
                        break;
                    case '\b':
                        _builder.Append("\\b");
                        break;
                    case '\f':
                        _builder.Append("\\f");
                        break;
                    case '\n':
                        _builder.Append("\\n");
                        break;
                    case '\r':
                        _builder.Append("\\r");
                        break;
                    case '\t':
                        _builder.Append("\\t");
                        break;
                    default:
                        if (character < 0x20 || character > 0x7E)
                        {
                            _builder.Append("\\u");
                            _builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            _builder.Append(character);
                        }

                        break;
                }
            }

            _builder.Append('"');
        }
    }
}
