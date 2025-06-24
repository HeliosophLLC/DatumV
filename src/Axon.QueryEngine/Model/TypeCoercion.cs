namespace Axon.QueryEngine.Model;

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
            DataKind.UInt8 => DataKind.Scalar,
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
            DataKind.UInt8 => DataValue.FromScalar(value.AsUInt8()),
            DataKind.Scalar => DataValue.FromVector([value.AsScalar()]),
            DataKind.Vector => value.ToTensor(),
            DataKind.Matrix => value.ToTensor(),
            _ => throw new InvalidOperationException($"No widening step exists for {value.Kind}."),
        };
    }
}
