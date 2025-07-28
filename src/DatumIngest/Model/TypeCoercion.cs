using System.Globalization;

namespace DatumIngest.Model;

/// <summary>
/// Implements the implicit numeric widening rules for the query engine type system.
/// The widening graph follows: UInt8 → Scalar → Vector → Tensor, and Matrix → Tensor.
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

        // Iterate enum values in numeric order (narrowest first) and return the
        // first kind that both inputs can reach through widening.
        foreach (DataKind candidate in Enum.GetValues<DataKind>())
        {
            if (CanWiden(kindA, candidate) && CanWiden(kindB, candidate))
            {
                return candidate;
            }
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
            DataKind.Boolean => DataKind.Scalar,
            DataKind.UInt8 => DataKind.Scalar,
            DataKind.Duration => DataKind.Scalar,
            DataKind.Scalar => DataKind.Vector,
            DataKind.Vector => DataKind.Tensor,
            DataKind.Matrix => DataKind.Tensor,
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
            DataKind.Boolean => DataValue.FromScalar(value.AsBoolean() ? 1f : 0f),
            DataKind.UInt8 => DataValue.FromScalar(value.AsUInt8()),
            DataKind.Duration => DataValue.FromScalar((float)value.AsDuration().TotalSeconds),
            DataKind.Scalar => DataValue.FromVector([value.AsScalar()]),
            DataKind.Vector => value.ToTensor(),
            DataKind.Matrix => value.ToTensor(),
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
            DataKind.Scalar or DataKind.UInt8 or DataKind.Boolean or
            DataKind.Date or DataKind.DateTime or DataKind.Time or
            DataKind.Duration or DataKind.Uuid or DataKind.JsonValue;
    }

    /// <summary>
    /// Attempts to parse a string into the target kind. Returns a typed null on failure.
    /// </summary>
    private static DataValue TryCoerceString(string text, DataKind targetKind)
    {
        return targetKind switch
        {
            DataKind.Scalar when float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands,
                CultureInfo.InvariantCulture, out float scalar) => DataValue.FromScalar(scalar),

            DataKind.UInt8 when byte.TryParse(text, NumberStyles.Integer,
                CultureInfo.InvariantCulture, out byte byteValue) => DataValue.FromUInt8(byteValue),

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
