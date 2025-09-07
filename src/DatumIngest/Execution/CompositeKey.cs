using DatumIngest.Model;

namespace DatumIngest.Execution;

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

    /// <summary>
    /// Gets the key component at the specified position.
    /// </summary>
    /// <param name="index">Zero-based index of the key component.</param>
    public DataValue this[int index] => _parts[index];

    /// <summary>
    /// Gets the underlying key components as a read-only span for replay during
    /// accumulator merge operations.
    /// </summary>
    public ReadOnlySpan<DataValue> Values => _parts;

    /// <inheritdoc/>
    public override bool Equals(object? other) => other is CompositeKey key && Equals(key);

    /// <inheritdoc/>
    public override int GetHashCode() => _hashCode;

    /// <summary>
    /// Formats the key values for use in error messages.
    /// </summary>
    public override string ToString()
    {
        return string.Join(", ", _parts.Select(static value => value.IsNull ? "NULL" : value.ToString()));
    }

    /// <inheritdoc/>
    public static bool operator ==(CompositeKey left, CompositeKey right) => left.Equals(right);

    /// <inheritdoc/>
    public static bool operator !=(CompositeKey left, CompositeKey right) => !left.Equals(right);
}

/// <summary>
/// Combined equality comparer for <see cref="CompositeKey"/> that also implements
/// <see cref="IAlternateEqualityComparer{TAlternate, TKey}"/> for
/// <c>ReadOnlySpan&lt;DataValue&gt;</c>.
/// Pass this comparer to a <see cref="System.Collections.Generic.Dictionary{TKey,TValue}"/>
/// constructor and then call <c>GetAlternateLookup&lt;ReadOnlySpan&lt;DataValue&gt;&gt;()</c>
/// to probe the dictionary with a reusable scratch buffer — eliminating the per-row
/// <see cref="DataValue"/> array heap allocation that a plain <c>new CompositeKey(...)</c>
/// lookup would otherwise incur on every probe row.
/// </summary>
internal sealed class CompositeKeyComparer
    : IEqualityComparer<CompositeKey>,
      IAlternateEqualityComparer<ReadOnlySpan<DataValue>, CompositeKey>
{
    /// <summary>The singleton instance. Stateless and thread-safe.</summary>
    internal static readonly CompositeKeyComparer Instance = new();

    private CompositeKeyComparer() { }

    /// <inheritdoc/>
    public bool Equals(CompositeKey x, CompositeKey y) => x.Equals(y);

    /// <inheritdoc/>
    public int GetHashCode(CompositeKey obj) => obj.GetHashCode();

    /// <inheritdoc/>
    public bool Equals(ReadOnlySpan<DataValue> alternate, CompositeKey other)
    {
        ReadOnlySpan<DataValue> otherValues = other.Values;

        if (alternate.Length != otherValues.Length)
        {
            return false;
        }

        for (int i = 0; i < alternate.Length; i++)
        {
            if (!alternate[i].Equals(otherValues[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public int GetHashCode(ReadOnlySpan<DataValue> alternate)
    {
        HashCode hash = new();

        for (int i = 0; i < alternate.Length; i++)
        {
            hash.Add(alternate[i]);
        }

        return hash.ToHashCode();
    }

    /// <inheritdoc/>
    public CompositeKey Create(ReadOnlySpan<DataValue> alternate) => new(alternate.ToArray());
}
