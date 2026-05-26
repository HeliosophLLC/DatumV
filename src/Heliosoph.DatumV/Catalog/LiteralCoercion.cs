using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Coerces a CLR literal value (as produced by
/// <c>SqlParser.ParseNumericLiteral</c>: <see cref="sbyte"/> / <see cref="short"/> /
/// <see cref="int"/> / <see cref="long"/> / <see cref="ulong"/> / <see cref="Int128"/> /
/// <see cref="UInt128"/> for integers, <see cref="float"/> / <see cref="double"/>
/// for fractionals) into a <see cref="DataValue"/> of a target <see cref="DataKind"/>.
/// Lossless coercions are accepted; lossy or cross-family coercions throw a
/// descriptive <see cref="InvalidOperationException"/>.
/// </summary>
internal static class LiteralCoercion
{
    public static DataValue Coerce(object? literal, ColumnInfo target, Arena arena, string columnName)
    {
        if (literal is null)
        {
            if (!target.Nullable)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}' is NOT NULL but the supplied value is NULL.");
            }
            return DataValue.Null(target.Kind);
        }

        // Typed-array columns aren't yet writable from a literal in
        // PR10c — there's no inline-array literal syntax wired to the
        // INSERT path. INSERT … SELECT (PR10c') is how array columns
        // get populated.
        if (target.IsArray)
        {
            throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': typed-array columns are not yet " +
                "writable from a literal. Use INSERT … SELECT (PR10c').");
        }

        DataValue coerced = target.Kind switch
        {
            DataKind.Boolean => CoerceBoolean(literal, columnName),
            DataKind.Int8 => DataValue.FromInt8(ToSignedInRange<sbyte>(literal, sbyte.MinValue, sbyte.MaxValue, columnName, "Int8")),
            DataKind.Int16 => DataValue.FromInt16(ToSignedInRange<short>(literal, short.MinValue, short.MaxValue, columnName, "Int16")),
            DataKind.Int32 => DataValue.FromInt32(ToSignedInRange<int>(literal, int.MinValue, int.MaxValue, columnName, "Int32")),
            DataKind.Int64 => DataValue.FromInt64(ToInt64(literal, columnName)),
            DataKind.Int128 => DataValue.FromInt128(ToInt128(literal, columnName)),
            DataKind.UInt8 => DataValue.FromUInt8(ToUnsignedInRange<byte>(literal, byte.MaxValue, columnName, "UInt8")),
            DataKind.UInt16 => DataValue.FromUInt16(ToUnsignedInRange<ushort>(literal, ushort.MaxValue, columnName, "UInt16")),
            DataKind.UInt32 => DataValue.FromUInt32(ToUnsignedInRange<uint>(literal, uint.MaxValue, columnName, "UInt32")),
            DataKind.UInt64 => DataValue.FromUInt64(ToUInt64(literal, columnName)),
            DataKind.UInt128 => DataValue.FromUInt128(ToUInt128(literal, columnName)),
            DataKind.Float32 => DataValue.FromFloat32(ToFloat32Lossless(literal, columnName)),
            DataKind.Float64 => DataValue.FromFloat64(ToFloat64(literal, columnName)),
            DataKind.String => CoerceString(literal, arena, columnName),
            DataKind.Uuid => CoerceUuid(literal, columnName),
            DataKind.Date => CoerceDate(literal, columnName),
            DataKind.Time => CoerceTime(literal, columnName),
            DataKind.Timestamp => CoerceTimestamp(literal, columnName),
            DataKind.TimestampTz => CoerceTimestampTz(literal, columnName),
            DataKind.Duration => CoerceDuration(literal, columnName),
            DataKind.Decimal => CoerceDecimal(literal, columnName),
            _ => throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': literal coercion to " +
                $"{target.Kind} is not yet supported."),
        };
        return EnforceStringWidth(coerced, target, arena, columnName);
    }

    /// <summary>
    /// Validates that a typed-array <paramref name="source"/> matches the
    /// target column's declared <see cref="ColumnInfo.FixedShape"/>. For
    /// shape <c>[D]</c> the source must have exactly <c>D</c> elements;
    /// for multi-dim shapes the check is against the product of dimensions.
    /// When the declared shape has <c>ndim ≥ 2</c> and the source value is not
    /// already multi-dim, the value is rebuilt as an arena-backed multi-dim
    /// <see cref="DataValue"/> with the declared shape attached (prefix in the
    /// payload bytes, <see cref="DataValue.IsMultiDim"/> flag set). No-op for
    /// nulls and for variable-length array columns (<c>FixedShape</c>
    /// is <see langword="null"/>). Internal so the array branches of both
    /// <c>InsertExecutor.ConvertSourceValue</c> (INSERT … SELECT) and
    /// <c>ComputedColumnEvaluator.ConvertValueRefToTarget</c> (INSERT VALUES)
    /// can share one enforcement point.
    /// </summary>
    internal static DataValue EnforceFixedShape(
        DataValue source, ColumnInfo target, string columnName, Arena? arena = null)
    {
        if (target.FixedShape is not int[] shape) return source;
        if (source.IsNull) return source;

        int expected = 1;
        foreach (int dim in shape) expected = checked(expected * dim);

        // Inline arrays cache their count in _charCount's low byte; arena-backed
        // arrays derive it from byte length / element size. Sidecar-backed arrays
        // don't arrive on the INSERT path (same-arena gates above reject them).
        int actual = source.IsInline ? source.InlineArrayElementCount : source.ElementCount;
        if (actual < 0)
        {
            throw new ColumnValueConstraintException(
                $"Column '{columnName}': could not determine the length of the supplied " +
                "array to validate against the declared fixed shape.");
        }

        if (actual != expected)
        {
            string shapeText = shape.Length == 1
                ? shape[0].ToString()
                : $"[{string.Join(",", shape)}] (= {expected} elements flat)";
            throw new ColumnValueConstraintException(
                $"Column '{columnName}': supplied array has {actual} element" +
                $"{(actual == 1 ? "" : "s")} but declared shape requires {shapeText}.");
        }

        // Attach the declared shape on multi-dim columns. 1-D fixed shapes leave
        // the value flat (existing element-count derivation suffices). Sources
        // already carrying IsMultiDim (e.g. re-INSERT from a multi-dim column)
        // are left alone — their shape is already in the bytes.
        if (shape.Length >= 2 && !source.IsMultiDim && arena is not null)
        {
            // Reference-element kinds have a slot-block layout, not a flat
            // element-byte run — they need a kind-specific multi-dim factory
            // rather than FromArenaMultiDimArrayBytes. Each supported kind
            // routes through its own per-kind factory; remaining reference
            // kinds reject at DDL via IsMultiDimIncompatibleElementKind and
            // never reach this branch.
            if (source.IsArray)
            {
                switch (source.Kind)
                {
                    case DataKind.String:
                        return DataValue.FromArenaMultiDimStringArray(source.AsStringArray(arena), shape, arena);
                    case DataKind.Image:
                        return DataValue.FromArenaMultiDimImageArray(source.AsImageArray(arena), shape, arena);
                    case DataKind.Audio:
                        return DataValue.FromArenaMultiDimAudioArray(source.AsAudioArray(arena), shape, arena);
                    case DataKind.Video:
                        return DataValue.FromArenaMultiDimVideoArray(source.AsVideoArray(arena), shape, arena);
                    case DataKind.Json:
                        return DataValue.FromArenaMultiDimJsonArray(source.AsJsonArray(arena), shape, arena);
                    case DataKind.PointCloud:
                        return DataValue.FromArenaMultiDimPointCloudArray(source.AsPointCloudArray(arena), shape, arena);
                }
            }

            // Inline arrays expose element bytes via InlineArrayBytes (count × elementSize,
            // prefix-skipping if any); arena/sidecar use AsArraySpan<byte>, which casts
            // through MemoryMarshal so element-count vs. byte-count semantics align.
            ReadOnlySpan<byte> elementBytes = source.IsInline
                ? source.InlineArrayBytes
                : source.AsArraySpan<byte>(arena);
            return DataValue.FromArenaMultiDimArrayBytes(elementBytes, shape, source.Kind, arena);
        }

        return source;
    }

    /// <summary>
    /// Applies declared <c>VARCHAR(N)</c> / <c>CHAR(N)</c> width semantics
    /// to a freshly-coerced String value: overlong rejected with a
    /// <see cref="ColumnValueConstraintException"/>; CHAR short values
    /// right-padded with spaces to the declared length. No-op for every
    /// other kind, for nulls, and for String columns without a declared
    /// <see cref="ColumnInfo.MaxLength"/>.
    /// </summary>
    private static DataValue EnforceStringWidth(DataValue value, ColumnInfo target, Arena arena, string columnName)
    {
        if (target.Kind != DataKind.String || target.MaxLength is not int maxLen)
        {
            return value;
        }
        if (value.IsNull)
        {
            return value;
        }

        int charCount = value.StringCharCount(arena);
        if (charCount > maxLen)
        {
            string declaredType = target.IsBlankPadded ? "CHAR" : "VARCHAR";
            throw new ColumnValueConstraintException(
                $"Column '{columnName}': value of length {charCount} exceeds declared " +
                $"{declaredType}({maxLen}).");
        }
        if (target.IsBlankPadded && charCount < maxLen)
        {
            string padded = value.AsString(arena).PadRight(maxLen);
            return DataValue.FromString(padded, arena);
        }
        return value;
    }

    private static DataValue CoerceDate(object literal, string columnName) =>
        literal switch
        {
            DateOnly d => DataValue.FromDate(d),
            DateTime dt => DataValue.FromDate(DateOnly.FromDateTime(dt)),
            DateTimeOffset dto => DataValue.FromDate(DateOnly.FromDateTime(dto.Date)),
            string s when DateOnly.TryParse(s, out DateOnly parsed) => DataValue.FromDate(parsed),
            _ => throw IncompatibleLiteral(literal, "Date", columnName),
        };

    private static DataValue CoerceTime(object literal, string columnName) =>
        literal switch
        {
            TimeOnly t => DataValue.FromTime(t),
            TimeSpan ts when ts >= TimeSpan.Zero && ts < TimeSpan.FromDays(1)
                => DataValue.FromTime(TimeOnly.FromTimeSpan(ts)),
            string s when TimeOnly.TryParse(s, out TimeOnly parsed) => DataValue.FromTime(parsed),
            _ => throw IncompatibleLiteral(literal, "Time", columnName),
        };

    private static DataValue CoerceTimestampTz(object literal, string columnName) =>
        literal switch
        {
            // PG: input offset normalised to UTC at construction, original
            // offset discarded. FromTimestampTz handles this internally.
            DateTimeOffset dto => DataValue.FromTimestampTz(dto),
            DateTime dt => DataValue.FromTimestampTz(new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)),
            DateOnly d => DataValue.FromTimestampTz(new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            string s when DateTimeOffset.TryParse(s, out DateTimeOffset parsed)
                => DataValue.FromTimestampTz(parsed),
            _ => throw IncompatibleLiteral(literal, "TimestampTz", columnName),
        };

    private static DataValue CoerceTimestamp(object literal, string columnName) =>
        literal switch
        {
            // PG timestamp (without tz): naive ticks. We accept any DateTimeKind
            // and store .Ticks unchanged; a DateTimeOffset is reduced to its
            // DateTime component (clock-face value, offset discarded).
            DateTime dt => DataValue.FromTimestamp(dt),
            DateTimeOffset dto => DataValue.FromTimestamp(dto.DateTime),
            DateOnly d => DataValue.FromTimestamp(d.ToDateTime(TimeOnly.MinValue)),
            string s when DateTime.TryParse(s, out DateTime parsed) => DataValue.FromTimestamp(parsed),
            _ => throw IncompatibleLiteral(literal, "Timestamp", columnName),
        };

    private static DataValue CoerceDuration(object literal, string columnName) =>
        literal switch
        {
            TimeSpan ts => DataValue.FromDuration(ts),
            string s when TimeSpan.TryParse(s, out TimeSpan parsed) => DataValue.FromDuration(parsed),
            _ => throw IncompatibleLiteral(literal, "Duration", columnName),
        };

    private static DataValue CoerceDecimal(object literal, string columnName)
    {
        try
        {
            return literal switch
            {
                decimal d => DataValue.FromDecimal(d),
                sbyte s => DataValue.FromDecimal(s),
                short s => DataValue.FromDecimal(s),
                int i => DataValue.FromDecimal(i),
                long l => DataValue.FromDecimal(l),
                byte b => DataValue.FromDecimal(b),
                ushort u => DataValue.FromDecimal(u),
                uint u => DataValue.FromDecimal(u),
                ulong u => DataValue.FromDecimal(u),
                Int128 i128 => DataValue.FromDecimal((decimal)i128),
                UInt128 u128 => DataValue.FromDecimal((decimal)u128),
                // Float→decimal could lose precision silently; require an explicit
                // decimal-typed literal or cast on the SQL side.
                string s when decimal.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out decimal parsed)
                    => DataValue.FromDecimal(parsed),
                _ => throw IncompatibleLiteral(literal, "Decimal", columnName),
            };
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': literal {literal} does not fit in Decimal " +
                $"(range [{decimal.MinValue}, {decimal.MaxValue}]).");
        }
    }

    private static DataValue CoerceBoolean(object literal, string columnName) =>
        literal switch
        {
            bool b => DataValue.FromBoolean(b),
            _ => throw IncompatibleLiteral(literal, "Boolean", columnName),
        };

    private static DataValue CoerceString(object literal, Arena arena, string columnName) =>
        literal switch
        {
            string s => DataValue.FromString(s, arena),
            _ => throw IncompatibleLiteral(literal, "String", columnName),
        };

    private static DataValue CoerceUuid(object literal, string columnName)
    {
        if (literal is Guid g) return DataValue.FromUuid(g);
        if (literal is string s && Guid.TryParse(s, out Guid parsed)) return DataValue.FromUuid(parsed);
        throw IncompatibleLiteral(literal, "Uuid", columnName);
    }

    private static long ToInt64(object literal, string columnName) =>
        literal switch
        {
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u when u <= long.MaxValue => (long)u,
            Int128 i128 when i128 >= long.MinValue && i128 <= long.MaxValue => (long)i128,
            UInt128 u128 when u128 <= (UInt128)long.MaxValue => (long)u128,
            Int128 or UInt128 => throw OutOfRange(literal, "Int64", columnName, long.MinValue, long.MaxValue),
            _ => throw IncompatibleLiteral(literal, "Int64", columnName),
        };

    private static Int128 ToInt128(object literal, string columnName) =>
        literal switch
        {
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            Int128 i128 => i128,
            UInt128 u128 when u128 <= (UInt128)Int128.MaxValue => (Int128)u128,
            UInt128 => throw OutOfRange(literal, "Int128", columnName, Int128.MinValue, Int128.MaxValue),
            _ => throw IncompatibleLiteral(literal, "Int128", columnName),
        };

    private static UInt128 ToUInt128(object literal, string columnName)
    {
        return literal switch
        {
            sbyte s when s >= 0 => (UInt128)(ulong)s,
            short s when s >= 0 => (UInt128)(ulong)s,
            int i when i >= 0 => (UInt128)(ulong)i,
            long l when l >= 0 => (UInt128)(ulong)l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            Int128 i128 when i128 >= 0 => (UInt128)i128,
            UInt128 u128 => u128,
            _ when literal is sbyte or short or int or long or Int128
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in UInt128."),
            _ => throw IncompatibleLiteral(literal, "UInt128", columnName),
        };
    }

    private static ulong ToUInt64(object literal, string columnName)
    {
        return literal switch
        {
            sbyte s when s >= 0 => (ulong)s,
            short s when s >= 0 => (ulong)s,
            int i when i >= 0 => (ulong)i,
            long l when l >= 0 => (ulong)l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            Int128 i128 when i128 >= 0 && i128 <= ulong.MaxValue => (ulong)i128,
            UInt128 u128 when u128 <= ulong.MaxValue => (ulong)u128,
            Int128 i128 when i128 < 0
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in UInt64."),
            Int128 or UInt128 => throw OutOfRange(literal, "UInt64", columnName, 0, ulong.MaxValue),
            _ when literal is sbyte or short or int or long
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in UInt64."),
            _ => throw IncompatibleLiteral(literal, "UInt64", columnName),
        };
    }

    private static InvalidOperationException OutOfRange(
        object literal, string targetName, string columnName, object min, object max) =>
        new($"Column '{columnName}': literal {literal} does not fit in {targetName} " +
            $"(range [{min}, {max}]).");

    private static T ToSignedInRange<T>(object literal, long min, long max, string columnName, string targetName)
        where T : struct
    {
        long widened = literal switch
        {
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u when u <= long.MaxValue => (long)u,
            Int128 i128 when i128 >= long.MinValue && i128 <= long.MaxValue => (long)i128,
            UInt128 u128 when u128 <= (UInt128)long.MaxValue => (long)u128,
            Int128 or UInt128 or ulong
                => throw OutOfRange(literal, targetName, columnName, min, max),
            _ => throw IncompatibleLiteral(literal, targetName, columnName),
        };

        if (widened < min || widened > max)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': literal {widened} does not fit in {targetName} " +
                $"(range [{min}, {max}]).");
        }
        return (T)Convert.ChangeType(widened, typeof(T));
    }

    private static T ToUnsignedInRange<T>(object literal, ulong max, string columnName, string targetName)
        where T : struct
    {
        ulong widened = literal switch
        {
            sbyte s when s >= 0 => (ulong)s,
            short s when s >= 0 => (ulong)s,
            int i when i >= 0 => (ulong)i,
            long l when l >= 0 => (ulong)l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            Int128 i128 when i128 >= 0 && i128 <= ulong.MaxValue => (ulong)i128,
            UInt128 u128 when u128 <= ulong.MaxValue => (ulong)u128,
            Int128 i128 when i128 < 0
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in {targetName}."),
            Int128 or UInt128
                => throw OutOfRange(literal, targetName, columnName, 0UL, max),
            _ when literal is sbyte or short or int or long
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in {targetName}."),
            _ => throw IncompatibleLiteral(literal, targetName, columnName),
        };

        if (widened > max)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': literal {widened} does not fit in {targetName} " +
                $"(max {max}).");
        }
        return (T)Convert.ChangeType(widened, typeof(T));
    }

    private static float ToFloat32Lossless(object literal, string columnName)
    {
        switch (literal)
        {
            case float f: return f;
            case double d:
            {
                float candidate = (float)d;
                // Round-trip check rejects coercions that lose precision
                // (e.g. 0.1 → Float32 isn't exact). NaN compares unequal,
                // so handle it explicitly to keep the lossless path open
                // for NaN literals (they round-trip bit-for-bit).
                if (double.IsNaN(d)) return float.NaN;
                if ((double)candidate != d)
                {
                    throw new InvalidOperationException(
                        $"Column '{columnName}': Float64 literal {d} cannot be represented exactly in Float32.");
                }
                return candidate;
            }
            case decimal m: return (float)m;
            case sbyte s: return s;
            case short s: return s;
            case int i: return i;
            case long l: return l;
            case byte b: return b;
            case ushort u: return u;
            case uint u: return u;
            case ulong u: return u;
            case Int128 i128: return (float)i128;
            case UInt128 u128: return (float)u128;
            default: throw IncompatibleLiteral(literal, "Float32", columnName);
        }
    }

    private static double ToFloat64(object literal, string columnName) =>
        literal switch
        {
            float f => f,
            double d => d,
            decimal m => (double)m,
            sbyte s => s,
            short s => s,
            int i => i,
            long l => l,
            byte b => b,
            ushort u => u,
            uint u => u,
            ulong u => u,
            Int128 i128 => (double)i128,
            UInt128 u128 => (double)u128,
            _ => throw IncompatibleLiteral(literal, "Float64", columnName),
        };

    private static InvalidOperationException IncompatibleLiteral(object literal, string targetKind, string columnName) =>
        new($"Column '{columnName}': cannot coerce {literal.GetType().Name} literal to {targetKind}.");
}
