using Heliosoph.DatumV.Model;
using Parquet.Schema;

namespace Heliosoph.DatumV.Serialization.Parquet;

/// <summary>
/// Mapped Parquet leaf-column type: carries the
/// <see cref="Model.DataKind"/> downstream SQL columns should use, plus
/// the per-column shape flags (<see cref="IsArray"/>,
/// <see cref="IsNullable"/>) and the underlying CLR / logical type
/// info needed by the row decoder.
/// </summary>
/// <remarks>
/// <para>
/// Parquet's type system has two layers: <em>physical</em> types
/// (INT32, INT64, FLOAT, DOUBLE, BOOLEAN, BYTE_ARRAY,
/// FIXED_LEN_BYTE_ARRAY, INT96) and <em>logical</em> annotations
/// (UTF8/String, Decimal, Timestamp, Date, Time, UUID, JSON, …). v1
/// maps the dominant set used by HuggingFace and Parquet-in-the-wild
/// datasets; rarer logical annotations surface with
/// <see cref="IsSupported"/> = <c>false</c> so <c>open_parquet_meta</c>
/// can flag them and <c>open_parquet</c> refuses cleanly.
/// </para>
/// <para>
/// Nested types (LIST&lt;T&gt;, STRUCT&lt;…&gt;) are not represented
/// here yet — Parquet.Net's <see cref="ParquetSchema.GetDataFields"/>
/// returns flattened leaf columns with synthetic dotted names for
/// struct children, so the per-leaf mapper this struct provides is
/// the right entry point for both the meta-introspection TVF and the
/// primitive-column read path. Phase D will extend the surface to
/// surface nested groupings without breaking the v1 leaf-column
/// reading model.
/// </para>
/// </remarks>
internal readonly record struct ParquetColumnType(
    DataKind ElementKind,
    bool IsArray,
    bool IsNullable,
    bool IsSupported,
    string? LogicalTypeName,
    Type ClrType)
{
    /// <summary>
    /// Maps a Parquet leaf <see cref="DataField"/> to the typed shape
    /// the row pipeline will use. Unsupported physical/logical
    /// combinations land with <see cref="ElementKind"/> =
    /// <see cref="DataKind.Unknown"/> and <see cref="IsSupported"/> =
    /// <c>false</c>.
    /// </summary>
    public static ParquetColumnType From(DataField field)
    {
        (DataKind kind, bool supported) = MapClrType(field.ClrType);
        return new ParquetColumnType(
            ElementKind: kind,
            IsArray: field.IsArray,
            IsNullable: field.IsNullable,
            IsSupported: supported,
            LogicalTypeName: DescribeLogicalType(field),
            ClrType: field.ClrType);
    }

    /// <summary>
    /// Maps Parquet.Net's surfaced .NET CLR type back to the
    /// <see cref="Model.DataKind"/> the engine uses. Parquet.Net does
    /// the physical-to-logical-to-CLR mapping itself, so the v1 mapper
    /// can lean on the CLR type as the dispatch key instead of
    /// re-deriving it from the physical + logical type pair.
    /// </summary>
    private static (DataKind Kind, bool Supported) MapClrType(Type clrType)
    {
        if (clrType == typeof(bool)) return (DataKind.Boolean, true);

        if (clrType == typeof(sbyte)) return (DataKind.Int8, true);
        if (clrType == typeof(byte)) return (DataKind.UInt8, true);
        if (clrType == typeof(short)) return (DataKind.Int16, true);
        if (clrType == typeof(ushort)) return (DataKind.UInt16, true);
        if (clrType == typeof(int)) return (DataKind.Int32, true);
        if (clrType == typeof(uint)) return (DataKind.UInt32, true);
        if (clrType == typeof(long)) return (DataKind.Int64, true);
        if (clrType == typeof(ulong)) return (DataKind.UInt64, true);

        if (clrType == typeof(float)) return (DataKind.Float32, true);
        if (clrType == typeof(double)) return (DataKind.Float64, true);

        if (clrType == typeof(string)) return (DataKind.String, true);
        if (clrType == typeof(byte[])) return (DataKind.UInt8, true);   // raw BYTE_ARRAY

        // Logical-type CLR mappings Parquet.Net surfaces.
        if (clrType == typeof(decimal)) return (DataKind.Decimal, true);
        if (clrType == typeof(DateTime)) return (DataKind.Timestamp, true);
        if (clrType == typeof(DateTimeOffset)) return (DataKind.TimestampTz, true);
        if (clrType == typeof(DateOnly)) return (DataKind.Date, true);
        if (clrType == typeof(TimeOnly)) return (DataKind.Time, true);
        if (clrType == typeof(TimeSpan)) return (DataKind.Time, true);
        if (clrType == typeof(Guid)) return (DataKind.Uuid, true);

        return (DataKind.Unknown, false);
    }

    /// <summary>
    /// Returns a short description of the field's logical type for
    /// surfacing through <c>open_parquet_meta</c>'s introspection column.
    /// Parquet.Net doesn't expose the on-disk logical-type token
    /// directly through the public surface, so v1 derives a stable
    /// name from the CLR type — enough for users to know what shape
    /// the column carries.
    /// </summary>
    private static string DescribeLogicalType(DataField field)
    {
        // Array-shaped CLR types like int? collapse to the non-nullable
        // form here — the IsNullable flag carries the absence info, so
        // the logical-type name is just the element shape.
        Type effective = field.IsNullable && field.ClrType.IsValueType
            ? Nullable.GetUnderlyingType(field.ClrType) ?? field.ClrType
            : field.ClrType;
        return effective.Name;
    }
}
