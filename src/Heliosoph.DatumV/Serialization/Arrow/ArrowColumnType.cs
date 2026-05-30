using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Arrow;

/// <summary>
/// Mapped Arrow column type: carries the <see cref="Model.DataKind"/>
/// downstream SQL columns should use, plus the per-column shape flags
/// (<see cref="IsArray"/>, <see cref="IsNullable"/>) and the underlying
/// Arrow <see cref="ArrowTypeId"/> needed by the row decoder.
/// </summary>
/// <remarks>
/// <para>
/// Arrow's type system is more direct than Parquet's two-layer
/// (physical + logical) scheme: each Arrow type is a distinct
/// <see cref="IArrowType"/> with its own metadata (timestamp unit /
/// timezone, decimal precision / scale, list element type, struct
/// fields). v1 maps the dominant set used by HuggingFace and the
/// Arrow-as-interchange ecosystem; rarer / nested types surface with
/// <see cref="IsSupported"/> = <c>false</c> so <c>open_arrow_meta</c>
/// can flag them and <c>open_arrow</c> refuses cleanly.
/// </para>
/// <para>
/// Dictionary-encoded columns are unwrapped at this layer — the
/// dictionary's value type drives the surfaced <see cref="DataKind"/>
/// and the row decoder reads decoded values transparently.
/// </para>
/// </remarks>
internal readonly record struct ArrowColumnType(
    DataKind ElementKind,
    bool IsArray,
    bool IsNullable,
    bool IsSupported,
    ArrowTypeId UnderlyingTypeId,
    string LogicalTypeName)
{
    /// <summary>
    /// Maps an Arrow <see cref="Field"/> to the typed shape the row
    /// pipeline will use. Unsupported types land with
    /// <see cref="ElementKind"/> = <see cref="DataKind.Unknown"/> and
    /// <see cref="IsSupported"/> = <c>false</c>.
    /// </summary>
    public static ArrowColumnType From(Field field)
    {
        IArrowType effective = UnwrapDictionary(field.DataType);
        bool isArray = effective is ListType or LargeListType or FixedSizeListType;
        IArrowType valueType = isArray ? GetListValueType(effective) : effective;

        (DataKind kind, bool supported) = MapArrowType(valueType);
        return new ArrowColumnType(
            ElementKind: kind,
            IsArray: isArray,
            IsNullable: field.IsNullable,
            IsSupported: supported,
            UnderlyingTypeId: effective.TypeId,
            LogicalTypeName: DescribeLogicalType(effective));
    }

    /// <summary>
    /// Unwraps a <see cref="DictionaryType"/> to its value type. Arrow
    /// uses dictionary encoding for low-cardinality categorical columns
    /// (HF label columns, language codes, enum-like strings); the row
    /// decoder reads decoded values, so callers see the value type at
    /// the schema level.
    /// </summary>
    private static IArrowType UnwrapDictionary(IArrowType type) =>
        type is DictionaryType dict ? dict.ValueType : type;

    private static IArrowType GetListValueType(IArrowType listType) =>
        listType switch
        {
            ListType lt => lt.ValueDataType,
            LargeListType llt => llt.ValueDataType,
            FixedSizeListType flt => flt.ValueDataType,
            _ => throw new InvalidOperationException(
                $"GetListValueType called on non-list type {listType.TypeId}"),
        };

    private static (DataKind Kind, bool Supported) MapArrowType(IArrowType type)
    {
        switch (type.TypeId)
        {
            case ArrowTypeId.Boolean: return (DataKind.Boolean, true);

            case ArrowTypeId.Int8: return (DataKind.Int8, true);
            case ArrowTypeId.UInt8: return (DataKind.UInt8, true);
            case ArrowTypeId.Int16: return (DataKind.Int16, true);
            case ArrowTypeId.UInt16: return (DataKind.UInt16, true);
            case ArrowTypeId.Int32: return (DataKind.Int32, true);
            case ArrowTypeId.UInt32: return (DataKind.UInt32, true);
            case ArrowTypeId.Int64: return (DataKind.Int64, true);
            case ArrowTypeId.UInt64: return (DataKind.UInt64, true);

            // HalfFloat (Float16) is rare and we have no Float16 kind — promote.
            case ArrowTypeId.HalfFloat: return (DataKind.Float32, true);
            case ArrowTypeId.Float: return (DataKind.Float32, true);
            case ArrowTypeId.Double: return (DataKind.Float64, true);

            case ArrowTypeId.String: return (DataKind.String, true);

            case ArrowTypeId.Binary: return (DataKind.UInt8, true);
            case ArrowTypeId.FixedSizedBinary: return (DataKind.UInt8, true);

            case ArrowTypeId.Date32: return (DataKind.Date, true);
            case ArrowTypeId.Date64: return (DataKind.Date, true);
            case ArrowTypeId.Time32: return (DataKind.Time, true);
            case ArrowTypeId.Time64: return (DataKind.Time, true);

            case ArrowTypeId.Timestamp:
                // Arrow timestamps with timezone metadata map to TimestampTz;
                // without timezone → naive Timestamp.
                return type is TimestampType ts && !string.IsNullOrEmpty(ts.Timezone)
                    ? (DataKind.TimestampTz, true)
                    : (DataKind.Timestamp, true);

            case ArrowTypeId.Decimal128: return (DataKind.Decimal, true);

            // Nested / less common types deliberately deferred in v1.
            // Struct / List nesting deeper than one level falls through here
            // even though their representations exist — Phase D's follow-up
            // would wire them with the typed Struct / multi-dim cell paths.
            case ArrowTypeId.Struct:
            case ArrowTypeId.Map:
            case ArrowTypeId.Union:
            case ArrowTypeId.Decimal256:
            default:
                return (DataKind.Unknown, false);
        }
    }

    /// <summary>
    /// Returns a short description of the column's logical type for
    /// surfacing through <c>open_arrow_meta</c>'s introspection column.
    /// Combines the <see cref="ArrowTypeId"/> name with any
    /// type-specific metadata (timestamp unit, decimal precision/scale,
    /// fixed-size-list length) so users can see the exact shape.
    /// </summary>
    private static string DescribeLogicalType(IArrowType type) =>
        type switch
        {
            TimestampType ts => string.IsNullOrEmpty(ts.Timezone)
                ? $"Timestamp[{ts.Unit}]"
                : $"Timestamp[{ts.Unit}, {ts.Timezone}]",
            Decimal128Type d => $"Decimal128({d.Precision}, {d.Scale})",
            FixedSizeListType fsl => $"FixedSizeList<{fsl.ValueDataType.TypeId}>[{fsl.ListSize}]",
            ListType lt => $"List<{lt.ValueDataType.TypeId}>",
            LargeListType llt => $"LargeList<{llt.ValueDataType.TypeId}>",
            _ => type.TypeId.ToString(),
        };
}
