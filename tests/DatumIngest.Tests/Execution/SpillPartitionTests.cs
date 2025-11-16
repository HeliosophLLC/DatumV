using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="SpillPartition"/>, verifying in-memory lifecycle,
/// spill-to-disk round-trips, and temporary file cleanup.
/// </summary>
public sealed class SpillPartitionTests : ServiceTestBase
{
    private readonly string _spillDirectory;
    private readonly Pool _pool = new(GlobalPool.Backing);

    /// <summary>
    /// Creates a unique temporary directory for each test run.
    /// </summary>
    public SpillPartitionTests()
    {
        _spillDirectory = Path.Combine(Path.GetTempPath(), $"spill-test-{Guid.NewGuid():N}");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        if (Directory.Exists(_spillDirectory))
        {
            Directory.Delete(_spillDirectory, recursive: true);
        }
        base.Dispose();
    }

    /// <summary>
    /// Verifies that rows added to the build side are accessible in memory when no spill occurs.
    /// </summary>
    [Fact]
    public void AddBuildRow_InMemory_RetainsRows()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Row row1 = CreateRow("id", DataValue.FromFloat32(1.0f));
        Row row2 = CreateRow("id", DataValue.FromFloat32(2.0f));
        partition.AddBuildRow(row1, sourceArena: null);
        partition.AddBuildRow(row2, sourceArena: null);

        Assert.Equal(2, partition.InMemoryBuildRowCount);
        Assert.Equal(2, partition.TotalBuildRowCount);
        Assert.False(partition.IsBuildSpilled);

