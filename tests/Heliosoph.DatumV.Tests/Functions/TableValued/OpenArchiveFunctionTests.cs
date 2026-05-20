using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.TableValued;
using Heliosoph.DatumV.Model;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Tests.Functions.TableValued;

/// <summary>
/// <c>open_archive(source [, path_pattern])</c> table-valued function: opens
/// a ZIP / TAR archive and yields one row per regular-file entry with the
/// body bytes materialized into the query arena. Covers schema declaration,
/// per-container round-trips, the path-pattern filter, the metadata timestamp,
/// and the deliberate "no auto OS-metadata filter" raw-scan contract.
/// </summary>
public sealed class OpenArchiveFunctionTests : ServiceTestBase
{
    // open_archive yields batches backed by per-batch arenas (see "Arena
    // isolation" remark on the function): each row's payload coordinates resolve
    // against batch.Arena, not the per-query Store. We Stabilize each value into
    // ctx.Store while the batch arena is still alive so the test helpers can
    // continue to read through ctx.Store after collection.
    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches, ExecutionContext ctx)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, ctx.Store);
                }
                rows.Add(new Row(source.ColumnLookup, stabilized));
            }
        }
        return rows;
    }

    private static byte[] PayloadOf(Row row, ExecutionContext ctx)
        => row["bytes"].AsByteSpan(ctx.Store, registry: null).ToArray();

    private static string PathOf(Row row) => row["path"].AsString();
    private static long SizeOf(Row row) => row["size"].AsInt64();

    // ───────────────────────── Schema ─────────────────────────

    [Fact]
    public void ValidateArguments_DeclaresPathSizeModifiedBytesSchema()
    {
        OpenArchiveFunction fn = new();

        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(4, schema.Columns.Count);
        Assert.Equal("path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.False(schema.Columns[0].Nullable);

        Assert.Equal("size", schema.Columns[1].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[1].Kind);

        Assert.Equal("modified", schema.Columns[2].Name);
        Assert.Equal(DataKind.TimestampTz, schema.Columns[2].Kind);
        Assert.True(schema.Columns[2].Nullable);

        Assert.Equal("bytes", schema.Columns[3].Name);
        Assert.Equal(DataKind.UInt8, schema.Columns[3].Kind);
        Assert.True(schema.Columns[3].IsArray);
    }

    [Fact]
    public void ValidateArguments_RejectsNonStringSource()
    {
        OpenArchiveFunction fn = new();
        FunctionArgumentException ex = Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([DataKind.Int32]));
        Assert.Contains("source", ex.Message);
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArity()
    {
        OpenArchiveFunction fn = new();
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([DataKind.String, DataKind.String, DataKind.String]));
    }

    // ───────────────────────── ZIP round-trip ─────────────────────────

    [Fact]
    public async Task OpenArchive_OnZip_YieldsOneRowPerEntryWithBytesRoundTripping()
    {
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath,
            [
                ("a.txt", "alpha"u8.ToArray()),
                ("b.bin", new byte[] { 0x01, 0x02, 0x03, 0x04 }),
                ("nested/c.txt", "gamma"u8.ToArray()),
            ]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(zipPath)], ctx), ctx);

            Assert.Equal(3, rows.Count);
            Assert.Equal("a.txt", PathOf(rows[0]));
            Assert.Equal(5L, SizeOf(rows[0]));
            Assert.Equal("alpha"u8.ToArray(), PayloadOf(rows[0], ctx));

            Assert.Equal("b.bin", PathOf(rows[1]));
            Assert.Equal(4L, SizeOf(rows[1]));
            Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, PayloadOf(rows[1], ctx));

            Assert.Equal("nested/c.txt", PathOf(rows[2]));
            Assert.Equal("gamma"u8.ToArray(), PayloadOf(rows[2], ctx));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task OpenArchive_OnZip_PopulatesModifiedTimestamp()
    {
        string zipPath = TempPath(".zip");
        try
        {
            DateTimeOffset stamp = new(2025, 4, 1, 12, 30, 45, TimeSpan.Zero);
            BuildZip(zipPath, [("only.txt", "x"u8.ToArray())], lastWrite: stamp);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(zipPath)], ctx), ctx);

            Row row = Assert.Single(rows);
            Assert.False(row["modified"].IsNull);
            // ZIP entries store MS-DOS time in the local timezone (no UTC offset
            // recorded), so .NET's ZipArchive round-trips through the host TZ;
            // the absolute instant we get back is the local-noon clock face, not
            // the UTC noon we set. Verify the date round-trips and the value is
            // within a 24-hour TZ window of the original — enough to confirm
            // "the timestamp made it through" without being fragile to host TZ.
            DateTimeOffset got = row["modified"].AsTimestampTz();
            Assert.Equal(2025, got.Year);
            Assert.Equal(4, got.Month);
            Assert.InRange(Math.Abs((got - stamp).TotalHours), 0, 24);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task OpenArchive_OnZip_EmptyArchiveYieldsNoRows()
    {
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath, []);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(zipPath)], ctx), ctx);

            Assert.Empty(rows);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    // ───────────────────────── TAR round-trip ─────────────────────────

    [Fact]
    public async Task OpenArchive_OnTar_YieldsOneRowPerEntryWithBytesRoundTripping()
    {
        string tarPath = TempPath(".tar");
        try
        {
            BuildTar(tarPath,
            [
                ("first.txt", "ein"u8.ToArray()),
                ("second.txt", "zwei"u8.ToArray()),
            ]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(tarPath)], ctx), ctx);

            Assert.Equal(2, rows.Count);
            Assert.Equal("first.txt", PathOf(rows[0]));
            Assert.Equal("ein"u8.ToArray(), PayloadOf(rows[0], ctx));

            Assert.Equal("second.txt", PathOf(rows[1]));
            Assert.Equal("zwei"u8.ToArray(), PayloadOf(rows[1], ctx));
        }
        finally
        {
            File.Delete(tarPath);
        }
    }

    // ───────────────────────── path_pattern filter ─────────────────────────

    [Fact]
    public async Task OpenArchive_PathPattern_FiltersBeforeBodyRead()
    {
        // Three entries with mixed extensions; pattern '%.txt' should yield 2 rows.
        // The b.bin entry's body bytes are never read — verifiable by the row count
        // alone since the filter sits between iteration and body decompression.
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath,
            [
                ("a.txt", "alpha"u8.ToArray()),
                ("b.bin", new byte[] { 0xFF, 0xFE, 0xFD }),
                ("c.txt", "gamma"u8.ToArray()),
            ]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [
                        ValueRef.FromString(zipPath),
                        ValueRef.FromString("%.txt"),
                    ],
                    ctx), ctx);

            Assert.Equal(2, rows.Count);
            Assert.Equal("a.txt", PathOf(rows[0]));
            Assert.Equal("c.txt", PathOf(rows[1]));
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task OpenArchive_PathPatternDefault_MatchesEverything()
    {
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath,
            [
                ("only-one.txt", "x"u8.ToArray()),
                ("only-two.bin", new byte[] { 0xCA, 0xFE }),
            ]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            // No path_pattern arg — implicit default '%'.
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(zipPath)], ctx), ctx);

            Assert.Equal(2, rows.Count);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    [Fact]
    public async Task OpenArchive_PathPatternEmpty_MatchesNothing()
    {
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath, [("a.txt", "x"u8.ToArray())]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [
                        ValueRef.FromString(zipPath),
                        ValueRef.FromString(""),
                    ],
                    ctx), ctx);

            Assert.Empty(rows);
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    // ───────────────────────── Raw-scan contract ─────────────────────────

    [Fact]
    public async Task OpenArchive_DoesNotFilterOsMetadataEntries()
    {
        // Deliberate raw-scan contract: __MACOSX/ and .DS_Store entries are visible
        // and the caller filters via SQL if they want them dropped. (The homogeneous
        // media-bag pipeline filters at its own layer; the TVF doesn't.)
        string zipPath = TempPath(".zip");
        try
        {
            BuildZip(zipPath,
            [
                ("__MACOSX/foo", "junk"u8.ToArray()),
                (".DS_Store", "junk"u8.ToArray()),
                ("real.txt", "x"u8.ToArray()),
            ]);

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(zipPath)], ctx), ctx);

            Assert.Equal(3, rows.Count);
            Assert.Contains(rows, r => PathOf(r) == "__MACOSX/foo");
            Assert.Contains(rows, r => PathOf(r) == ".DS_Store");
            Assert.Contains(rows, r => PathOf(r) == "real.txt");
        }
        finally
        {
            File.Delete(zipPath);
        }
    }

    // ───────────────────────── Error paths ─────────────────────────

    [Fact]
    public async Task OpenArchive_UnknownFormat_ThrowsInvalidData()
    {
        string txtPath = TempPath(".txt");
        try
        {
            File.WriteAllText(txtPath, "this is not an archive");

            OpenArchiveFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
            {
                await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                    .ExecuteAsync([ValueRef.FromString(txtPath)], ctx))
                {
                }
            });
        }
        finally
        {
            File.Delete(txtPath);
        }
    }

    // ───────────────────────── Helpers ─────────────────────────

    private static string TempPath(string ext) =>
        Path.Combine(Path.GetTempPath(), $"open-archive-test-{Guid.NewGuid():N}{ext}");

    private static void BuildZip(
        string path,
        IReadOnlyList<(string name, byte[] bytes)> entries,
        DateTimeOffset? lastWrite = null)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using ZipArchive archive = new(fs, ZipArchiveMode.Create);
        foreach ((string name, byte[] bytes) in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
            if (lastWrite is { } stamp) entry.LastWriteTime = stamp;
            using Stream s = entry.Open();
            s.Write(bytes);
        }
    }

    private static void BuildTar(
        string path,
        IReadOnlyList<(string name, byte[] bytes)> entries)
    {
        using FileStream fs = new(path, FileMode.Create, FileAccess.Write);
        using TarWriter writer = new(fs, TarEntryFormat.Pax, leaveOpen: false);
        foreach ((string name, byte[] bytes) in entries)
        {
            PaxTarEntry entry = new(TarEntryType.RegularFile, name)
            {
                DataStream = new MemoryStream(bytes),
            };
            writer.WriteEntry(entry);
        }
    }
}
