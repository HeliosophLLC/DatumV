using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.GroupBy;

/// <summary>
/// Hash-table abstraction for keyed GROUP BY aggregation. Hides the
/// single-key / composite-key split so the operator's hot path is shape-free.
/// </summary>
/// <remarks>
/// <para>
/// Two flavours of operation:
/// <list type="bullet">
/// <item><description><b>Scratch-based</b> (per-row hot path):
/// <see cref="EvaluateAsync"/>, <see cref="TryGetExisting"/>,
/// <see cref="InsertNew"/>, <see cref="HashScratch"/>,
/// <see cref="StabilizeScratchInto"/>. These operate on the table's reusable
/// internal key buffer — fills via <see cref="EvaluateAsync"/>, all later
/// calls consume the buffer without re-evaluation.</description></item>
/// <item><description><b>Key-based</b> (spill drain replay):
/// <see cref="Contains"/>, <see cref="TryGetByKey"/>, <see cref="Insert"/>.
/// Operate on a caller-supplied <see cref="DataValue"/>[] extracted from a
/// replayed spill row.</description></item>
/// </list>
/// </para>
/// <para>
/// Use <see cref="CreatePartitionLocal"/> from the main table to build the
/// drain-time partition-local tables — they share the same key shape but are
/// otherwise independent.
/// </para>
/// </remarks>
internal interface IHashGroupTable
{
    /// <summary>Number of GROUP BY key expressions (1 for single-key tables).</summary>
    int KeyCount { get; }

    /// <summary>Number of groups currently materialized in this table.</summary>
    int Count { get; }

    /// <summary>Enumerates every materialized <see cref="GroupState"/>.</summary>
    IEnumerable<GroupState> AllGroups { get; }

    /// <summary>
    /// Evaluates the GROUP BY expressions for this row into the table's
    /// internal scratch buffer. Subsequent scratch-based calls
    /// (<see cref="TryGetExisting"/>, <see cref="InsertNew"/>,
    /// <see cref="HashScratch"/>, <see cref="StabilizeScratchInto"/>) operate
    /// on the values just written.
    /// </summary>
    ValueTask EvaluateAsync(ExpressionEvaluator evaluator, Row row, CancellationToken cancellationToken);

    /// <summary>
    /// Looks up the current scratch in the table. Returns the matching
    /// <see cref="GroupState"/> if present, otherwise <see langword="null"/>.
    /// </summary>
    GroupState? TryGetExisting();

    /// <summary>
    /// Captures the current scratch into a fresh permanent
    /// <see cref="DataValue"/>[], assigns it to <paramref name="group"/>.KeyValues,
    /// and inserts the group into the table.
    /// </summary>
    void InsertNew(GroupState group);

    /// <summary>
    /// Hash of the current scratch — used to route rows to spill partitions.
    /// </summary>
    int HashScratch();

    /// <summary>
    /// Writes the current scratch into <paramref name="dest"/> at offset 0,
    /// stabilising each value from <paramref name="from"/> into
    /// <paramref name="to"/>. Returns the number of values written
    /// (== <see cref="KeyCount"/>).
    /// </summary>
    int StabilizeScratchInto(Span<DataValue> dest, Arena from, Arena to);

    /// <summary>
    /// Reads <see cref="KeyCount"/> values starting at <paramref name="offset"/>
    /// from <paramref name="spillRow"/> into a fresh <see cref="DataValue"/>[]
    /// and advances <paramref name="offset"/>. Used during drain replay before
    /// dedup / partition-local lookup.
    /// </summary>
    DataValue[] ReadKeyFromRow(Row spillRow, ref int offset);

    /// <summary>
    /// Returns <see langword="true"/> when this table already holds an entry
    /// for the supplied key. Used during drain replay to skip keys that
    /// already exist in the main in-memory table.
    /// </summary>
    bool Contains(DataValue[] key);

    /// <summary>
    /// Looks up a previously-extracted key (e.g. from a drain replay). Returns
    /// the existing <see cref="GroupState"/> or <see langword="null"/>.
    /// </summary>
    GroupState? TryGetByKey(DataValue[] key);

    /// <summary>
    /// Inserts a fresh group keyed on the supplied <paramref name="key"/>.
    /// Assigns the key array to <paramref name="group"/>.KeyValues without
    /// copying — the array is taken ownership of.
    /// </summary>
    void Insert(DataValue[] key, GroupState group);

    /// <summary>
    /// Creates an empty table of the same key shape. Used to build the
    /// per-partition drain-time tables.
    /// </summary>
    IHashGroupTable CreatePartitionLocal();
}