        IReadOnlyList<Row> rows = partition.GetInMemoryBuildRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(1.0f, rows[0]["id"].AsFloat32());
        Assert.Equal(2.0f, rows[1]["id"].AsFloat32());
    }

    /// <summary>
    /// Verifies that rows added to the probe side are accessible in memory when no spill occurs.
    /// </summary>
    [Fact]
    public void AddProbeRow_InMemory_RetainsRows()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Row row = CreateRow("name", DataValue.FromString("test"));
        partition.AddProbeRow(row, sourceArena: null);

        Assert.Equal(1, partition.InMemoryProbeRowCount);
        Assert.Equal(1, partition.TotalProbeRowCount);
        Assert.False(partition.IsProbeSpilled);

        IReadOnlyList<Row> rows = partition.GetInMemoryProbeRows();
        Assert.Single(rows);
        Assert.Equal("test", rows[0]["name"].AsString());
    }

    /// <summary>
    /// Verifies that spilling build rows to disk clears in-memory rows and allows round-trip reading.
    /// </summary>
    [Fact]
    public void SpillBuildToDisk_ClearsMemoryAndSurvivesRoundTrip()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Row row1 = CreateRow("value", DataValue.FromFloat32(10.0f));
        Row row2 = CreateRow("value", DataValue.FromFloat32(20.0f));
        partition.AddBuildRow(row1, sourceArena: null);
        partition.AddBuildRow(row2, sourceArena: null);

        partition.SpillBuildToDisk();

        Assert.True(partition.IsBuildSpilled);
        Assert.Equal(0, partition.InMemoryBuildRowCount);
        Assert.Equal(2, partition.TotalBuildRowCount);

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Equal(2, readBack.Count);
        Assert.Equal(10.0f, readBack[0]["value"].AsFloat32());
        Assert.Equal(20.0f, readBack[1]["value"].AsFloat32());
    }

    /// <summary>
    /// Verifies that rows added after spilling are written directly to disk.
    /// </summary>
    [Fact]
    public void AddBuildRow_AfterSpill_WritesDirectlyToDisk()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        partition.AddBuildRow(CreateRow("id", DataValue.FromFloat32(1.0f)), sourceArena: null);
        partition.SpillBuildToDisk();

        // This row should go directly to the spill file.
        partition.AddBuildRow(CreateRow("id", DataValue.FromFloat32(2.0f)), sourceArena: null);

        Assert.Equal(0, partition.InMemoryBuildRowCount);
        Assert.Equal(2, partition.TotalBuildRowCount);

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Equal(2, readBack.Count);
        Assert.Equal(1.0f, readBack[0]["id"].AsFloat32());
        Assert.Equal(2.0f, readBack[1]["id"].AsFloat32());
    }

    /// <summary>
    /// Verifies that spilling probe rows to disk clears memory and allows reading.
    /// </summary>
    [Fact]
    public void SpillProbeToDisk_ClearsMemoryAndSurvivesRoundTrip()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        partition.AddProbeRow(CreateRow("label", DataValue.FromString("alpha")), sourceArena: null);
        partition.AddProbeRow(CreateRow("label", DataValue.FromString("beta")), sourceArena: null);

        partition.SpillProbeToDisk();

        Assert.True(partition.IsProbeSpilled);
        Assert.Equal(0, partition.InMemoryProbeRowCount);
        Assert.Equal(2, partition.TotalProbeRowCount);

        List<Row> readBack = partition.ReadSpilledProbeRows().ToList();
        Assert.Equal(2, readBack.Count);
        Assert.Equal("alpha", readBack[0]["label"].AsString());
        Assert.Equal("beta", readBack[1]["label"].AsString());
    }

    /// <summary>
    /// Verifies that calling SpillBuildToDisk twice is a no-op (idempotent).
    /// </summary>
    [Fact]
    public void SpillBuildToDisk_CalledTwice_IsIdempotent()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        partition.AddBuildRow(CreateRow("x", DataValue.FromFloat32(42.0f)), sourceArena: null);
        partition.SpillBuildToDisk();
        partition.SpillBuildToDisk();

        Assert.Equal(1, partition.TotalBuildRowCount);

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Single(readBack);
        Assert.Equal(42.0f, readBack[0]["x"].AsFloat32());
    }

    /// <summary>
    /// Verifies that Dispose deletes temporary spill files.
    /// </summary>
    [Fact]
    public void Dispose_DeletesTemporaryFiles()
    {
        SpillPartition partition = new(_spillDirectory, 0, _pool);

        partition.AddBuildRow(CreateRow("id", DataValue.FromFloat32(1.0f)), sourceArena: null);
        partition.SpillBuildToDisk();
        partition.AddProbeRow(CreateRow("id", DataValue.FromFloat32(2.0f)), sourceArena: null);
        partition.SpillProbeToDisk();

        // Verify files exist before dispose.
        Assert.True(Directory.Exists(_spillDirectory));

        partition.Dispose();

        // Files should be deleted (directory may still exist since Dispose only deletes files).
        string[] remainingFiles = Directory.Exists(_spillDirectory)
            ? Directory.GetFiles(_spillDirectory)
            : [];
        Assert.Empty(remainingFiles);
    }

    /// <summary>
    /// Verifies that an empty partition returns empty collections.
    /// </summary>
    [Fact]
    public void EmptyPartition_ReturnsEmptyCollections()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Assert.Equal(0, partition.InMemoryBuildRowCount);
        Assert.Equal(0, partition.InMemoryProbeRowCount);
        Assert.Equal(0, partition.TotalBuildRowCount);
        Assert.Equal(0, partition.TotalProbeRowCount);
        Assert.False(partition.IsBuildSpilled);
        Assert.False(partition.IsProbeSpilled);
        Assert.Empty(partition.GetInMemoryBuildRows());
        Assert.Empty(partition.GetInMemoryProbeRows());
    }

    /// <summary>
    /// Verifies that spilling an empty partition then reading back yields no rows.
    /// </summary>
    [Fact]
    public void SpillEmptyPartition_ReadBackYieldsNoRows()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        partition.SpillBuildToDisk();

        Assert.True(partition.IsBuildSpilled);
        Assert.Equal(0, partition.TotalBuildRowCount);

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Empty(readBack);
    }

    /// <summary>
    /// Verifies that multi-column rows survive the spill round-trip with all values intact.
    /// </summary>
    [Fact]
    public void SpillRoundTrip_MultiColumnRow_PreservesAllValues()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Row row = new(
            ["id", "name", "active"],
            [DataValue.FromFloat32(42.0f), DataValue.FromString("hello"), DataValue.FromBoolean(true)]);

        partition.AddBuildRow(row, sourceArena: null);
        partition.SpillBuildToDisk();

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Single(readBack);
        Assert.Equal(42.0f, readBack[0]["id"].AsFloat32());
        Assert.Equal("hello", readBack[0]["name"].AsString());
        Assert.True(readBack[0]["active"].AsBoolean());
    }

    /// <summary>
    /// Verifies that null values survive the spill round-trip.
    /// </summary>
    [Fact]
    public void SpillRoundTrip_NullValues_PreservesNulls()
    {
        using SpillPartition partition = new(_spillDirectory, 0, _pool);

        Row row = new(
            ["value"],
            [DataValue.Null(DataKind.Float32)]);

        partition.AddBuildRow(row, sourceArena: null);
        partition.SpillBuildToDisk();

        List<Row> readBack = partition.ReadSpilledBuildRows().ToList();
        Assert.Single(readBack);
        Assert.True(readBack[0]["value"].IsNull);
    }

    /// <summary>
    /// Verifies that multiple partitions with different indices create separate spill files.
    /// </summary>
    [Fact]
    public void MultiplePartitions_CreateSeparateSpillFiles()
    {
        using SpillPartition partition0 = new(_spillDirectory, 0, _pool);
        using SpillPartition partition1 = new(_spillDirectory, 1, _pool);

        partition0.AddBuildRow(CreateRow("id", DataValue.FromFloat32(1.0f)), sourceArena: null);
        partition1.AddBuildRow(CreateRow("id", DataValue.FromFloat32(2.0f)), sourceArena: null);

        partition0.SpillBuildToDisk();
        partition1.SpillBuildToDisk();

        List<Row> rows0 = partition0.ReadSpilledBuildRows().ToList();
        List<Row> rows1 = partition1.ReadSpilledBuildRows().ToList();

        Assert.Single(rows0);
        Assert.Equal(1.0f, rows0[0]["id"].AsFloat32());
        Assert.Single(rows1);
        Assert.Equal(2.0f, rows1[0]["id"].AsFloat32());
    }

    private static Row CreateRow(string columnName, DataValue value)
    {
        return new Row([columnName], [value]);
    }
}
