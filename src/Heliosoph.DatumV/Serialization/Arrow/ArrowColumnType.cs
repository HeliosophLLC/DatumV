using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Parquet;

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
/// <para>
/// <strong><c>datumv.*</c> field metadata routing.</strong> Files
/// written by the engine's own Arrow sink stamp each typed-media column
/// (and Json, Uuid, Duration, Date, TimestampTz) with a
/// <c>datumv.kind</c> / <c>datumv.format</c> pair so the reader can
/// restore the original <see cref="DataKind"/> even when Arrow's native
/// type system can't carry it. <see cref="MediaRouteKind"/> is the
/// post-decode target kind; the row decoder reads the column in its
/// on-disk shape (<see cref="ElementKind"/> + <see cref="IsArray"/>),
/// then applies the route to lift each row into the typed value.
/// </para>
/// </remarks>
internal readonly record struct ArrowColumnType(
    DataKind ElementKind,
    bool IsArray,
    bool IsNullable,
    bool IsSupported,
    ArrowTypeId UnderlyingTypeId,
    string LogicalTypeName,
    DataKind? MediaRouteKind = null,
    string? MediaRouteFormat = null,
    // For struct columns (top-level or list-element), the parsed Arrow
    // child fields. Lets the schema builder produce real nested
    // `ColumnInfo.Fields` and the row decoder dispatch each child column
    // through its own ArrowColumnType recursively.
    IReadOnlyList<Field>? StructChildren = null)
{
    /// <summary>
    /// The <see cref="DataKind"/> that SQL downstream should see for this
    /// column — the route's target kind when metadata routing applies,
    /// otherwise the raw <see cref="ElementKind"/>.
    /// </summary>
    public DataKind SurfacedKind => MediaRouteKind ?? ElementKind;

    /// <summary>
    /// True when the column carries a <see cref="MediaRouteKind"/> that
    /// targets a scalar typed-media or scalar-retag kind (Image, Audio,
    /// Video, Mesh, PointCloud, Json, Date, TimestampTz, Uuid). For these
    /// the row pipeline lifts each row from the on-disk shape into a
    /// scalar typed value, dropping the <see cref="IsArray"/> flag the
    /// source column carried.
    /// </summary>
    public bool RouteCollapsesToScalar => MediaRouteKind switch
    {
        DataKind.Image or DataKind.Audio or DataKind.Video
            or DataKind.Mesh or DataKind.PointCloud
            or DataKind.Json or DataKind.Date or DataKind.TimestampTz
            or DataKind.Uuid or DataKind.Duration
            or DataKind.Int128 or DataKind.UInt128 => true,
        _ => false,
    };

    /// <summary>
    /// Maps an Arrow <see cref="Field"/> to the typed shape the row
    /// pipeline will use. Unsupported types land with
    /// <see cref="ElementKind"/> = <see cref="DataKind.Unknown"/> and
    /// <see cref="IsSupported"/> = <c>false</c>.
    /// </summary>
    public static ArrowColumnType From(Field field)
    {
        IArrowType effective = UnwrapDictionary(field.DataType);
        bool isList = effective is ListType or LargeListType or FixedSizeListType;
        IArrowType valueType = isList ? GetListValueType(effective) : effective;

        // Binary at the top level holds a *variable-length* byte buffer
        // per row. In the engine model that's a byte-array column
        // (DataKind.UInt8 + IsArray=true), not a scalar UInt8 column.
        // Pre-mark IsArray so the row decoder routes to the BinaryArray
        // branch instead of casting to UInt8Array (which is a primitive-
        // scalar carrier and crashes at runtime). FixedSizeBinary maps
        // here too at the schema layer; its v23 .NET reader-array type
        // isn't exposed, so we surface IsSupported=false for it via the
        // pre-existing UnsupportedTypeId path below if it ever shows up.
        bool isBinary = !isList && effective is BinaryType;

        (DataKind kind, bool supported) = MapArrowType(valueType);
        bool isArray = isList || isBinary;

        // Capture child fields for Struct columns (top-level or list-
        // element). The reader walks these recursively, and the schema
        // builder uses them to populate ColumnInfo.Fields so SQL sees
        // real per-field names instead of f0/f1/...
        IReadOnlyList<Field>? structChildren = null;
        if (valueType is StructType st)
        {
            structChildren = st.Fields;
        }

        // Read the datumv.* metadata if present and route the column
        // back to its original kind. Files written by the engine's own
        // sink carry these tags; third-party Arrow files don't, and fall
        // through to the raw Arrow type mapping.
        (DataKind? routeKind, string? routeFormat) = TryParseDatumvRoute(field.Metadata);

        return new ArrowColumnType(
            ElementKind: kind,
            IsArray: isArray,
            IsNullable: field.IsNullable,
            IsSupported: supported,
            UnderlyingTypeId: effective.TypeId,
            LogicalTypeName: DescribeLogicalType(effective),
            MediaRouteKind: routeKind,
            MediaRouteFormat: routeFormat,
            StructChildren: structChildren);
    }

    /// <summary>
    /// Parses the <c>datumv.kind</c> / <c>datumv.format</c> pair off the
    /// field metadata. Returns <c>(null, null)</c> when the metadata is
    /// absent or the kind name isn't recognised — a build reading a file
    /// produced by a later build with a new kind tag degrades to the raw
    /// Arrow shape rather than throwing.
    /// </summary>
    private static (DataKind? Kind, string? Format) TryParseDatumvRoute(IReadOnlyDictionary<string, string>? meta)
    {
        if (meta is null) return (null, null);
        if (!meta.TryGetValue(ParquetDatumvMetadata.KindKey, out string? kindName)) return (null, null);

        DataKind? parsed = kindName switch
        {
            "Image" => DataKind.Image,
            "Audio" => DataKind.Audio,
            "Video" => DataKind.Video,
            "Mesh" => DataKind.Mesh,
            "PointCloud" => DataKind.PointCloud,
            "Json" => DataKind.Json,
            "Date" => DataKind.Date,
            "TimestampTz" => DataKind.TimestampTz,
            "Uuid" => DataKind.Uuid,
            "Duration" => DataKind.Duration,
            "Int128" => DataKind.Int128,
            "UInt128" => DataKind.UInt128,
            _ => null,
        };
        if (parsed is null) return (null, null);

        meta.TryGetValue(ParquetDatumvMetadata.FormatKey, out string? format);
        return (parsed, format);
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

            // Binary at the top level holds per-row variable-length byte
            // buffers — the engine maps this to a byte-array column
            // (UInt8 + IsArray=true; the IsArray flag is set by the
            // caller, not here).
            case ArrowTypeId.Binary: return (DataKind.UInt8, true);

            // FixedSizeBinary's .NET v23 array surface isn't ergonomically
            // exposed (no public concrete array class), so the reader has
            // no decode path even though the schema layer could classify
            // it. Surface unsupported with a hint pointing at the regular
            // Binary variant — pyarrow's `cast(arr.type, pa.binary())`
            // converts in-place.
            case ArrowTypeId.FixedSizedBinary: return (DataKind.Unknown, false);

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

            case ArrowTypeId.Struct: return (DataKind.Struct, true);

            // Nested / less common types deliberately deferred in v1.
            // Map / Union / Decimal256 surface as unsupported so
            // open_arrow_meta can flag them.
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
    /// Unsupported variants of supported types (Large* / FixedSizeBinary
    /// / Map / Union / Decimal256) embed a one-line actionable hint so
    /// the error surfaced by <c>open_arrow</c> tells the user how to
    /// convert upstream rather than just naming the type.
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
            // Unsupported-but-named variants — embed the hint in the type
            // name so it propagates through to the open_arrow error
            // message verbatim. The .NET v23 library doesn't expose
            // concrete LargeBinary / LargeString array classes (the
            // ArrowTypeId values exist; the reader-array surfaces don't),
            // so the conversion has to happen on the producer side.
            _ when type.TypeId == ArrowTypeId.FixedSizedBinary =>
                "FixedSizeBinary — convert upstream to Binary (pyarrow: arr.cast(pa.binary()))",
            _ when type.TypeId == ArrowTypeId.LargeBinary =>
                "LargeBinary — convert upstream to Binary (pyarrow: arr.cast(pa.binary()))",
            _ when type.TypeId == ArrowTypeId.LargeString =>
                "LargeString — convert upstream to String (pyarrow: arr.cast(pa.string()))",
            _ when type.TypeId == ArrowTypeId.Map =>
                "Map — convert upstream to a list of {key, value} structs",
            _ when type.TypeId == ArrowTypeId.Union =>
                "Union — not supported; export individual variants as separate columns",
            _ when type.TypeId == ArrowTypeId.Decimal256 =>
                "Decimal256 — exceeds .NET decimal range; convert upstream to Decimal128",
            _ => type.TypeId.ToString(),
        };
}
