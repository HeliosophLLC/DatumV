using DatumQuery.Model;

namespace DatumQuery.Execution;

/// <summary>
/// A composite hash key formed from multiple <see cref="DataValue"/> parts,
/// used as a dictionary key for compound equi-joins.
/// Provides value-equality semantics over the ordered sequence of parts.
/// </summary>
public readonly struct CompositeKey : IEquatable<CompositeKey>
{
    private readonly DataValue[] _parts;
    private readonly int _hashCode;

    /// <summary>
    /// Creates a composite key from the given parts.
    /// </summary>
    /// <param name="parts">The ordered key components. Must not be null or empty.</param>
    public CompositeKey(DataValue[] parts)
    {
        _parts = parts;

        HashCode hash = new();
        for (int index = 0; index < parts.Length; index++)
        {
            hash.Add(parts[index]);
        }
        _hashCode = hash.ToHashCode();
    }

    /// <inheritdoc/>
    public bool Equals(CompositeKey other)
    {
        if (_parts.Length != other._parts.Length)
        {
            return false;
        }

        for (int index = 0; index < _parts.Length; index++)
        {
            if (!_parts[index].Equals(other._parts[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is CompositeKey key && Equals(key);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <inheritdoc/>
    public static bool operator ==(CompositeKey left, CompositeKey right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(CompositeKey left, CompositeKey right) => !left.Equals(right);
}
