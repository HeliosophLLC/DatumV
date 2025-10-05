using System.Globalization;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Pure static CSV field parsing helpers. No state, no allocations in the fast path.
/// </summary>
internal static class CsvParser
{
    [ThreadStatic]
    private static List<string>? _fieldBuffer;

    /// <summary>Parses a CSV line into fields following RFC 4180 rules.</summary>
    internal static string[] ParseCsvLine(string line, char delimiter)
        => ParseCsvLineList(line, delimiter).ToArray();

    /// <summary>
    /// Parses a CSV line into the thread-local field list. The returned list is
    /// only valid until the next call on the same thread.
    /// </summary>
    internal static List<string> ParseCsvLineList(string line, char delimiter)
    {
        List<string> fields = (_fieldBuffer ??= new(16));
        fields.Clear();
        int position = 0;

        while (position <= line.Length)
        {
            if (position == line.Length)
            {
                fields.Add(string.Empty);
                break;
            }

            if (line[position] == '"')
            {
                position++;
                int start = position;
                System.Text.StringBuilder builder = new();

                while (position < line.Length)
                {
                    if (line[position] == '"')
                    {
                        if (position + 1 < line.Length && line[position + 1] == '"')
                        {
                            builder.Append(line.AsSpan(start, position - start));
                            builder.Append('"');
                            position += 2;
                            start = position;
                        }
                        else
                        {
                            builder.Append(line.AsSpan(start, position - start));
                            position++;
                            break;
                        }
                    }
                    else
                    {
                        position++;
                    }
                }

                fields.Add(builder.ToString());

                if (position < line.Length && line[position] == delimiter)
                    position++;
                else if (position >= line.Length)
                    break;
            }
            else
            {
                int nextDelimiter = line.IndexOf(delimiter, position);
                string fieldValue;
                if (nextDelimiter == -1)
                {
                    fieldValue = line[position..];
                    fields.Add(fieldValue.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? string.Empty : fieldValue);
                    break;
                }
                else
                {
                    fieldValue = line[position..nextDelimiter];
                    fields.Add(fieldValue.Equals("NULL", StringComparison.OrdinalIgnoreCase) ? string.Empty : fieldValue);
                    position = nextDelimiter + 1;
                }
            }
        }

        return fields;
    }

    /// <summary>Span-based field parser for numeric types. Falls back to string for non-numeric.</summary>
    internal static DataValue ParseFieldSpan(ReadOnlySpan<char> field, DataKind kind)
    {
        if (field.IsEmpty) return DataValue.Null(kind);

        switch (kind)
        {
            case DataKind.Float32:
                return float.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out float f32)
                    ? DataValue.FromFloat32(f32) : DataValue.Null(DataKind.Float32);
            case DataKind.Float64:
                return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double f64)
                    ? DataValue.FromFloat64(f64) : DataValue.Null(DataKind.Float64);
            case DataKind.Int8:
                return sbyte.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte i8)
                    ? DataValue.FromInt8(i8) : DataValue.Null(DataKind.Int8);
            case DataKind.Int16:
                return short.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16)
                    ? DataValue.FromInt16(i16) : DataValue.Null(DataKind.Int16);
            case DataKind.Int32:
                return int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32)
                    ? DataValue.FromInt32(i32) : DataValue.Null(DataKind.Int32);
            case DataKind.Int64:
                if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64))
                    return DataValue.FromInt64(i64);
                return double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double floatEncoded)
                    ? DataValue.FromInt64((long)floatEncoded) : DataValue.Null(DataKind.Int64);
            case DataKind.Boolean:
                if (field.Length == 1)
                {
                    if (field[0] == '1') return DataValue.FromBoolean(true);
                    if (field[0] == '0') return DataValue.FromBoolean(false);
                }
                else if (field.Equals("true", StringComparison.OrdinalIgnoreCase))
                    return DataValue.FromBoolean(true);
                else if (field.Equals("false", StringComparison.OrdinalIgnoreCase))
                    return DataValue.FromBoolean(false);
                return DataValue.Null(DataKind.Boolean);
            default:
                return ParseFieldString(field.ToString(), kind);
        }
    }

    /// <summary>String-based field parser for non-numeric types (dates, UUIDs, strings).</summary>
    internal static DataValue ParseFieldString(string field, DataKind kind, IValueStore? store = null)
    {
        if (field.Length == 0) return DataValue.Null(kind);

        return kind switch
        {
            DataKind.Date when DateOnly.TryParse(field, CultureInfo.InvariantCulture, out DateOnly date)
                => DataValue.FromDate(date),
            DataKind.Date when DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset dto)
                => DataValue.FromDate(DateOnly.FromDateTime(dto.DateTime)),
            DataKind.Date => DataValue.Null(DataKind.Date),
            DataKind.DateTime when DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out DateTimeOffset dt)
                => DataValue.FromDateTime(dt),
            DataKind.DateTime => DataValue.Null(DataKind.DateTime),
            DataKind.Uuid when Guid.TryParse(field, out Guid uuid)
                => DataValue.FromUuid(uuid),
            DataKind.Uuid => DataValue.Null(DataKind.Uuid),
            DataKind.Boolean when field is "1" || field.Equals("true", StringComparison.OrdinalIgnoreCase)
                => DataValue.FromBoolean(true),
            DataKind.Boolean when field is "0" || field.Equals("false", StringComparison.OrdinalIgnoreCase)
                => DataValue.FromBoolean(false),
            DataKind.Boolean => DataValue.Null(DataKind.Boolean),
            _ => store is not null ? DataValue.FromString(field, store) : DataValue.FromString(field),
        };
    }

    /// <summary>Returns true when the trimmed span is the unquoted literal NULL.</summary>
    internal static bool IsNullLiteral(ReadOnlySpan<char> field)
        => field.Length == 4 && field.Equals("NULL", StringComparison.OrdinalIgnoreCase);
}
