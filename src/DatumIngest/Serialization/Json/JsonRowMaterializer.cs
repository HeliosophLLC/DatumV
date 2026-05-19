using System.Globalization;
using System.Text.Json;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Json;

/// <summary>
/// Shared row-materialization logic for the JSON-family deserializers. Reads
/// fields from a <see cref="JsonElement"/> object and writes one
/// <see cref="DataValue"/> per scanned schema column. Missing keys and explicit
/// JSON nulls both produce typed nulls; non-null values are dispatched on the
/// scanner-chosen <see cref="DataKind"/>.
/// </summary>
internal static class JsonRowMaterializer
{
    /// <summary>
    /// Fills <paramref name="values"/> with one <see cref="DataValue"/> per column,
    /// reading fields from <paramref name="row"/> against the schema defined by
    /// <paramref name="columnNames"/> and <paramref name="kinds"/>.
    /// </summary>
    public static void FillRow(
        JsonElement row,
        string[] columnNames,
        DataKind[] kinds,
        DataValue[] values,
        Arena arena)
    {
        for (int i = 0; i < columnNames.Length; i++)
        {
            if (!row.TryGetProperty(columnNames[i], out JsonElement value)
                || value.ValueKind == JsonValueKind.Null)
            {
                values[i] = DataValue.Null(kinds[i]);
                continue;
            }

            values[i] = ConvertValue(value, kinds[i], arena);
        }
    }

    /// <summary>
    /// Materializes a single <see cref="JsonElement"/> as a <see cref="DataValue"/>
    /// of the requested <paramref name="kind"/>. The scanner picked
    /// <paramref name="kind"/> so the value is known to conform; mismatches fall
    /// through to the JSON encoder rather than throwing.
    /// </summary>
    public static DataValue ConvertValue(JsonElement value, DataKind kind, Arena arena)
    {
        switch (kind)
        {
            case DataKind.Boolean:
                return DataValue.FromBoolean(value.GetBoolean());

            case DataKind.UInt8: return DataValue.FromUInt8((byte)value.GetInt64());
            case DataKind.Int8: return DataValue.FromInt8((sbyte)value.GetInt64());
            case DataKind.UInt16: return DataValue.FromUInt16((ushort)value.GetInt64());
            case DataKind.Int16: return DataValue.FromInt16((short)value.GetInt64());
            case DataKind.UInt32: return DataValue.FromUInt32((uint)value.GetInt64());
            case DataKind.Int32: return DataValue.FromInt32((int)value.GetInt64());
            case DataKind.UInt64: return DataValue.FromUInt64(value.GetUInt64());
            case DataKind.Int64: return DataValue.FromInt64(value.GetInt64());

            case DataKind.Int128:
                return DataValue.FromInt128(Int128.Parse(
                    value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture));
            case DataKind.UInt128:
                return DataValue.FromUInt128(UInt128.Parse(
                    value.GetRawText(), NumberStyles.Integer, CultureInfo.InvariantCulture));

            case DataKind.Float32: return DataValue.FromFloat32((float)value.GetDouble());
            case DataKind.Float64: return DataValue.FromFloat64(value.GetDouble());

            case DataKind.String:
                return DataValue.FromString(value.GetString()!, arena);

            case DataKind.Json:
            default:
                byte[] cbor = CborJsonCodec.EncodeFromJsonText(value.GetRawText());
                return DataValue.FromJson(cbor, arena);
        }
    }
}
