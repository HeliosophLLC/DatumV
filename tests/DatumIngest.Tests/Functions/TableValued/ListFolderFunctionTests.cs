using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>list_folder(source [, recursion_depth [, path_pattern]])</c> table-valued
/// function: the no-body counterpart to <c>open_folder</c>. Covers schema,
/// recursion-depth and path-pattern parity with <c>open_folder</c>, and the
/// key behavioural difference — locked files still appear in the row stream
/// because no file body is opened.
/// </summary>
public sealed class ListFolderFunctionTests : ServiceTestBase
{
    private static async Task<List<Row>> CollectAsync(IAsyncEnumerable<RowBatch> batches)
    {
        List<Row> rows = [];
        await foreach (RowBatch batch in batches)
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add(batch[i]);
            }
        }
        return rows;
    }

    private static string PathOf(Row row) => row["path"].AsString();
    private static long SizeOf(Row row) => row["size"].AsInt64();

    // ───────────────────────── Schema ─────────────────────────

    [Fact]
    public void ValidateArguments_DeclaresPathSizeModifiedSchema_NoBytes()
    {
        ListFolderFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("size", schema.Columns[1].Name);
        Assert.Equal(DataKind.Int64, schema.Columns[1].Kind);
        Assert.Equal("modified", schema.Columns[2].Name);
        Assert.Equal(DataKind.TimestampTz, schema.Columns[2].Kind);
        Assert.True(schema.Columns[2].Nullable);

        // Specifically NOT a bytes column — the whole point of list_folder.
        Assert.DoesNotContain(schema.Columns, c => c.Name == "bytes");
    }

    [Fact]
    public void ValidateArguments_RejectsWrongArity()
    {
        ListFolderFunction fn = new();
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments(
                [DataKind.String, DataKind.Int32, DataKind.String, DataKind.String]));
    }

    // ───────────────────────── Walk parity with open_folder ─────────────────────────

    [Fact]
    public async Task ListFolder_DefaultDepth_ReturnsOnlyTopLevelFiles()
    {
        string root = CreateTempTree();
        try
        {
            ListFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(root)], ctx));

            Row only = Assert.Single(rows);
            Assert.Equal("top.txt", PathOf(only));
            Assert.Equal(8L, SizeOf(only)); // "toplevel" = 8 bytes
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ListFolder_UnlimitedDepth_WalksEntireSubtreeAndReportsSizes()
    {
        string root = CreateTempTree();
        try
        {
            ListFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(root), ValueRef.FromInt64(-1)], ctx));

            Assert.Equal(4, rows.Count);
            Dictionary<string, long> sizes = rows.ToDictionary(PathOf, SizeOf);
            Assert.Equal(8, sizes["top.txt"]);          // "toplevel"
            Assert.Equal(8, sizes["sub/mid.txt"]);      // "midlevel"
            Assert.Equal(5, sizes["deeper/d1.txt"]);    // "deep1"
            Assert.Equal(5, sizes["deeper/deep/d2.txt"]); // "deep2"
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ListFolder_PathPattern_FiltersOnRelativePath()
    {
        string root = CreateTempTree();
        try
        {
            ListFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [
                        ValueRef.FromString(root),
                        ValueRef.FromInt64(-1),
                        ValueRef.FromString("deeper/%"),
                    ],
                    ctx));

            Assert.Equal(2, rows.Count);
            HashSet<string> paths = [.. rows.Select(PathOf)];
            Assert.Contains("deeper/d1.txt", paths);
            Assert.Contains("deeper/deep/d2.txt", paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ListFolder_NegativeDepthOutOfRange_Throws()
    {
        string root = CreateTempTree();
        try
        {
            ListFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            {
                await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                    .ExecuteAsync([ValueRef.FromString(root), ValueRef.FromInt64(-7)], ctx))
                {
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ───────────────────────── Key difference from open_folder ─────────────────────────

    [Fact]
    public async Task ListFolder_LockedFile_StillAppearsInRowStream()
    {
        // The whole reason to reach for list_folder over open_folder: locked files
        // (DumpStack.log.tmp, pagefile.sys, in-flight DB files) are listed with
        // their size and mtime because no file body is opened. open_folder skips
        // them silently; list_folder shows them.
        string root = Path.Combine(Path.GetTempPath(), $"list-folder-locked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string readable = Path.Combine(root, "readable.txt");
            string locked = Path.Combine(root, "locked.txt");
            File.WriteAllBytes(readable, "ok"u8.ToArray());
            File.WriteAllBytes(locked, "secret bytes"u8.ToArray());

            using FileStream exclusiveLock = new(
                locked, FileMode.Open, FileAccess.Read, FileShare.None);

            ListFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(root)], ctx));

            Assert.Equal(2, rows.Count);
            Dictionary<string, long> sizes = rows.ToDictionary(PathOf, SizeOf);
            Assert.Equal(2, sizes["readable.txt"]);
            Assert.Equal(12, sizes["locked.txt"]);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ───────────────────────── Error paths ─────────────────────────

    [Fact]
    public async Task ListFolder_NonexistentSource_Throws()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"list-folder-nonexistent-{Guid.NewGuid():N}");

        ListFolderFunction fn = new();
        ExecutionContext ctx = CreateExecutionContext();
        await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
        {
            await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                .ExecuteAsync([ValueRef.FromString(fake)], ctx))
            {
            }
        });
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Same tree shape as OpenFolderFunctionTests so the walk-semantics tests
    /// stay symmetric between the two functions.
    /// </summary>
    private static string CreateTempTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"list-folder-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "deeper"));
        Directory.CreateDirectory(Path.Combine(root, "deeper", "deep"));

        File.WriteAllBytes(Path.Combine(root, "top.txt"), "toplevel"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "sub", "mid.txt"), "midlevel"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "deeper", "d1.txt"), "deep1"u8.ToArray());
        File.WriteAllBytes(Path.Combine(root, "deeper", "deep", "d2.txt"), "deep2"u8.ToArray());

        return root;
    }
}
