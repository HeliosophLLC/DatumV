using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Coerces a CLR literal value (as produced by
/// <c>SqlParser.NarrowNumericLiteral</c>: <see cref="sbyte"/> / <see cref="short"/> /
/// <see cref="int"/> / <see cref="long"/> for integers, <see cref="float"/> /
/// <see cref="double"/> for fractionals) into a <see cref="DataValue"/>
/// of a target <see cref="DataKind"/>. Lossless coercions are accepted;
/// lossy or cross-family coercions throw a descriptive
/// <see cref="InvalidOperationException"/>.
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

        return target.Kind switch
        {
            DataKind.Boolean => CoerceBoolean(literal, columnName),
            DataKind.Int8 => DataValue.FromInt8(ToSignedInRange<sbyte>(literal, sbyte.MinValue, sbyte.MaxValue, columnName, "Int8")),
            DataKind.Int16 => DataValue.FromInt16(ToSignedInRange<short>(literal, short.MinValue, short.MaxValue, columnName, "Int16")),
            DataKind.Int32 => DataValue.FromInt32(ToSignedInRange<int>(literal, int.MinValue, int.MaxValue, columnName, "Int32")),
            DataKind.Int64 => DataValue.FromInt64(ToInt64(literal, columnName)),
            DataKind.UInt8 => DataValue.FromUInt8(ToUnsignedInRange<byte>(literal, byte.MaxValue, columnName, "UInt8")),
            DataKind.UInt16 => DataValue.FromUInt16(ToUnsignedInRange<ushort>(literal, ushort.MaxValue, columnName, "UInt16")),
            DataKind.UInt32 => DataValue.FromUInt32(ToUnsignedInRange<uint>(literal, uint.MaxValue, columnName, "UInt32")),
            DataKind.UInt64 => DataValue.FromUInt64(ToUInt64(literal, columnName)),
            DataKind.Float32 => DataValue.FromFloat32(ToFloat32Lossless(literal, columnName)),
            DataKind.Float64 => DataValue.FromFloat64(ToFloat64(literal, columnName)),
            DataKind.String => CoerceString(literal, arena, columnName),
            DataKind.Uuid => CoerceUuid(literal, columnName),
            DataKind.Date => CoerceDate(literal, columnName),
            DataKind.Time => CoerceTime(literal, columnName),
            DataKind.DateTime => CoerceDateTime(literal, columnName),
            DataKind.Duration => CoerceDuration(literal, columnName),
            DataKind.Decimal => CoerceDecimal(literal, columnName),
            _ => throw new InvalidOperationException(
                $"INSERT VALUES for column '{columnName}': literal coercion to " +
                $"{target.Kind} is not yet supported."),
        };
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

    private static DataValue CoerceDateTime(object literal, string columnName) =>
        literal switch
        {
            DateTimeOffset dto => DataValue.FromDateTime(dto),
            DateTime dt => DataValue.FromDateTime(new DateTimeOffset(
                dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt)),
            DateOnly d => DataValue.FromDateTime(new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)),
            string s when DateTimeOffset.TryParse(s, out DateTimeOffset parsed)
                => DataValue.FromDateTime(parsed),
            _ => throw IncompatibleLiteral(literal, "DateTime", columnName),
        };

    private static DataValue CoerceDuration(object literal, string columnName) =>
        literal switch
        {
            TimeSpan ts => DataValue.FromDuration(ts),
            string s when TimeSpan.TryParse(s, out TimeSpan parsed) => DataValue.FromDuration(parsed),
            _ => throw IncompatibleLiteral(literal, "Duration", columnName),
        };

    private static DataValue CoerceDecimal(object literal, string columnName) =>
        literal switch
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
            // Float→decimal could lose precision silently; require an explicit
            // decimal-typed literal or cast on the SQL side.
            string s when decimal.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out decimal parsed)
                => DataValue.FromDecimal(parsed),
            _ => throw IncompatibleLiteral(literal, "Decimal", columnName),
        };

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
            _ => throw IncompatibleLiteral(literal, "Int64", columnName),
        };

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
            _ when literal is sbyte or short or int or long
                => throw new InvalidOperationException(
                    $"Column '{columnName}': cannot store negative literal in UInt64."),
            _ => throw IncompatibleLiteral(literal, "UInt64", columnName),
        };
    }

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
            _ => throw IncompatibleLiteral(literal, "Float64", columnName),
        };

    private static InvalidOperationException IncompatibleLiteral(object literal, string targetKind, string columnName) =>
        new($"Column '{columnName}': cannot coerce {literal.GetType().Name} literal to {targetKind}.");
}
