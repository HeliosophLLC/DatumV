using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Execution;

/// <summary>
/// A single hash partition in a Grace hash join that can hold rows in memory
/// or spill them to a temporary file on disk when memory pressure is reached.
/// </summary>
/// <remarks>
/// <para>
/// Each partition maintains separate build-side and probe-side row collections.
/// Build-side rows are stored with pool-rented <see cref="DataValue"/> arrays
/// (via <see cref="LocalBufferPool.RentCopy"/>) so the input batch can be
/// returned immediately. When <see cref="SpillBuildToDisk"/> or
/// <see cref="SpillProbeToDisk"/> is called, the in-memory rows are flushed
/// to a temporary file via <see cref="RowSerializer"/> and pool-rented arrays
/// are returned. Subsequent rows are appended directly to the spill file.
/// Reading spilled rows back uses <see cref="ReadSpilledBuildRows"/> /
/// <see cref="ReadSpilledProbeRows"/>.
/// </para>
/// <para>
/// Callers must call <see cref="Dispose"/> to delete temporary files and
/// return any remaining pool-rented arrays.
/// </para>
/// </remarks>
internal sealed class SpillPartition : IDisposable
{
    private List<Row>? _buildRows = new();
    private List<Row>? _probeRows = new();

    private string? _buildSpillPath;
    private string? _probeSpillPath;
    private BinaryWriter? _buildSpillWriter;
    private BinaryWriter? _probeSpillWriter;
    private bool _buildSchemaWritten;
    private bool _probeSchemaWritten;
    private int _spilledBuildRowCount;
    private int _spilledProbeRowCount;

    private readonly string _spillDirectory;
    private readonly int _partitionIndex;
    private readonly LocalBufferPool? _pool;
    private readonly Pool _arenaPool;
    private Arena? _retentionArena;

    /// <summary>
    /// Creates a new partition.
    /// </summary>
    /// <param name="spillDirectory">The temporary directory for spill files.</param>
    /// <param name="partitionIndex">The zero-based index of this partition (used in file names).</param>
    /// <param name="arenaPool">
    /// The arena-aware pool. Used to lazy-rent a per-partition retention arena that holds
    /// stabilized payload bytes for arena-backed values, so input batches can be safely
    /// returned without invalidating the rows we keep.
    /// </param>
    /// <param name="estimatedBuildRows">
    /// Estimated number of build-side rows for this partition. Used to pre-size the
    /// internal list and avoid repeated LOH-crossing doublings that drive Gen2 GC pressure.
    /// Zero or negative values fall back to the default list capacity.
    /// </param>
    /// <param name="pool">
    /// Optional buffer pool for renting <see cref="DataValue"/> array copies.
    /// When provided, <see cref="AddBuildRow"/> copies the row's values into a
    /// pool-rented array so the input batch can be returned immediately.
    /// </param>
    internal SpillPartition(string spillDirectory, int partitionIndex, Pool arenaPool, int estimatedBuildRows = 0, LocalBufferPool? pool = null)
    {
        _spillDirectory = spillDirectory;
        _partitionIndex = partitionIndex;
        _arenaPool = arenaPool;
        _pool = pool;
        if (estimatedBuildRows > 0)
        {
            _buildRows = new List<Row>(estimatedBuildRows);
        }
    }

    /// <summary>
    /// The retention arena for this partition. Holds stabilized payload bytes for any
    /// arena-backed values added via <see cref="AddBuildRow"/> / <see cref="AddProbeRow"/>.
    /// Lazily allocated on first stabilization. Exposed so callers handing rows from one
    /// partition to another (e.g. recursive repartitioning) can pass it as the source arena.
    /// </summary>
    internal Arena? RetentionArena => _retentionArena;

    /// <summary>The number of build-side rows currently held in memory.</summary>
    internal int InMemoryBuildRowCount => _buildRows?.Count ?? 0;

    /// <summary>The number of probe-side rows currently held in memory.</summary>
    internal int InMemoryProbeRowCount => _probeRows?.Count ?? 0;

    /// <summary>The total number of build-side rows (in-memory + spilled).</summary>
    internal int TotalBuildRowCount => InMemoryBuildRowCount + _spilledBuildRowCount;

    /// <summary>The total number of probe-side rows (in-memory + spilled).</summary>
    internal int TotalProbeRowCount => InMemoryProbeRowCount + _spilledProbeRowCount;

    /// <summary>Whether build-side rows have been spilled to disk.</summary>
    internal bool IsBuildSpilled => _buildSpillPath is not null;

