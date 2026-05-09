using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.TableValued;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Tests.Functions.TableValued;

/// <summary>
/// <c>open_folder(source [, recursion_depth [, path_pattern]])</c> table-valued
/// function: walks a filesystem directory and yields one row per regular
/// file. Covers schema parity with <c>open_archive</c>, depth semantics
/// (0 / N / -1), the path-pattern filter, and the directory-doesn't-exist
/// error path.
/// </summary>
public sealed class OpenFolderFunctionTests : ServiceTestBase
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

    private static byte[] PayloadOf(Row row, ExecutionContext ctx)
        => row["bytes"].AsByteSpan(ctx.Store, registry: null).ToArray();

    private static string PathOf(Row row) => row["path"].AsString();

    // ───────────────────────── Schema ─────────────────────────

    [Fact]
    public void ValidateArguments_MatchesOpenArchiveSchema()
    {
        OpenFolderFunction fn = new();
        Schema schema = ((ITableValuedFunction)fn).ValidateArguments([DataKind.String]);

        Assert.Equal(4, schema.Columns.Count);
        Assert.Equal("path", schema.Columns[0].Name);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
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
    public void ValidateArguments_RejectsWrongArity()
    {
        OpenFolderFunction fn = new();
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments([]));
        Assert.Throws<FunctionArgumentException>(
            () => ((ITableValuedFunction)fn).ValidateArguments(
                [DataKind.String, DataKind.Int32, DataKind.String, DataKind.String]));
    }

    // ───────────────────────── Recursion depth ─────────────────────────

    [Fact]
    public async Task OpenFolder_DefaultDepth_ReturnsOnlyTopLevelFiles()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(root)], ctx));

            // Tree at /: top.txt + sub/ + deeper/. With depth=0 we should only see top.txt.
            Assert.Single(rows);
            Assert.Equal("top.txt", PathOf(rows[0]));
            Assert.Equal("toplevel"u8.ToArray(), PayloadOf(rows[0], ctx));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenFolder_DepthOne_IncludesOneLevelOfSubdirectories()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(root), ValueRef.FromInt64(1)], ctx));

            // depth=1 includes top.txt + sub/mid.txt + deeper/d1.txt (3 files total).
            // It does NOT include deeper/deep/d2.txt (that's depth 2).
            Assert.Equal(3, rows.Count);
            HashSet<string> paths = [.. rows.Select(PathOf)];
            Assert.Contains("top.txt", paths);
            Assert.Contains("sub/mid.txt", paths);
            Assert.Contains("deeper/d1.txt", paths);
            Assert.DoesNotContain("deeper/deep/d2.txt", paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenFolder_UnlimitedDepth_WalksEntireSubtree()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(root), ValueRef.FromInt64(-1)], ctx));

            // Unlimited: top.txt + sub/mid.txt + deeper/d1.txt + deeper/deep/d2.txt.
            Assert.Equal(4, rows.Count);
            HashSet<string> paths = [.. rows.Select(PathOf)];
            Assert.Contains("top.txt", paths);
            Assert.Contains("sub/mid.txt", paths);
            Assert.Contains("deeper/d1.txt", paths);
            Assert.Contains("deeper/deep/d2.txt", paths);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenFolder_NegativeDepthOutOfRange_Throws()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
            {
                await foreach (RowBatch _ in ((ITableValuedFunction)fn)
                    .ExecuteAsync(
                        [ValueRef.FromString(root), ValueRef.FromInt64(-2)],
                        ctx))
                {
                }
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ───────────────────────── path_pattern filter ─────────────────────────

    [Fact]
    public async Task OpenFolder_PathPattern_FiltersBeforeReadingBytes()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            // Unlimited depth, only *.txt files matching deep/d2.
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [
                        ValueRef.FromString(root),
                        ValueRef.FromInt64(-1),
                        ValueRef.FromString("deeper/deep/%"),
                    ],
                    ctx));

            Assert.Single(rows);
            Assert.Equal("deeper/deep/d2.txt", PathOf(rows[0]));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ───────────────────────── Path semantics ─────────────────────────

    [Fact]
    public async Task OpenFolder_PathColumn_IsForwardSlashedRelativeToSource()
    {
        string root = CreateTempTree();
        try
        {
            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync(
                    [ValueRef.FromString(root), ValueRef.FromInt64(1)], ctx));

            // None of the paths should contain a backslash or start with the root.
            foreach (Row row in rows)
            {
                string path = PathOf(row);
                Assert.DoesNotContain('\\', path);
                Assert.False(Path.IsPathRooted(path),
                    $"path '{path}' should be relative to source, not absolute");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    // ───────────────────────── Error paths ─────────────────────────

    [Fact]
    public async Task OpenFolder_LockedFileInDirectory_IsSilentlySkippedRatherThanAbortingWalk()
    {
        // Simulate the Windows-kernel-locked-file case (e.g. DumpStack.log.tmp,
        // pagefile.sys) by holding a file with FileShare.None inside an otherwise
        // walkable directory. open_folder should yield the readable files and
        // skip the locked one rather than throwing an IOException that aborts
        // the whole walk.
        string root = Path.Combine(Path.GetTempPath(), $"open-folder-locked-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            string readable = Path.Combine(root, "readable.txt");
            string locked = Path.Combine(root, "locked.txt");
            File.WriteAllBytes(readable, "ok"u8.ToArray());
            File.WriteAllBytes(locked, "secret"u8.ToArray());

            using FileStream exclusiveLock = new(
                locked, FileMode.Open, FileAccess.Read, FileShare.None);

            OpenFolderFunction fn = new();
            ExecutionContext ctx = CreateExecutionContext();
            List<Row> rows = await CollectAsync(
                ((ITableValuedFunction)fn).ExecuteAsync([ValueRef.FromString(root)], ctx));

            // readable.txt is the only row; locked.txt was silently skipped.
            Row only = Assert.Single(rows);
            Assert.Equal("readable.txt", PathOf(only));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OpenFolder_NonexistentSource_Throws()
    {
        string fake = Path.Combine(Path.GetTempPath(), $"open-folder-nonexistent-{Guid.NewGuid():N}");

        OpenFolderFunction fn = new();
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
    /// Builds a small directory tree for the recursion tests:
    /// <code>
    /// root/
    ///   top.txt
    ///   sub/
    ///     mid.txt
    ///   deeper/
    ///     d1.txt
    ///     deep/
    ///       d2.txt
    /// </code>
    /// </summary>
    private static string CreateTempTree()
    {
        string root = Path.Combine(Path.GetTempPath(), $"open-folder-test-{Guid.NewGuid():N}");
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
