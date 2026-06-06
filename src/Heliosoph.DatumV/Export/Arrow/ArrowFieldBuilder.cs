using Apache.Arrow.Types;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;
using ArrowField = Apache.Arrow.Field;
using ArrowStructType = Apache.Arrow.Types.StructType;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Builds the Arrow <see cref="ArrowField"/> for one engine
/// <see cref="ColumnInfo"/>, including the <c>datumv.*</c> metadata block
/// for typed-media kinds + Json + the kinds whose round-trip needs help
/// (Uuid / Duration / Int128 / UInt128). The kind→Arrow-type mapping
/// mirrors what <c>OpenArrowFunction</c> reads on the way in, so a
/// supported column round-trips through Arrow's native type system
/// (Date32 ↔ Date, Timestamp ↔ Timestamp, Decimal128 ↔ Decimal) without
/// any metadata wiring.
/// </summary>
internal static class ArrowFieldBuilder
{
    public static ArrowField Build(ColumnInfo column)
    {
        // Struct columns route through a dedicated helper because their
        // Arrow type is composed from child fields (which themselves can
        // be struct / list / scalar). The scalar resolver doesn't need
        // to know about struct.
        if (column.Kind == DataKind.Struct)
        {
            return BuildStructField(column);
        }

        (IArrowType type, string? datumKind, string? datumFormat) = ResolveType(column);

        IEnumerable<KeyValuePair<string, string>>? metadata = null;
        if (datumKind is not null && datumFormat is not null)
        {
            metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [ParquetDatumvMetadata.KindKey] = datumKind,
                [ParquetDatumvMetadata.FormatKey] = datumFormat,
                [ParquetDatumvMetadata.VersionKey] = ParquetDatumvMetadata.CurrentVersion,
            };
        }

        return new ArrowField(column.Name, type, column.Nullable, metadata);
    }

    /// <summary>
    /// Builds the Arrow field for a Struct (or Array&lt;Struct&gt;)
    /// column. Recurses through each declared child via
    /// <see cref="Build"/> so nested struct shapes propagate cleanly.
    /// Requires <see cref="ColumnInfo.Fields"/> to be set; the format's
    /// plan-time rejector catches the null case upstream.
    /// </summary>
    private static ArrowField BuildStructField(ColumnInfo column)
    {
        IReadOnlyList<ColumnInfo> children = column.Fields!;
        ArrowField[] childFields = new ArrowField[children.Count];
        for (int i = 0; i < children.Count; i++)
        {
            childFields[i] = Build(children[i]);
        }
        IArrowType structType = new ArrowStructType(childFields);
        IArrowType fieldType = column.IsArray ? new ListType(structType) : structType;
        return new ArrowField(column.Name, fieldType, column.Nullable);
    }

    /// <summary>
    /// Picks the Arrow type for <paramref name="column"/> and returns
    /// the <c>datumv.kind</c> / <c>datumv.format</c> tags for kinds
    /// whose round-trip needs metadata help. <see langword="null"/>
    /// metadata means the kind round-trips natively through Arrow's
    /// type system.
    /// </summary>
    private static (IArrowType Type, string? DatumKind, string? DatumFormat) ResolveType(ColumnInfo column)
    {
        if (column.IsArray)
        {
            // Top-level array → Arrow LIST<element-type>. FixedSizeList
            // would be tighter for known-shape vectors, but ListType
            // round-trips through open_arrow either way and keeps the
            // writer uniform across variable- and fixed-length cases.
            (IArrowType elemType, string? elemKind, string? elemFormat) = ResolveScalarType(column.Kind);
            return (new ListType(elemType), elemKind, elemFormat);
        }
        return ResolveScalarType(column.Kind);
    }

    private static (IArrowType Type, string? DatumKind, string? DatumFormat) ResolveScalarType(DataKind kind)
    {
        switch (kind)
        {
            case DataKind.Boolean: return (BooleanType.Default, null, null);
            case DataKind.Int8: return (Int8Type.Default, null, null);
            case DataKind.UInt8: return (UInt8Type.Default, null, null);
            case DataKind.Int16: return (Int16Type.Default, null, null);
            case DataKind.UInt16: return (UInt16Type.Default, null, null);
            case DataKind.Int32: return (Int32Type.Default, null, null);
            case DataKind.UInt32: return (UInt32Type.Default, null, null);
            case DataKind.Int64: return (Int64Type.Default, null, null);
            case DataKind.UInt64: return (UInt64Type.Default, null, null);
            case DataKind.Float32: return (FloatType.Default, null, null);
            case DataKind.Float64: return (DoubleType.Default, null, null);
            case DataKind.Float16:
                // No HalfFloat writer surface; promote to Float32 (matching
                // the way the reader demotes the other direction).
                return (FloatType.Default, null, null);

            case DataKind.String: return (StringType.Default, null, null);

            case DataKind.Date: return (Date32Type.Default, null, null);
            case DataKind.Time:
                return (new Time64Type(TimeUnit.Microsecond), null, null);
            case DataKind.Timestamp:
                // Cast null to string to bind the (TimeUnit, string)
                // overload — bare null matches both string and
                // TimeZoneInfo overloads and the compiler rejects it.
                return (new TimestampType(TimeUnit.Microsecond, (string?)null), null, null);
            case DataKind.TimestampTz:
                // The (TimeUnit, string) and (TimeUnit, TimeZoneInfo) ctors
                // share the same parameter name across overloads; pass
                // TimeZoneInfo.Utc to bind unambiguously. Same on-disk
                // representation as the string-named "UTC" form.
                return (new TimestampType(TimeUnit.Microsecond, TimeZoneInfo.Utc), null, null);

            case DataKind.Decimal:
                // Decimal128(38, 18) covers .NET decimal's full 28-29-digit
                // range with enough fractional precision for every dataset
                // we've shipped against. A future per-column precision/scale
                // hint would let us emit tighter types.
                return (new Decimal128Type(precision: 38, scale: 18), null, null);

            case DataKind.Uuid:
                // No Uuid arm in the reader; emit as String D-form so the
                // column re-imports as String, with a datumv tag so a
                // future read-side enhancement can route back to Uuid.
                return (StringType.Default, "Uuid", "string");

            case DataKind.Duration:
                // No DurationType in the .NET writer surface (v23). Emit
                // as ISO 8601 duration text with a datumv tag.
                return (StringType.Default, "Duration", "iso8601");

            case DataKind.Int128:
                return (StringType.Default, "Int128", "string");
            case DataKind.UInt128:
                return (StringType.Default, "UInt128", "string");

            case DataKind.Json:
                // CBOR on the wire → JSON text on disk. Same approach
                // the Parquet sink uses, same datumv tag so the read-side
                // route is uniform.
                return (StringType.Default, "Json", "text");

            case DataKind.Image:
                return (BinaryType.Default, "Image", "passthrough");
            case DataKind.Audio:
                return (BinaryType.Default, "Audio", "passthrough");
            case DataKind.Video:
                return (BinaryType.Default, "Video", "passthrough");

            case DataKind.Mesh:
                // Convert the engine's blob format to standard glTF so
                // external Arrow consumers see something they can decode.
                // Round-trip through the engine works via the datumv tag.
                return (BinaryType.Default, "Mesh", "gltf");
            case DataKind.PointCloud:
                return (BinaryType.Default, "PointCloud", "ply");

            default:
                throw new ExportPlanException(
                    $"COPY TO arrow: kind {kind} has no Arrow representation in v1. " +
                    "Surface this case in the format's plan-time rejector before it " +
                    "reaches the sink.");
        }
    }
}