    /// <summary>Whether probe-side rows have been spilled to disk.</summary>
    internal bool IsProbeSpilled => _probeSpillPath is not null;

    /// <summary>
    /// Adds a build-side row. The row's values are stabilized into the partition's
    /// retention arena so the source batch can be returned without invalidating the
    /// arena offsets in arena-backed payloads. If the partition has already been
    /// spilled, the row is written directly to the spill file.
    /// </summary>
    /// <param name="row">The row to add.</param>
    /// <param name="sourceArena">
    /// The arena holding the row's payload bytes (typically the source batch's arena,
    /// or the prior partition's <see cref="RetentionArena"/> when re-partitioning).
    /// May be null when the caller cannot supply one (e.g. rows read back from a spill
    /// file via the legacy codec, where only inline values are reliably round-tripped);
    /// in that case the row is stored without arena stabilization.
    /// </param>
    internal void AddBuildRow(Row row, Arena? sourceArena)
    {
        if (_buildSpillWriter is not null)
        {
            WriteRowToSpill(_buildSpillWriter, row, ref _buildSchemaWritten);
            _spilledBuildRowCount++;
        }
        else
        {
            _buildRows!.Add(StabilizeRow(row, sourceArena));
        }
    }

    /// <summary>
    /// Adds a probe-side row. The row's values are stabilized into the partition's
    /// retention arena so the source batch can be returned without invalidating the
    /// arena offsets in arena-backed payloads. If the partition has already been
    /// spilled, the row is written directly to the spill file.
    /// </summary>
    /// <param name="row">The row to add.</param>
    /// <param name="sourceArena">
    /// The arena holding the row's payload bytes (typically the source batch's arena,
    /// or the prior partition's <see cref="RetentionArena"/> when re-partitioning).
    /// May be null when the caller cannot supply one (e.g. rows read back from a spill
    /// file via the legacy codec, where only inline values are reliably round-tripped);
    /// in that case the row is stored without arena stabilization.
    /// </param>
    internal void AddProbeRow(Row row, Arena? sourceArena)
    {
        if (_probeSpillWriter is not null)
        {
            WriteRowToSpill(_probeSpillWriter, row, ref _probeSchemaWritten);
            _spilledProbeRowCount++;
        }
        else
        {
            _probeRows!.Add(StabilizeRow(row, sourceArena));
        }
    }

    private Row StabilizeRow(Row row, Arena? sourceArena)
    {
        ReadOnlySpan<DataValue> source = row.RawValues;
        DataValue[] copy = _pool is not null
            ? _pool.Rent(source.Length)
            : new DataValue[source.Length];

        if (sourceArena is null)
        {
            // Legacy path — caller didn't supply an arena. Fall back to a shallow array
            // copy. Safe for inline values; arena-backed values will become stale once
            // the source arena is recycled. Step C will tighten this.
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = source[i];
            }
        }
        else
        {
            _retentionArena ??= _arenaPool.Backing.RentArena();
            for (int i = 0; i < source.Length; i++)
            {
                copy[i] = DataValueRetention.Stabilize(source[i], sourceArena, _retentionArena);
            }
        }

