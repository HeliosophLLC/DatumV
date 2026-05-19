namespace Heliosoph.DatumV.Statistics.Accumulators;

using System.Formats.Cbor;
using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates root-type histogram, top-level field set (for object-rooted
/// values), and maximum nesting depth for <see cref="DataKind.Json"/>
/// columns. Stored CBOR bytes are inspected via <see cref="CborReader"/>;
/// no full JSON-text materialisation happens.
/// </summary>
/// <remarks>
/// V1 scope (Q5 Option A): root-type histogram + top-level fields + depth.
/// Recursive shape inference (per-key-path type frequencies) is deferred
/// until a real workload demonstrates the need — the current scope catches
/// the common "what shape is this column?" question without per-document
/// recursion overhead.
/// </remarks>
public sealed class JsonAccumulator : IStatisticAccumulator
{
    private long _objectCount;
    private long _arrayCount;
    private long _stringCount;
    private long _numberCount;
    private long _booleanCount;
    private long _nullCount;
    private long _otherCount;
    private int _maxDepth;
    private readonly Dictionary<string, long> _topLevelFieldCounts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull || value.Kind != DataKind.Json)
        {
            return;
        }

        // Sidecar-backed JSON requires a SidecarRegistry to resolve. The
        // collector path doesn't propagate one, so skip — large JSON
        // documents that overflow into the sidecar fall out of the
        // shape-inspection scope. Manifest still carries Count / NullCount
        // (live from PR13d) so the column isn't entirely opaque.
        if (value.IsInSidecar)
        {
            return;
        }

        ReadOnlySpan<byte> bytes = value.AsByteSpan(store);
        if (bytes.IsEmpty)
        {
            _nullCount++;
            return;
        }

        // CborReader requires ReadOnlyMemory<byte>; copy once per value.
        // JSON columns are typically small documents, so the per-value
        // allocation is acceptable; revisit if hot.
        byte[] copy = bytes.ToArray();
        CborReader reader = new(copy, CborConformanceMode.Canonical);

        try
        {
            int depth = ReadAndClassify(reader, isRoot: true, currentDepth: 1);
            if (depth > _maxDepth) _maxDepth = depth;
        }
        catch (CborContentException)
        {
            _otherCount++;
        }
    }

    /// <summary>
    /// Walks one CBOR value, classifying its root type and recursively
    /// tracking the deepest nesting reached. Top-level field names (for
    /// object-rooted values) are accumulated into
    /// <see cref="_topLevelFieldCounts"/>.
    /// </summary>
    private int ReadAndClassify(CborReader reader, bool isRoot, int currentDepth)
    {
        CborReaderState state = reader.PeekState();
        int deepest = currentDepth;

        switch (state)
        {
            case CborReaderState.StartMap:
            {
                if (isRoot) _objectCount++;
                int count = reader.ReadStartMap() ?? -1;
                while (reader.PeekState() != CborReaderState.EndMap)
                {
                    string key = reader.ReadTextString();
                    if (isRoot)
                    {
                        if (_topLevelFieldCounts.TryGetValue(key, out long c))
                            _topLevelFieldCounts[key] = c + 1;
                        else
                            _topLevelFieldCounts[key] = 1;
                    }
                    int childDepth = ReadAndClassify(reader, isRoot: false, currentDepth + 1);
                    if (childDepth > deepest) deepest = childDepth;
                }
                reader.ReadEndMap();
                break;
            }

            case CborReaderState.StartArray:
            {
                if (isRoot) _arrayCount++;
                _ = reader.ReadStartArray();
                while (reader.PeekState() != CborReaderState.EndArray)
                {
                    int childDepth = ReadAndClassify(reader, isRoot: false, currentDepth + 1);
                    if (childDepth > deepest) deepest = childDepth;
                }
                reader.ReadEndArray();
                break;
            }

            case CborReaderState.TextString:
                if (isRoot) _stringCount++;
                reader.ReadTextString();
                break;

            case CborReaderState.UnsignedInteger:
                if (isRoot) _numberCount++;
                reader.ReadUInt64();
                break;

            case CborReaderState.NegativeInteger:
                if (isRoot) _numberCount++;
                reader.ReadInt64();
                break;

            case CborReaderState.HalfPrecisionFloat:
            case CborReaderState.SinglePrecisionFloat:
            case CborReaderState.DoublePrecisionFloat:
                if (isRoot) _numberCount++;
                reader.ReadDouble();
                break;

            case CborReaderState.Boolean:
                if (isRoot) _booleanCount++;
                reader.ReadBoolean();
                break;

            case CborReaderState.Null:
                if (isRoot) _nullCount++;
                reader.ReadNull();
                break;

            default:
                if (isRoot) _otherCount++;
                reader.SkipValue();
                break;
        }

        return deepest;
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        Dictionary<string, long> rootCounts = new(StringComparer.Ordinal);
        if (_objectCount > 0)  rootCounts["object"]  = _objectCount;
        if (_arrayCount > 0)   rootCounts["array"]   = _arrayCount;
        if (_stringCount > 0)  rootCounts["string"]  = _stringCount;
        if (_numberCount > 0)  rootCounts["number"]  = _numberCount;
        if (_booleanCount > 0) rootCounts["boolean"] = _booleanCount;
        if (_nullCount > 0)    rootCounts["null"]    = _nullCount;
        if (_otherCount > 0)   rootCounts["other"]   = _otherCount;

        yield return new StatisticResult("json_stats", new JsonStatsResult(
            rootCounts,
            new Dictionary<string, long>(_topLevelFieldCounts, StringComparer.Ordinal),
            _maxDepth));
    }
}

/// <summary>
/// JSON-column statistics produced by <see cref="JsonAccumulator"/>.
/// </summary>
/// <param name="RootTypeCounts">Counts of root-type per value (object / array / string / number / boolean / null / other).</param>
/// <param name="TopLevelFieldCounts">For object-rooted values, the count of values where each top-level key was present. Empty when no object-rooted values were observed.</param>
/// <param name="MaxDepth">Maximum nesting depth observed across all values. 1 for scalar-rooted values; 2 for an object containing scalar values; etc.</param>
public sealed record JsonStatsResult(
    IReadOnlyDictionary<string, long> RootTypeCounts,
    IReadOnlyDictionary<string, long> TopLevelFieldCounts,
    int MaxDepth)
{
    /// <summary>An empty result.</summary>
    public static JsonStatsResult Empty { get; } =
        new(new Dictionary<string, long>(), new Dictionary<string, long>(), 0);
}
