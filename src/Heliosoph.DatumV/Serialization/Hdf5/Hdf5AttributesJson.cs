using System.Text.Json;
using Heliosoph.DatumV.Functions.Json;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Canonical-CBOR serializer for an HDF5 object's attribute list,
/// shared by <c>open_h5_meta</c> and the ingest <c>Hdf5Deserializer</c>.
/// Output shape is a JSON array of <c>{name, kind, value}</c> objects;
/// the bytes returned are CBOR (the storage contract for
/// <see cref="Model.DataKind.Json"/>), produced by
/// <see cref="CborJsonCodec.EncodeFromJsonText"/>.
/// </summary>
internal static class Hdf5AttributesJson
{
    /// <summary>
    /// Encodes <paramref name="attributes"/> as canonical-CBOR bytes
    /// suitable for a <see cref="Model.DataKind.Json"/> DataValue.
    /// </summary>
    internal static byte[] Build(IReadOnlyList<Hdf5AttributeRecord> attributes)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartArray();
            foreach (Hdf5AttributeRecord attr in attributes)
            {
                writer.WriteStartObject();
                writer.WriteString("name", attr.Name);
                writer.WriteString("kind", attr.Type.IsSupported ? attr.Type.ElementKind.ToString() : "Unknown");
                writer.WritePropertyName("value");
                WriteValue(writer, attr.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
        }

        return CborJsonCodec.EncodeFromJsonText(System.Text.Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static void WriteValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null: writer.WriteNullValue(); break;
            case string s: writer.WriteStringValue(s); break;
            case bool b: writer.WriteBooleanValue(b); break;
            case sbyte i: writer.WriteNumberValue(i); break;
            case byte i: writer.WriteNumberValue(i); break;
            case short i: writer.WriteNumberValue(i); break;
            case ushort i: writer.WriteNumberValue(i); break;
            case int i: writer.WriteNumberValue(i); break;
            case uint i: writer.WriteNumberValue(i); break;
            case long i: writer.WriteNumberValue(i); break;
            case ulong i: writer.WriteNumberValue(i); break;
            case float f: writer.WriteNumberValue(f); break;
            case double d: writer.WriteNumberValue(d); break;
            case System.Array array:
                writer.WriteStartArray();
                foreach (object? element in array)
                {
                    WriteValue(writer, element);
                }
                writer.WriteEndArray();
                break;
            default:
                writer.WriteStringValue(value.ToString() ?? "");
                break;
        }
    }
}
