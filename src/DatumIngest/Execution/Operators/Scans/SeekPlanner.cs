using DatumIngest.Indexing;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// Accumulator that picks the fewest-positions winner across every
/// <see cref="ISeekStrategy"/>'s candidate position lists. Owns the
/// active-chunk filtering and the position-sort that the seek executor
/// expects.
/// </summary>
/// <remarks>
/// <para>
/// Strategies call <see cref="Submit"/> (or
/// <see cref="SubmitCompositeEntries"/> for the composite-index path,
/// which also updates the diagnostic counter) with each candidate list.
/// The planner keeps a single <c>bestPositions</c> reference, replacing
/// it any time a smaller candidate arrives.
/// </para>
/// <para>
/// <see cref="Finalize"/> returns the winning list (sorted ascending) or
/// <see langword="null"/> when no strategy submitted anything usable.
/// </para>
/// </remarks>
internal sealed class SeekPlanner
{
    private readonly IReadOnlyList<IndexChunk> _chunks;
    private readonly HashSet<int> _activeChunkIndexes;

    private List<long>? _bestPositions;
    private int? _compositeHits;

    public SeekPlanner(IReadOnlyList<IndexChunk> chunks, HashSet<int> activeChunkIndexes)
    {
        _chunks = chunks;
        _activeChunkIndexes = activeChunkIndexes;
    }

    /// <summary>
    /// Number of positions the composite-index branch contributed across
    /// every call to <see cref="SubmitCompositeEntries"/>, or
    /// <see langword="null"/> when no composite path ran. Independent of
    /// the fewest-positions tiebreak — tests use this counter to prove
    /// composite indexes were consulted even when a single-column index
    /// happened to win.
    /// </summary>
    public int? CompositeHits => _compositeHits;

    /// <summary>
    /// Converts <paramref name="entries"/> into absolute row positions
    /// (filtered to active chunks) and submits the list as a candidate.
    /// </summary>
    public void SubmitEntries(IReadOnlyList<ValueIndexEntry> entries)
        => Submit(BuildPositions(entries));

    /// <summary>
    /// Same as <see cref="SubmitEntries"/> but also accumulates the count
    /// into <see cref="CompositeHits"/> regardless of whether this
    /// candidate wins the fewest-positions tiebreak.
    /// </summary>
    public void SubmitCompositeEntries(IReadOnlyList<ValueIndexEntry> entries)
    {
        List<long> positions = BuildPositions(entries);
        _compositeHits = (_compositeHits ?? 0) + positions.Count;
        Submit(positions);
    }

    /// <summary>
    /// Submits a pre-built position list. Used by the IN strategy which
    /// unions multiple entry lists into one candidate before submitting.
    /// </summary>
    public void Submit(List<long> positions)
    {
        if (_bestPositions is null || positions.Count < _bestPositions.Count)
        {
            _bestPositions = positions;
        }
    }

    /// <summary>
    /// Converts index entries into absolute row positions, keeping only
    /// entries that land in active (non-pruned) chunks. Exposed so
    /// strategies can build up a unioned candidate (e.g. IN's
    /// per-value union) before <see cref="Submit"/>ing it.
    /// </summary>
    public List<long> BuildPositions(IReadOnlyList<ValueIndexEntry> entries)
    {
        List<long> positions = new(entries.Count);
        foreach (ValueIndexEntry entry in entries)
        {
            if (_activeChunkIndexes.Contains(entry.ChunkIndex))
            {
                long absoluteRow = _chunks[entry.ChunkIndex].RowOffset
                    + entry.RowOffsetInChunk;
                positions.Add(absoluteRow);
            }
        }
        return positions;
    }

    /// <summary>
    /// Returns the winning candidate sorted ascending, or
    /// <see langword="null"/> if no strategy contributed any positions.
    /// </summary>
    public List<long>? Finalize()
    {
        if (_bestPositions is null || _bestPositions.Count == 0)
        {
            return _bestPositions;
        }
        _bestPositions.Sort();
        return _bestPositions;
    }
}
