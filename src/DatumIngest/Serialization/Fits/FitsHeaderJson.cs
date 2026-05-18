using System.Text;
using DatumIngest.Functions.Json;

namespace DatumIngest.Serialization.Fits;

/// <summary>
/// Canonical-CBOR serializer for a FITS HDU's card list, shared by the
/// two TVFs (<c>open_fits_hdus</c>, <c>open_fits_images</c>) and the
/// <see cref="FitsDeserializer"/> ingest path. The logical shape is an
/// array of <c>{key, value, comment}</c> objects in file order;
/// <c>value</c> and <c>comment</c> are <c>null</c> for cards that don't
/// carry them (<c>END</c> is excluded by the reader so it never reaches
/// here). The bytes returned are CBOR, not raw JSON text — that's the
/// storage contract for <see cref="Model.DataKind.Json"/> DataValues:
/// the per-row arena slot holds canonical CBOR, and consumers
/// (renderers, <c>json_*</c> functions) read it back through that
/// codec.
/// </summary>
internal static class FitsHeaderJson
{
    /// <summary>
    /// Encodes <paramref name="cards"/> as canonical-CBOR bytes suitable
    /// for a <see cref="Model.DataKind.Json"/> DataValue.
    /// </summary>
    internal static byte[] Build(IReadOnlyList<FitsCard> cards)
    {
        StringBuilder sb = new();
        sb.Append('[');
        for (int i = 0; i < cards.Count; i++)
        {
            if (i > 0) sb.Append(',');
            FitsCard card = cards[i];
            sb.Append("{\"key\":");
            AppendString(sb, card.Keyword);
            sb.Append(",\"value\":");
            if (card.RawValue is null) sb.Append("null"); else AppendString(sb, card.RawValue);
            sb.Append(",\"comment\":");
            if (card.Comment is null) sb.Append("null"); else AppendString(sb, card.Comment);
            sb.Append('}');
        }
        sb.Append(']');
        return CborJsonCodec.EncodeFromJsonText(sb.ToString());
    }

    private static void AppendString(StringBuilder sb, string s)
    {
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("X4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
    }
}
