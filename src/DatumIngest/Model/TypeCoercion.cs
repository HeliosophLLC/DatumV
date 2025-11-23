using System.Globalization;

namespace DatumIngest.Model;

/// <summary>
/// Implements the implicit numeric widening rules for the query engine type system.
/// The widening graph follows the cross-sign convergent chain:
/// Boolean → UInt8 → Int16 → Int32 → Int64 → Float64 → Vector → Tensor,
/// with signed/unsigned merge points: Int8 → Int16, UInt16 → Int32, UInt32 → Int64,
/// UInt64 → Float64, Float32 → Float64, Duration → Float64, Matrix → Tensor.
/// </summary>
public static class TypeCoercion
{
    /// <summary>
    /// Determines whether a value of kind <paramref name="from"/> can be widened
    /// to kind <paramref name="to"/> by following the transitive widening chain.
    /// Same-kind widening always returns <c>true</c>.
    /// </summary>
    public static bool CanWiden(DataKind from, DataKind to)
    {
        if (from == to) return true;

        DataKind current = from;
        while (true)
        {
            DataKind? next = GetWideningTarget(current);
            if (next is null) return false;
            if (next.Value == to) return true;
            current = next.Value;
        }
    }

    /// <summary>
    /// Widens a value to the target kind, applying intermediate conversion steps as needed.
    /// Null values remain null but adopt the target kind.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when no valid widening path exists from the value's kind to <paramref name="target"/>.
    /// </exception>
    public static DataValue Widen(DataValue value, DataKind target)
    {
        if (value.Kind == target) return value;
        if (value.IsNull) return DataValue.Null(target);

        if (!CanWiden(value.Kind, target))
        {
            throw new InvalidOperationException(
                $"Cannot widen {value.Kind} to {target}.");
        }

        // Walk the widening chain one step at a time until we reach the target.
        DataValue current = value;
        while (current.Kind != target)
        {
            current = WidenOneStep(current);
        }

        return current;
    }

    /// <summary>
    /// Finds the narrowest kind that both <paramref name="kindA"/> and <paramref name="kindB"/>
    /// can widen to. Returns <c>null</c> when the kinds are incompatible.
    /// </summary>
    public static DataKind? FindCommonKind(DataKind kindA, DataKind kindB)
    {
        if (kindA == kindB) return kindA;

        // Collect all kinds reachable from A (including A itself).
        HashSet<DataKind> reachableFromA = [];
        DataKind? current = kindA;
        while (current is not null)
        {
            reachableFromA.Add(current.Value);
            current = GetWideningTarget(current.Value);
        }

        // Walk from B and return the first kind also reachable from A.
        // This yields the narrowest type both can widen to.
        current = kindB;
        while (current is not null)
        {
            if (reachableFromA.Contains(current.Value))
            {
                return current.Value;
            }

            current = GetWideningTarget(current.Value);
        }

        return null;
    }

    /// <summary>
    /// Returns the immediate widening target for a given kind, or <c>null</c>
    /// if no further widening is possible.
    /// </summary>
    private static DataKind? GetWideningTarget(DataKind kind)
    {
        return kind switch
        {
            DataKind.Boolean => DataKind.UInt8,
            DataKind.UInt8 => DataKind.Int16,
            DataKind.Int8 => DataKind.Int16,
            DataKind.Int16 => DataKind.Int32,
            DataKind.UInt16 => DataKind.Int32,
            DataKind.Int32 => DataKind.Int64,
            DataKind.UInt32 => DataKind.Int64,
            DataKind.Int64 => DataKind.Float64,
            DataKind.UInt64 => DataKind.Float64,
            DataKind.Float32 => DataKind.Float64,
            DataKind.Duration => DataKind.Float64,

            _ => null,
        };
    }