        return new Row(row.RawNames, copy, row.RawNameIndex);
    }

    /// <summary>
    /// Flushes in-memory build rows to a temporary file and clears them from memory.
    /// Pool-rented <see cref="DataValue"/> arrays are returned after serialization.
    /// Subsequent <see cref="AddBuildRow"/> calls write directly to the spill file.
    /// </summary>
    internal void SpillBuildToDisk()
    {
        if (_buildSpillWriter is not null)
        {
            return;
        }

        Directory.CreateDirectory(_spillDirectory);
        _buildSpillPath = Path.Combine(_spillDirectory, $"build_{_partitionIndex}.spill");
        FileStream fileStream = new(_buildSpillPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        _buildSpillWriter = new BinaryWriter(fileStream);

        if (_buildRows is not null)
        {
            foreach (Row row in _buildRows)
            {
                WriteRowToSpill(_buildSpillWriter, row, ref _buildSchemaWritten);
                _spilledBuildRowCount++;

                // Return pool-rented arrays — the row is now serialized to disk.
                if (_pool is not null)
                {
                    _pool.ReturnValues(row);
                }
            }

            _buildRows.Clear();
            _buildRows = null;
        }
    }

    /// <summary>
    /// Flushes in-memory probe rows to a temporary file and clears them from memory.
    /// Subsequent <see cref="AddProbeRow"/> calls write directly to the spill file.
    /// </summary>
    internal void SpillProbeToDisk()
    {
        if (_probeSpillWriter is not null)
        {
            return;
        }

        Directory.CreateDirectory(_spillDirectory);
        _probeSpillPath = Path.Combine(_spillDirectory, $"probe_{_partitionIndex}.spill");
        FileStream fileStream = new(_probeSpillPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536);
        _probeSpillWriter = new BinaryWriter(fileStream);

        if (_probeRows is not null)
        {
            foreach (Row row in _probeRows)
            {
                WriteRowToSpill(_probeSpillWriter, row, ref _probeSchemaWritten);
                _spilledProbeRowCount++;
            }

            _probeRows.Clear();
            _probeRows = null;
        }
    }

    /// <summary>
    /// Returns the in-memory build rows. Only valid when <see cref="IsBuildSpilled"/> is false.
    /// </summary>
    internal IReadOnlyList<Row> GetInMemoryBuildRows()
    {
        return _buildRows ?? (IReadOnlyList<Row>)[];
    }

    /// <summary>
    /// Returns the in-memory probe rows. Only valid when <see cref="IsProbeSpilled"/> is false.
    /// </summary>
    internal IReadOnlyList<Row> GetInMemoryProbeRows()
    {
        return _probeRows ?? (IReadOnlyList<Row>)[];
    }

    /// <summary>
    /// Reads all spilled build-side rows from disk. The spill writer is flushed and closed
    /// before reading. Only valid when <see cref="IsBuildSpilled"/> is true.
    /// </summary>
    internal IEnumerable<Row> ReadSpilledBuildRows()
    {
        FlushAndCloseWriter(ref _buildSpillWriter);
        return ReadSpilledRows(_buildSpillPath, _spilledBuildRowCount);
    }

    /// <summary>
    /// Reads all spilled probe-side rows from disk. The spill writer is flushed and closed
    /// before reading. Only valid when <see cref="IsProbeSpilled"/> is true.
    /// </summary>
    internal IEnumerable<Row> ReadSpilledProbeRows()
    {
        FlushAndCloseWriter(ref _probeSpillWriter);
        return ReadSpilledRows(_probeSpillPath, _spilledProbeRowCount);
    }

    /// <summary>
    /// Disposes the partition, closing any open spill writers, deleting temporary files,
    /// and returning any pool-rented <see cref="DataValue"/> arrays still held in memory.
    /// </summary>
    public void Dispose()
    {
        _buildSpillWriter?.Dispose();
        _buildSpillWriter = null;
        _probeSpillWriter?.Dispose();
        _probeSpillWriter = null;

        // Return pool-rented build row arrays that weren't spilled.
        if (_buildRows is not null && _pool is not null)
        {
            foreach (Row row in _buildRows)
            {
                _pool.ReturnValues(row);
            }
        }

        // Probe rows also use pool-rented arrays since stabilization rents from the same pool.
        if (_probeRows is not null && _pool is not null)
        {
            foreach (Row row in _probeRows)
            {
                _pool.ReturnValues(row);
            }
        }

        _buildRows = null;
        _probeRows = null;

        if (_retentionArena is not null)
        {
            _arenaPool.Backing.TryReturn(_retentionArena);
            _retentionArena = null;
        }

        if (_buildSpillPath is not null && File.Exists(_buildSpillPath))
        {
            File.Delete(_buildSpillPath);
            _buildSpillPath = null;
        }

        if (_probeSpillPath is not null && File.Exists(_probeSpillPath))
        {
            File.Delete(_probeSpillPath);
            _probeSpillPath = null;
        }
    }

    private static void WriteRowToSpill(BinaryWriter writer, Row row, ref bool schemaWritten)
    {
        if (!schemaWritten)
        {
            RowSerializer.WriteSchema(writer, row);
            schemaWritten = true;
        }

        RowSerializer.WriteRow(writer, row);
    }

    private static void FlushAndCloseWriter(ref BinaryWriter? writer)
    {
        if (writer is not null)
        {
            writer.Flush();
            writer.Dispose();
            writer = null;
        }
    }

    private static IEnumerable<Row> ReadSpilledRows(
        string? spillPath,
        int rowCount)
    {
        if (spillPath is null || rowCount == 0)
        {
            yield break;
        }

        using FileStream fileStream = new(spillPath, FileMode.Open, FileAccess.Read, FileShare.None, 65536);
        using BinaryReader reader = new(fileStream);

        RowSerializer.ReadSchema(reader, out string[] names, out Dictionary<string, int> nameIndex);

        for (int index = 0; index < rowCount; index++)
        {
            yield return RowSerializer.ReadRow(reader, names, nameIndex);
        }
    }
}
