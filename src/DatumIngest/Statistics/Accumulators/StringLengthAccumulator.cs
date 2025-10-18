namespace DatumIngest.Statistics.Accumulators;

using DatumIngest.Model;

/// <summary>
/// Accumulates min and max string length for string-typed columns.
/// </summary>
public sealed class StringLengthAccumulator : IStatisticAccumulator
{
    private long _count;
    private int _minLength = int.MaxValue;
    private int _maxLength = int.MinValue;

    /// <summary>Gets the number of string values observed.</summary>
    public long Count => _count;

    /// <summary>Gets the minimum string length.</summary>
    public int MinLength => _count > 0 ? _minLength : 0;

    /// <summary>Gets the maximum string length.</summary>
    public int MaxLength => _count > 0 ? _maxLength : 0;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull) return;
        if (value.Kind is not DataKind.String and not DataKind.JsonValue) return;

        // Cached char count read directly from the DataValue — no UTF-8 decode, no allocation.
        // Strings >= 65535 chars saturate to ushort.MaxValue; that's the right signal for any
        // downstream "is this column small enough to index" decision.
        int length = value.RawCharCount;

        _count++;
        if (length < _minLength) _minLength = length;
        if (length > _maxLength) _maxLength = length;
    }

    /// <inheritdoc />
    public StatisticResult GetResult()
    {
        return new StatisticResult("string_length", new StringLengthResult(_count, MinLength, MaxLength));
    }
}

/// <summary>
/// Contains the string length accumulation results.
/// </summary>
/// <param name="Count">Number of string values processed.</param>
/// <param name="MinLength">Shortest string length.</param>
/// <param name="MaxLength">Longest string length.</param>
public sealed record StringLengthResult(long Count, int MinLength, int MaxLength)
{
    /// <summary>An empty result with zero count and zero lengths.</summary>
    public static StringLengthResult Empty { get; } = new(0, 0, 0);
}