    /// <summary>
    /// Applies a single widening step, converting the value to the next type in the chain.
    /// </summary>
    private static DataValue WidenOneStep(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Boolean => DataValue.FromUInt8(value.AsBoolean() ? (byte)1 : (byte)0),
            DataKind.UInt8 => DataValue.FromInt16(value.AsUInt8()),
            DataKind.Int8 => DataValue.FromInt16(value.AsInt8()),
            DataKind.Int16 => DataValue.FromInt32(value.AsInt16()),
            DataKind.UInt16 => DataValue.FromInt32(value.AsUInt16()),
            DataKind.Int32 => DataValue.FromInt64(value.AsInt32()),
            DataKind.UInt32 => DataValue.FromInt64(value.AsUInt32()),
            DataKind.Int64 => DataValue.FromFloat64(value.AsInt64()),
            DataKind.UInt64 => DataValue.FromFloat64(value.AsUInt64()),
            DataKind.Float32 => DataValue.FromFloat64(value.AsFloat32()),
            DataKind.Duration => DataValue.FromFloat64(value.AsDuration().TotalSeconds),
            _ => throw new InvalidOperationException($"No widening step exists for {value.Kind}."),
        };
    }

    /// <summary>
    /// Attempts to coerce a value to the target kind using implicit conversion rules.
    /// First tries the standard widening chain; then falls back to string-to-type parsing.
    /// Returns a typed null when coercion fails, unlike <see cref="Widen"/> which throws.
    /// Used by CASE expression evaluation to unify mixed-type branch results.
    /// </summary>
    public static DataValue CoerceValue(DataValue value, DataKind targetKind)
    {
        if (value.Kind == targetKind) return value;
        if (value.IsNull) return DataValue.Null(targetKind);

        // Standard widening chain always succeeds.
        if (CanWiden(value.Kind, targetKind))
        {
            return Widen(value, targetKind);
        }

        // String → parseable types: attempt parsing, null on failure.
        if (value.Kind == DataKind.String)
        {
            return TryCoerceString(value.AsString(), targetKind);
        }

        // No implicit coercion path available.
        return DataValue.Null(targetKind);
    }

    /// <summary>
    /// Returns whether a CASE expression can implicitly coerce String values to
    /// the specified target kind. Used by type resolution to determine whether
    /// String + X branch combinations should resolve to X.
    /// </summary>
    internal static bool CanCoerceStringTo(DataKind targetKind)
    {
        return targetKind is
            DataKind.Float32 or DataKind.Float64 or
            DataKind.UInt8 or DataKind.Int8 or DataKind.Int16 or DataKind.UInt16 or
            DataKind.Int32 or DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64 or
            DataKind.Boolean or
            DataKind.Date or DataKind.DateTime or DataKind.Time or
            DataKind.Duration or DataKind.Uuid;
    }

    /// <summary>
    /// Attempts to parse a string into the target kind. Returns a typed null on failure.
    /// </summary>
    private static DataValue TryCoerceString(string text, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Float32 when float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out float scalar) => DataValue.FromFloat32(scalar),

            DataKind.Float64 when double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out double float64) => DataValue.FromFloat64(float64),

            DataKind.UInt8 when byte.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out byte byteValue) => DataValue.FromUInt8(byteValue),

            DataKind.Int8 when sbyte.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out sbyte int8Value) => DataValue.FromInt8(int8Value),

            DataKind.Int16 when short.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out short int16Value) => DataValue.FromInt16(int16Value),

            DataKind.UInt16 when ushort.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out ushort uint16Value) => DataValue.FromUInt16(uint16Value),

            DataKind.Int32 when int.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int int32Value) => DataValue.FromInt32(int32Value),

            DataKind.UInt32 when uint.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out uint uint32Value) => DataValue.FromUInt32(uint32Value),

            DataKind.Int64 when long.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out long int64Value) => DataValue.FromInt64(int64Value),

            DataKind.UInt64 when ulong.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out ulong uint64Value) => DataValue.FromUInt64(uint64Value),

            DataKind.Boolean => TryParseBoolean(text),

            DataKind.Date when DateOnly.TryParse(text, CultureInfo.InvariantCulture, out DateOnly date)
                => DataValue.FromDate(date),

            DataKind.DateTime when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset dateTime) => DataValue.FromDateTime(dateTime),

            DataKind.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out TimeOnly time)
                => DataValue.FromTime(time),

            DataKind.Duration when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan duration)
                => DataValue.FromDuration(duration),

            DataKind.Uuid when Guid.TryParse(text, out Guid uuid)
                => DataValue.FromUuid(uuid),

            _ => DataValue.Null(targetKind),
        };
    }

    /// <summary>
    /// Parses "true", "false", "1", "0" to Boolean. Returns null for anything else.
    /// </summary>
    private static DataValue TryParseBoolean(string text)
    {
        if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
        {
            return DataValue.FromBoolean(true);
        }

        if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
        {
            return DataValue.FromBoolean(false);
        }

        return DataValue.Null(DataKind.Boolean);
    }
}
