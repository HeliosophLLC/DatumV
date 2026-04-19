using System.Numerics;
using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Model;

public readonly partial struct DataValue
{
    // ─────────────────────── Date/time widening ───────────────────────────────

    /// <summary>
    /// Converts a <see cref="DataKind.Date"/>, <see cref="DataKind.Timestamp"/>,
    /// or <see cref="DataKind.TimestampTz"/> value to <see cref="DateTimeOffset"/>.
    /// Date values become midnight UTC; Timestamp (naive) is reinterpreted as
    /// UTC ticks (PG-equivalent of casting timestamp → timestamptz with the
    /// session TZ pinned to UTC).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The value is null or its kind is none of Date, Timestamp, TimestampTz.
    /// </exception>
    public DateTimeOffset ToDateTimeOffset()
    {
        if (IsNull) throw new InvalidOperationException("Cannot convert a null DataValue to DateTimeOffset.");
        return _kind switch
        {
            DataKind.Date => new DateTimeOffset(AsDate().ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
            DataKind.TimestampTz => AsTimestampTz(),
            // PG: timestamp (without tz) → assume UTC when forced into a DateTimeOffset.
            // The naive ticks are reinterpreted as UTC; callers that need a different
            // session-tz interpretation must cast explicitly.
            DataKind.Timestamp => new DateTimeOffset(AsTimestamp().Ticks, TimeSpan.Zero),
            _ => throw new InvalidOperationException(
                $"Cannot convert DataKind.{_kind} to DateTimeOffset. Expected Date, Timestamp, or TimestampTz."),
        };
    }

    // ─────────────────────── Object boxing ─────────────────────────────────────

    /// <summary>
    /// Returns the value as its natural boxed CLR type. Useful for JSON serialization
    /// and other contexts where the typed object is needed rather than a string.
    /// </summary>
    /// <param name="store">
    /// Optional value store used to resolve reference kinds. When supplied,
    /// <see cref="DataKind.String"/> returns the real <see cref="string"/> payload.
    /// When <see langword="null"/>, reference kinds fall back to
    /// <see cref="ToString"/>'s summary form. Inline kinds (integers, floats,
    /// booleans, dates, etc.) never need a store.
    /// </param>
    /// <param name="registry">
    /// Optional sidecar registry for resolving sidecar-backed reference values
    /// (long strings spilled to a <c>.datum-blob</c> sidecar). Required whenever
    /// the value's <see cref="IsInSidecar"/> flag is set; callers that work
    /// against arena-only batches may pass <see langword="null"/>.
    /// </param>
    /// <returns>
    /// The boxed primitive (<see cref="float"/>, <see cref="int"/>, <see cref="bool"/>, etc.),
    /// the reference-type payload (<see cref="string"/>) when <paramref name="store"/> is
    /// supplied, or <see langword="null"/> when <see cref="IsNull"/> is true.
    /// Composite types (<see cref="DataKind.Struct"/>, typed arrays) and other reference
    /// kinds (Image / Audio / Video / Json) return the <see cref="ToString"/> summary;
    /// callers that need recursive conversion should handle those kinds explicitly.
    /// </returns>
    public object? ToObject(IValueStore? store = null, SidecarRegistry? registry = null)
    {
        if (IsNull) return null;

        return _kind switch
        {
            DataKind.Float32   => AsFloat32(),
            DataKind.Float64   => AsFloat64(),
            DataKind.UInt8     => AsUInt8(),
            DataKind.Int8      => AsInt8(),
            DataKind.Int16     => AsInt16(),
            DataKind.UInt16    => AsUInt16(),
            DataKind.Int32     => AsInt32(),
            DataKind.UInt32    => AsUInt32(),
            DataKind.Int64     => AsInt64(),
            DataKind.UInt64    => AsUInt64(),
            DataKind.Int128    => AsInt128(),
            DataKind.UInt128   => AsUInt128(),
            DataKind.Float16   => AsFloat16(),
            DataKind.Decimal   => AsDecimal(),
            DataKind.Boolean   => AsBoolean(),
            DataKind.Date      => AsDate(),
            DataKind.Timestamp   => AsTimestamp(),
            DataKind.TimestampTz => AsTimestampTz(),
            DataKind.Time      => AsTime(),
            DataKind.Duration  => AsDuration(),
            DataKind.Uuid      => AsUuid(),
            DataKind.Point2D   => AsPoint2D(),
            DataKind.Point3D   => AsPoint3D(),
            // String resolves uniformly across inline / arena / sidecar tiers
            // via the (store, registry) overload — the store-only AsString
            // throws on sidecar-backed values.
            DataKind.String when store is not null => AsString(store, registry),
            // Other reference kinds (Image / Audio / Video / Json, byte[],
            // typed arrays, structs) — return the ToString() summary without
            // content. Callers that need the payload should branch on
            // _kind / IsArray and read via the kind-specific accessor.
            _ => ToString(),
        };
    }

    // ─────────────────────── Display formatting ───────────────────────────────

    /// <summary>
    /// Returns a human-readable string representation of this value suitable for
    /// display in tables, logs, and diagnostics.
    /// </summary>
    /// <param name="converter">
    /// Optional per-kind override. When supplied, the delegate is called first with the
    /// value's <see cref="DataKind"/>. If it returns <c>(true, result)</c> the result is
    /// used directly; if it returns <c>(false, _)</c> the canonical formatting applies.
    /// This lets callers customise specific kinds (e.g. numeric precision, date format)
    /// while inheriting default formatting for everything else.
    /// </param>
    /// <returns>
    /// A formatted string, or <c>"NULL"</c> when <see cref="IsNull"/> is true.
    /// </returns>
    public string ToDisplayString(Func<DataValue, (bool Handled, string? Result)>? converter = null)
    {
        if (IsNull) return "NULL";

        if (converter is not null)
        {
            (bool handled, string? result) = converter(this);
            if (handled) return result ?? "NULL";
        }

        return _kind switch
        {
            DataKind.Float32  => AsFloat32().ToString("G"),
            DataKind.Float64  => AsFloat64().ToString("G"),
            DataKind.UInt8    => AsUInt8().ToString(),
            DataKind.Int8     => AsInt8().ToString(),
            DataKind.Int16    => AsInt16().ToString(),
            DataKind.UInt16   => AsUInt16().ToString(),
            DataKind.Int32    => AsInt32().ToString(),
            DataKind.UInt32   => AsUInt32().ToString(),
            DataKind.Int64    => AsInt64().ToString(),
            DataKind.UInt64   => AsUInt64().ToString(),
            DataKind.Int128   => AsInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.UInt128  => AsUInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Decimal  => AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Float16  => AsFloat16().ToString("G"),
            DataKind.Boolean  => AsBoolean() ? "true" : "false",
            DataKind.Date     => AsDate().ToString("yyyy-MM-dd"),
            DataKind.Timestamp   => AsTimestamp().ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
            DataKind.TimestampTz => AsTimestampTz().ToString("O"),
            DataKind.Time     => AsTime().ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => AsDuration().ToString("c"),
            DataKind.Uuid     => AsUuid().ToString("D"),
            DataKind.Point2D  => FormatPoint2D(),
            DataKind.Point3D  => FormatPoint3D(),
            DataKind.Type     => FormatType(),
            // Reference types require a store — return ToString() summary without content.
            _ => ToString() ?? _kind.ToString(),
        };
    }

    /// <inheritdoc/>
    public override string ToString()
    {
        if (IsNull) return $"NULL({_kind})";

        if (IsByteArrayKind)
        {
            // Arena and sidecar layouts both pack length across _p2 + low byte of _p3
            // (40-bit field, ~1 TiB cap). BackedLength decodes both uniformly.
            long byteLen = BackedLength;
            return $"UInt8[{byteLen} bytes]";
        }

        return _kind switch
        {
            DataKind.Float32 => BitConverter.Int32BitsToSingle(_p0).ToString("G"),
            DataKind.UInt8 => ((byte)_p0).ToString(),
            DataKind.Int8 => ((sbyte)_p0).ToString(),
            DataKind.Int16 => ((short)_p0).ToString(),
            DataKind.UInt16 => ((ushort)_p0).ToString(),
            DataKind.Int32 => _p0.ToString(),
            DataKind.UInt32 => unchecked((uint)_p0).ToString(),
            DataKind.Int64 => ReadLong().ToString(),
            DataKind.UInt64 => unchecked((ulong)ReadLong()).ToString(),
            DataKind.Float64 => BitConverter.Int64BitsToDouble(ReadLong()).ToString("G"),
            DataKind.Float16 => BitConverter.UInt16BitsToHalf((ushort)_p0).ToString("G"),
            DataKind.Decimal => AsDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.UInt128 => AsUInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.Int128 => AsInt128().ToString(System.Globalization.CultureInfo.InvariantCulture),
            DataKind.String => IsInline
                ? System.Text.Encoding.UTF8.GetString(InlineUtf8Span)
                : $"String[arena@{BackedOffset}+{BackedLength}]",
            DataKind.Date => DateOnly.FromDayNumber(_p0).ToString("yyyy-MM-dd"),
            DataKind.Timestamp => AsTimestamp().ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
            DataKind.TimestampTz => AsTimestampTz().ToString("O"),
            DataKind.Uuid => AsUuid().ToString("D"),
            DataKind.Boolean => _p0 != 0 ? "true" : "false",
            DataKind.Time => new TimeOnly(ReadLong()).ToString("HH:mm:ss.FFFFFFF"),
            DataKind.Duration => new TimeSpan(ReadLong()).ToString("c"),
            // Sidecar-backed values pack the 64-bit offset across _p0/_p1
            // and the 40-bit length across _p2/_p3, so reading _p0/_p1 as
            // offset/length only works for arena-backed values. Branching
            // on IsInSidecar restores the real length when the bytes live
            // in a .datum-blob sidecar.
            DataKind.Image => IsInSidecar
                ? $"Image[offset={SidecarOffset}, len={SidecarLength}]"
                : $"Image[offset={_p0}, len={_p1}]",
            DataKind.Audio => IsInSidecar
                ? $"Audio[offset={SidecarOffset}, len={SidecarLength}]"
                : $"Audio[offset={_p0}, len={_p1}]",
            DataKind.Video => IsInSidecar
                ? $"Video[offset={SidecarOffset}, len={SidecarLength}]"
                : $"Video[offset={_p0}, len={_p1}]",
            DataKind.Json => IsInSidecar
                ? $"Json[offset={SidecarOffset}, len={SidecarLength}]"
                : $"Json[offset={_p0}, len={_p1}]",
            DataKind.Struct => $"Struct({_meta} fields)",
            DataKind.Point2D => FormatPoint2D(),
            DataKind.Point3D => FormatPoint3D(),
            _ => _kind.ToString(),
        };
    }

    private string FormatPoint2D()
    {
        Vector2 v = AsPoint2D();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"({v.X:G}, {v.Y:G})");
    }

    private string FormatPoint3D()
    {
        Vector3 v = AsPoint3D();
        return string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"({v.X:G}, {v.Y:G}, {v.Z:G})");
    }

    /// <summary>
    /// Renders the type this value describes as a human-readable string. When
    /// <paramref name="registry"/> is provided and this value carries a non-zero
    /// <see cref="TypeId"/>, the descriptor is looked up and field names / element
    /// kinds are included recursively (e.g. <c>"Struct{label: String, score: Float32}"</c>,
    /// <c>"Array&lt;Struct{kx: Float32, ky: Float32}&gt;"</c>). Falls back to
    /// <c>AsType().ToString()</c> when the registry is null or the TypeId is 0.
    /// </summary>
    public string FormatType(TypeRegistry? registry = null)
    {
        if (IsNull) return "NULL";
        DataKind kind = AsType();
        ushort typeId = TypeId;
        if (typeId != 0 && registry is not null)
        {
            TypeDescriptor? desc = registry.GetDescriptor(typeId);
            if (desc is not null)
                return FormatTypeDescriptor(desc, registry);
        }
        // Primitive arrays carry no descriptor — the array-ness rides on the
        // Type-tag-private annotation in _p1 (stamped by typeof() at the
        // source). Render Array<...> from the annotation so
        // typeof([1::float32]) surfaces as "Array<Float32>" rather than just
        // "Float32".
        if (TypeDescribesArray)
        {
            return $"Array<{kind}>";
        }
        return kind.ToString();
    }

    private static string FormatTypeDescriptor(TypeDescriptor desc, TypeRegistry registry)
    {
        if (desc.IsArray)
        {
            string elementName = desc.ElementTypeId is { } eid && registry.GetDescriptor(eid) is { } ed
                ? FormatTypeDescriptor(ed, registry)
                : desc.Kind.ToString();
            return $"Array<{elementName}>";
        }
        if (desc.Kind == DataKind.Struct && desc.Fields is { Count: > 0 } fields)
        {
            System.Text.StringBuilder sb = new();
            sb.Append("Struct{");
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                StructFieldDescriptor f = fields[i];
                string fieldTypeName = f.TypeId != TypeRegistry.NoType && registry.GetDescriptor(f.TypeId) is { } fd
                    ? FormatTypeDescriptor(fd, registry)
                    : "?";
                sb.Append(f.Name).Append(": ").Append(fieldTypeName);
            }
            sb.Append('}');
            return sb.ToString();
        }
        return desc.Kind.ToString();
    }
}
