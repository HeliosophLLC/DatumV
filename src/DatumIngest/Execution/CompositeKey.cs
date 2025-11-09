using DatumIngest.Model;
using DatumIngest.Pooling;

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

    /// <summary>
    /// Returns the underlying parts <see cref="DataValue"/>[] to <paramref name="pool"/>.
    /// Use only when the key was created via a pool-bound <see cref="CompositeKeyComparer"/>
    /// (e.g. <see cref="CompositeKeyComparer.ForPool"/>). After this call the key must not
    /// be accessed — its bytes may be re-rented and overwritten by another caller.
    /// </summary>
    internal void ReturnPartsToPool(Pool pool) => pool.ReturnDataValues(_parts);
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
/// <para>
/// Two construction modes:
/// <list type="bullet">
/// <item><description><see cref="Instance"/> — stateless singleton, allocates each
/// inserted key's parts via <c>span.ToArray()</c>. Use when there's no <see cref="Pool"/>
/// available or when the key set's lifetime is short enough that a heap allocation per
/// unique key is acceptable.</description></item>
/// <item><description><see cref="ForPool"/> — per-instance, rents each inserted key's
/// parts from the supplied <see cref="Pool"/>. Caller must invoke
/// <see cref="ReturnPooledKeys"/> on every set/dictionary keyed with this comparer
/// before the operator finishes, to balance the rent counter.</description></item>
/// </list>
/// </para>
/// </summary>
internal sealed class CompositeKeyComparer
    : IEqualityComparer<CompositeKey>,
      IAlternateEqualityComparer<ReadOnlySpan<DataValue>, CompositeKey>
{
    /// <summary>
    /// Stateless singleton. <see cref="Create"/> allocates via <c>span.ToArray()</c>.
    /// Safe to share across threads and queries.
    /// </summary>
    internal static readonly CompositeKeyComparer Instance = new(pool: null);

    private readonly Pool? _pool;

    private CompositeKeyComparer(Pool? pool)
    {
        _pool = pool;
    }

    /// <summary>
    /// Returns a per-instance comparer that rents inserted keys' backing arrays from
    /// <paramref name="pool"/>. Callers must invoke <see cref="ReturnPooledKeys"/> on
    /// every set/dictionary keyed with this comparer (including any per-partition
    /// drain-time copies) before the operator finishes.
    /// </summary>
    public static CompositeKeyComparer ForPool(Pool pool) => new(pool);

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
    public CompositeKey Create(ReadOnlySpan<DataValue> alternate)
    {
        DataValue[] parts;
        if (_pool is not null)
        {
            parts = _pool.RentDataValues(alternate.Length);
            alternate.CopyTo(parts);
        }
        else
        {
            parts = alternate.ToArray();
        }
        return new CompositeKey(parts);
    }

    /// <summary>
    /// Returns every key's backing array to the bound pool. No-op when this comparer
    /// was constructed without a pool. After this call the keys must not be accessed.
    /// </summary>
    public void ReturnPooledKeys(IEnumerable<CompositeKey> keys)
    {
        if (_pool is null) return;
        foreach (CompositeKey key in keys)
        {
            key.ReturnPartsToPool(_pool);
        }
    }
}
