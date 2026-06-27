using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace DependencyFreezer
{

internal static class SimpleJson
{
    public static Dictionary<string, object?> ParseObject(string json)
    {
        var parser = new Parser(json);
        if (parser.ParseValue() is not Dictionary<string, object?> result)
        {
            throw new InvalidOperationException("JSON root is not an object.");
        }

        parser.EnsureComplete();
        return result;
    }

    public static string Serialize(object? value, bool writeIndented = false)
    {
        var builder = new StringBuilder();
        WriteValue(builder, value, writeIndented, 0);
        return builder.ToString();
    }

    public static string? ReadString(object? value)
        => value switch
        {
            null => null,
            string text => text,
            long number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            bool boolean => boolean ? "true" : "false",
            _ => null,
        };

    public static int? ReadInt32(object? value)
        => value switch
        {
            int number => number,
            long number when number >= int.MinValue && number <= int.MaxValue => (int)number,
            double number when number >= int.MinValue && number <= int.MaxValue && number == Math.Floor(number) => (int)number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => null,
        };

    public static TEnum ReadEnum<TEnum>(object? value, TEnum defaultValue)
        where TEnum : struct, Enum
    {
        if (value is string text && Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsedFromText))
        {
            return parsedFromText;
        }

        var numericValue = ReadInt32(value);
        if (numericValue.HasValue)
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), numericValue.Value);
        }

        return defaultValue;
    }

    public static Dictionary<string, string> ReadStringMap(object? value)
    {
        if (value is not Dictionary<string, object?> dictionary)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        return dictionary.ToDictionary(
            kvp => kvp.Key,
            kvp => ReadString(kvp.Value) ?? string.Empty,
            StringComparer.Ordinal);
    }

    public static List<string> ReadStringList(object? value)
    {
        if (value is not List<object?> list)
        {
            return new List<string>();
        }

        return list
            .Select(ReadString)
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .Select(text => text!)
            .ToList();
    }

    private static void WriteValue(StringBuilder builder, object? value, bool writeIndented, int depth)
    {
        switch (value)
        {
            case null:
                builder.Append("null");
                break;
            case string text:
                WriteString(builder, text);
                break;
            case bool boolean:
                builder.Append(boolean ? "true" : "false");
                break;
            case int number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case double number:
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case float number:
                builder.Append(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal number:
                builder.Append(number.ToString(CultureInfo.InvariantCulture));
                break;
            case Dictionary<string, object?> dictionary:
                WriteObject(builder, dictionary, writeIndented, depth);
                break;
            case IReadOnlyDictionary<string, object?> dictionary:
                WriteObject(builder, dictionary.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal), writeIndented, depth);
                break;
            case List<object?> list:
                WriteArray(builder, list, writeIndented, depth);
                break;
            case IReadOnlyList<object?> list:
                WriteArray(builder, list.ToList(), writeIndented, depth);
                break;
            default:
                throw new InvalidOperationException($"Unsupported JSON value type '{value.GetType().FullName}'.");
        }
    }

    private static void WriteObject(StringBuilder builder, Dictionary<string, object?> dictionary, bool writeIndented, int depth)
    {
        builder.Append('{');
        if (dictionary.Count == 0)
        {
            builder.Append('}');
            return;
        }

        var first = true;
        foreach (var (key, value) in dictionary)
        {
            if (first)
            {
                first = false;
            }
            else
            {
                builder.Append(',');
            }

            if (writeIndented)
            {
                builder.AppendLine();
                builder.Append(' ', (depth + 1) * 2);
            }

            WriteString(builder, key);
            builder.Append(writeIndented ? ": " : ":");
            WriteValue(builder, value, writeIndented, depth + 1);
        }

        if (writeIndented)
        {
            builder.AppendLine();
            builder.Append(' ', depth * 2);
        }

        builder.Append('}');
    }

    private static void WriteArray(StringBuilder builder, List<object?> list, bool writeIndented, int depth)
    {
        builder.Append('[');
        if (list.Count == 0)
        {
            builder.Append(']');
            return;
        }

        for (var index = 0; index < list.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }

            if (writeIndented)
            {
                builder.AppendLine();
                builder.Append(' ', (depth + 1) * 2);
            }

            WriteValue(builder, list[index], writeIndented, depth + 1);
        }

        if (writeIndented)
        {
            builder.AppendLine();
            builder.Append(' ', depth * 2);
        }

        builder.Append(']');
    }

    private static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                default:
                    if (character < ' ')
                    {
                        builder.Append("\\u");
                        builder.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    private sealed class Parser
    {
        private readonly string _json;
        private int _position;

        public Parser(string json)
        {
            _json = json ?? throw new ArgumentNullException(nameof(json));
        }

        public object? ParseValue()
        {
            SkipWhitespace();
            if (_position >= _json.Length)
            {
                throw new InvalidOperationException("Unexpected end of JSON input.");
            }

            return _json[_position] switch
            {
                '{' => ParseObjectInternal(),
                '[' => ParseArrayInternal(),
                '"' => ParseString(),
                't' => ParseTrue(),
                'f' => ParseFalse(),
                'n' => ParseNull(),
                '-' => ParseNumber(),
                _ when char.IsDigit(_json[_position]) => ParseNumber(),
                _ => throw new InvalidOperationException($"Unexpected character '{_json[_position]}' in JSON input."),
            };
        }

        public void EnsureComplete()
        {
            SkipWhitespace();
            if (_position != _json.Length)
            {
                throw new InvalidOperationException("Unexpected trailing content in JSON input.");
            }
        }

        private Dictionary<string, object?> ParseObjectInternal()
        {
            Expect('{');
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            SkipWhitespace();
            if (TryConsume('}'))
            {
                return result;
            }

            while (true)
            {
                SkipWhitespace();
                var key = ParseString();
                SkipWhitespace();
                Expect(':');
                var value = ParseValue();
                result[key] = value;
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return result;
                }

                Expect(',');
            }
        }

        private List<object?> ParseArrayInternal()
        {
            Expect('[');
            var result = new List<object?>();
            SkipWhitespace();
            if (TryConsume(']'))
            {
                return result;
            }

            while (true)
            {
                result.Add(ParseValue());
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return result;
                }

                Expect(',');
            }
        }

        private string ParseString()
        {
            Expect('"');
            var builder = new StringBuilder();
            while (_position < _json.Length)
            {
                var character = _json[_position++];
                if (character == '"')
                {
                    return builder.ToString();
                }

                if (character != '\\')
                {
                    builder.Append(character);
                    continue;
                }

                if (_position >= _json.Length)
                {
                    throw new InvalidOperationException("Unexpected end of JSON input in string escape sequence.");
                }

                var escape = _json[_position++];
                builder.Append(escape switch
                {
                    '"' => '"',
                    '\\' => '\\',
                    '/' => '/',
                    'b' => '\b',
                    'f' => '\f',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'u' => ParseUnicodeEscape(),
                    _ => throw new InvalidOperationException($"Unsupported JSON escape sequence '\\{escape}'."),
                });
            }

            throw new InvalidOperationException("Unterminated JSON string.");
        }

        private object ParseNumber()
        {
            var start = _position;
            if (_json[_position] == '-')
            {
                _position++;
            }

            ConsumeDigits();
            if (_position < _json.Length && _json[_position] == '.')
            {
                _position++;
                ConsumeDigits();
            }

            if (_position < _json.Length && (_json[_position] == 'e' || _json[_position] == 'E'))
            {
                _position++;
                if (_position < _json.Length && (_json[_position] == '+' || _json[_position] == '-'))
                {
                    _position++;
                }

                ConsumeDigits();
            }

            var token = _json.Substring(start, _position - start);
            return token.IndexOfAny(new[] { '.', 'e', 'E' }) >= 0
                ? double.Parse(token, CultureInfo.InvariantCulture)
                : long.Parse(token, CultureInfo.InvariantCulture);
        }

        private bool ParseTrue()
        {
            ExpectKeyword("true");
            return true;
        }

        private bool ParseFalse()
        {
            ExpectKeyword("false");
            return false;
        }

        private object? ParseNull()
        {
            ExpectKeyword("null");
            return null;
        }

        private char ParseUnicodeEscape()
        {
            if (_position + 4 > _json.Length)
            {
                throw new InvalidOperationException("Incomplete JSON unicode escape sequence.");
            }

            var text = _json.Substring(_position, 4);
            _position += 4;
            return (char)int.Parse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }

        private void ConsumeDigits()
        {
            var start = _position;
            while (_position < _json.Length && char.IsDigit(_json[_position]))
            {
                _position++;
            }

            if (start == _position)
            {
                throw new InvalidOperationException("Expected a digit in JSON number.");
            }
        }

        private void Expect(char expected)
        {
            SkipWhitespace();
            if (_position >= _json.Length || _json[_position] != expected)
            {
                throw new InvalidOperationException($"Expected '{expected}' in JSON input.");
            }

            _position++;
        }

        private void ExpectKeyword(string keyword)
        {
            if (_position + keyword.Length > _json.Length || !string.Equals(_json.Substring(_position, keyword.Length), keyword, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Expected '{keyword}' in JSON input.");
            }

            _position += keyword.Length;
        }

        private bool TryConsume(char value)
        {
            SkipWhitespace();
            if (_position < _json.Length && _json[_position] == value)
            {
                _position++;
                return true;
            }

            return false;
        }

        private void SkipWhitespace()
        {
            while (_position < _json.Length && char.IsWhiteSpace(_json[_position]))
            {
                _position++;
            }
        }
    }
}
}
