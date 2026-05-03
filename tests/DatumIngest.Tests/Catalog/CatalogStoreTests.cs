using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="CatalogStore"/> + <see cref="TableCatalog"/>'s
/// optional file persistence: round-trip of UDF DDL across catalog
/// instances, atomic save semantics, and graceful handling of missing,
/// malformed, or partially-corrupt catalog files.
/// </summary>
public class CatalogStoreTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public CatalogStoreTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-catalog-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_scratchDir))
            {
                Directory.Delete(_scratchDir, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; OS may still hold a handle on Windows.
        }
    }

    private TableCatalog OpenCatalog() => CreateCatalog(_catalogPath);

    // ───────────────────── Round-trip ─────────────────────

    [Fact]
    public void NoFile_OnConstruct_StartsEmpty()
    {
        Assert.False(File.Exists(_catalogPath));

        TableCatalog catalog = OpenCatalog();

        Assert.Empty(catalog.Udfs.Entries);
        Assert.NotNull(catalog.CatalogLoadReport);
        Assert.Equal(0, catalog.CatalogLoadReport!.LoadedUdfs);
        Assert.Equal(0, catalog.CatalogLoadReport.SkippedUdfs);
    }

    [Fact]
    public void CreateFunction_PersistsToFile()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");

        Assert.True(File.Exists(_catalogPath));
        string contents = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"shout\"", contents);
        Assert.Contains("\"version\":", contents);
    }

    [Fact]
    public void Reopen_LoadsPersistedUdfs()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");
        first.Plan("CREATE FUNCTION whisper(name STRING) AS lower(name)");

        TableCatalog second = OpenCatalog();

        Assert.Equal(2, second.Udfs.Entries.Count);
        Assert.True(second.Udfs.TryGet("shout", out _));
        Assert.True(second.Udfs.TryGet("whisper", out _));
        Assert.Equal(2, second.CatalogLoadReport!.LoadedUdfs);
    }

    [Fact]
    public void Reopen_PreservesParametersAndReturnType()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION sq(x INT32) RETURNS INT32 AS x * x");

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("sq", out UdfDescriptor? udf));
        Assert.Equal("sq", udf!.Name);
        Assert.Single(udf.Parameters);
        Assert.Equal("x", udf.Parameters[0].Name);
        Assert.Equal("INT32", udf.Parameters[0].TypeName, ignoreCase: true);
        Assert.Equal("INT32", udf.ReturnTypeName, ignoreCase: true);
    }

    [Fact]
    public void Reopen_PreservesParameterDefaults()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION add(a INT32, b INT32 = 5) AS a + b");

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("add", out UdfDescriptor? udf));
        Assert.Equal(2, udf!.Parameters.Count);
        Assert.Null(udf.Parameters[0].Default);
        Assert.NotNull(udf.Parameters[1].Default);

        // The reloaded default expression should round-trip into the same
        // formatted text — confirms the JSON path captured an evaluable form.
        string formatted = DatumIngest.Execution.QueryExplainer.FormatExpression(
            udf.Parameters[1].Default!);
        Assert.Equal("5", formatted);
    }

    [Fact]
    public void DropFunction_PersistsRemoval()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");
        first.Plan("CREATE FUNCTION whisper(name STRING) AS lower(name)");
        first.Plan("DROP FUNCTION shout");

        TableCatalog second = OpenCatalog();

        Assert.Single(second.Udfs.Entries);
        Assert.False(second.Udfs.TryGet("shout", out _));
        Assert.True(second.Udfs.TryGet("whisper", out _));
    }

    [Fact]
    public void OrReplace_PersistsNewBody()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");
        first.Plan("CREATE OR REPLACE FUNCTION shout(name STRING) AS lower(name)");

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("shout", out UdfDescriptor? udf));
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.ExpressionBody!);
        Assert.Contains("lower", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ChainedUdfs_RoundTrip_AlphabeticalOrder()
    {
        // A UDF whose body references another UDF: as long as the dependency
        // is loaded first (alphabetical order does this for "inner_macro"
        // before "outer_macro"), the dependent rehydrates cleanly.
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION inner_macro(s STRING) AS upper(s)");
        first.Plan("CREATE FUNCTION outer_macro(s STRING) AS inner_macro(s)");

        TableCatalog second = OpenCatalog();
        Assert.True(second.Udfs.TryGet("inner_macro", out _));
        Assert.True(second.Udfs.TryGet("outer_macro", out _));
        Assert.Equal(2, second.CatalogLoadReport!.LoadedUdfs);
    }

    // ───────────────────── Procedures ─────────────────────

    [Fact]
    public void CreateProcedure_PersistsToFile()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        Assert.True(File.Exists(_catalogPath));
        string manifest = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"noop\"", manifest);
        Assert.Contains("\"file_path\": \"procedures/public/noop.sql\"", manifest);

        string procPath = Path.Combine(_scratchDir, "procedures", "public", "noop.sql");
        Assert.True(File.Exists(procPath));
        string source = File.ReadAllText(procPath);
        Assert.StartsWith("CREATE OR REPLACE PROCEDURE", source);
        Assert.Contains("BEGIN", source);
    }

    [Fact]
    public void Reopen_LoadsPersistedProcedures()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE PROCEDURE foo(x INT32) AS BEGIN DECLARE y INT32 = x + 1 END");

        TableCatalog second = OpenCatalog();

        Assert.True(second.Procedures.TryGet("foo", out ProcedureDescriptor? proc));
        Assert.Equal("foo", proc!.Name);
        Assert.Single(proc.Parameters);
        Assert.Equal("x", proc.Parameters[0].Name);
        Assert.Equal(1, second.CatalogLoadReport!.LoadedProcedures);
    }

    [Fact]
    public void Reopen_PreservesProcedureBodyVerbatim()
    {
        // Save rewrites the leading `CREATE` to `CREATE OR REPLACE` so the
        // on-disk .sql is self-applying. Everything after that — body
        // formatting, comments, multi-line layout — round-trips unchanged.
        const string sql = "CREATE PROCEDURE multi_line(x INT32) AS BEGIN\n  SET x = x + 1\nEND";
        TableCatalog first = OpenCatalog();
        first.Plan(sql);

        TableCatalog second = OpenCatalog();

        Assert.True(second.Procedures.TryGet("multi_line", out ProcedureDescriptor? proc));
        Assert.Equal(
            "CREATE OR REPLACE PROCEDURE multi_line(x INT32) AS BEGIN\n  SET x = x + 1\nEND",
            proc!.SourceText);
    }

    [Fact]
    public void DropProcedure_PersistsRemoval()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE PROCEDURE keep() AS BEGIN SELECT 1 END");
        first.Plan("CREATE PROCEDURE remove() AS BEGIN SELECT 1 END");
        first.Plan("DROP PROCEDURE remove");

        TableCatalog second = OpenCatalog();

        Assert.Single(second.Procedures.Entries);
        Assert.True(second.Procedures.TryGet("keep", out _));
        Assert.False(second.Procedures.TryGet("remove", out _));
    }

    [Fact]
    public void Reopen_LoadsBothUdfsAndProcedures()
    {
        TableCatalog first = OpenCatalog();
        first.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        first.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        TableCatalog second = OpenCatalog();

        Assert.Single(second.Udfs.Entries);
        Assert.Single(second.Procedures.Entries);
        Assert.Equal(1, second.CatalogLoadReport!.LoadedUdfs);
        Assert.Equal(1, second.CatalogLoadReport.LoadedProcedures);
    }

    // ───────────────────── Procedural UDFs ─────────────────────

    [Fact]
    public void CreateProceduralUdf_PersistsToFile()
    {
        // Procedural UDFs persist the verbatim CREATE FUNCTION text in the
        // per-routine .sql file (with the leading CREATE rewritten to
        // CREATE OR REPLACE). The manifest just points at it.
        TableCatalog catalog = OpenCatalog();
        catalog.Plan(
            "CREATE FUNCTION sq(x INT32) RETURNS INT32 BEGIN RETURN x * x END");

        string manifest = File.ReadAllText(_catalogPath);
        Assert.Contains("\"name\": \"sq\"", manifest);
        Assert.Contains("\"file_path\": \"udfs/public/sq.sql\"", manifest);

        string udfPath = Path.Combine(_scratchDir, "udfs", "public", "sq.sql");
        string source = File.ReadAllText(udfPath);
        Assert.StartsWith("CREATE OR REPLACE FUNCTION", source);
        Assert.Contains("BEGIN RETURN x * x END", source);
    }

    [Fact]
    public void Reopen_ProceduralUdf_RehydratesStatementBody()
    {
        const string sql =
            "CREATE FUNCTION sq(x INT32) RETURNS INT32 BEGIN RETURN x * x END";
        TableCatalog first = OpenCatalog();
        first.Plan(sql);

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("sq", out UdfDescriptor? udf));
        Assert.True(udf!.IsProcedural);
        Assert.Null(udf.ExpressionBody);
        Assert.NotNull(udf.StatementBody);
        Assert.Single(udf.StatementBody);
        Assert.Equal("INT32", udf.ReturnTypeName, ignoreCase: true);
        // SourceText round-trips with the leading `CREATE` rewritten to
        // `CREATE OR REPLACE` so the file is self-applying.
        Assert.Equal(
            "CREATE OR REPLACE FUNCTION sq(x INT32) RETURNS INT32 BEGIN RETURN x * x END",
            udf.SourceText);
        Assert.False(udf.IsPure);
    }

    [Fact]
    public void Reopen_PureProceduralUdf_PreservesIsPureFlag()
    {
        // PURE is the assertion the planner's CSE pass keys on; it must
        // survive a catalog round-trip independently of the body bytes.
        TableCatalog first = OpenCatalog();
        first.Plan(
            "CREATE PURE FUNCTION sq(x INT32) RETURNS INT32 BEGIN RETURN x * x END");

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("sq", out UdfDescriptor? udf));
        Assert.True(udf!.IsPure);
    }

    [Fact]
    public void Reopen_ProceduralUdf_PreservesBodyFormattingVerbatim()
    {
        // The user's multi-line body formatting round-trips byte-for-byte;
        // the only rewrite Save makes is the leading `CREATE` →
        // `CREATE OR REPLACE` so the file is self-applying.
        const string sql =
            "CREATE FUNCTION pipeline(x INT32) RETURNS INT32 BEGIN\n" +
            "  DECLARE y INT32 = x + 1;\n" +
            "  RETURN y * 2\n" +
            "END";
        TableCatalog first = OpenCatalog();
        first.Plan(sql);

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("pipeline", out UdfDescriptor? udf));
        Assert.Equal(
            "CREATE OR REPLACE FUNCTION pipeline(x INT32) RETURNS INT32 BEGIN\n" +
            "  DECLARE y INT32 = x + 1;\n" +
            "  RETURN y * 2\n" +
            "END",
            udf!.SourceText);
    }

    [Fact]
    public async Task Reopen_ProceduralUdfRegisteredViaBatchExecutor_RoundTripsBody()
    {
        // Regression test: the batch executor path
        // parses via ParseBatchWithText and passes per-statement source
        // slices through BatchExecutor → Plan(Statement, sourceText) so
        // the catalog file captures the body verbatim. Without the slice,
        // the descriptor falls back to the synthesised "CREATE FUNCTION
        // <name>" placeholder which fails to reparse on reopen and the
        // function disappears.
        const string sql =
            "CREATE OR ALTER FUNCTION RewriteCaption(caption STRING)\n" +
            "RETURNS STRING\n" +
            "BEGIN\n" +
            "  DECLARE prefix STRING = 'rewrite: ';\n" +
            "  RETURN concat(prefix, caption)\n" +
            "END";

        TableCatalog first = OpenCatalog();
        IReadOnlyList<(Statement Statement, string SourceText)> pairs =
            SqlParser.ParseBatchWithText(sql);
        (Statement, string?)[] nullablePairs = new (Statement, string?)[pairs.Count];
        for (int i = 0; i < pairs.Count; i++)
            nullablePairs[i] = (pairs[i].Statement, pairs[i].SourceText);
        BatchExecutor executor = new(first);
        await executor.ExecuteAsync(nullablePairs, CancellationToken.None);

        TableCatalog second = OpenCatalog();

        Assert.True(second.Udfs.TryGet("RewriteCaption", out UdfDescriptor? udf));
        Assert.True(udf!.IsProcedural);
        Assert.Equal(sql, udf.SourceText);
        Assert.NotNull(udf.StatementBody);
        // 2 DECLAREs + 1 RETURN — proves the body actually rehydrated, not just the source.
        Assert.Equal(2, udf.StatementBody.Count);
    }

    [Fact]
    public void HandWrittenManifest_WithFilePath_LoadsFromDisk()
    {
        // A hand-edited (or version-controlled) catalog with the manifest +
        // sibling .sql file pair loads cleanly. This is the canonical v7
        // round-trip shape — the file is the source of truth, the manifest
        // is the registry.
        string udfDir = Path.Combine(_scratchDir, "udfs", "public");
        Directory.CreateDirectory(udfDir);
        File.WriteAllText(Path.Combine(udfDir, "shout.sql"),
            "CREATE OR REPLACE FUNCTION shout(s STRING) AS upper(s)");
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 7,
              "udfs": [
                {"schema": "public", "name": "shout", "file_path": "udfs/public/shout.sql"}
              ]
            }
            """);

        TableCatalog catalog = OpenCatalog();

        Assert.True(catalog.Udfs.TryGet("shout", out UdfDescriptor? udf));
        Assert.False(udf!.IsProcedural);
        Assert.NotNull(udf.ExpressionBody);
        Assert.Null(udf.StatementBody);
    }

    // ───────────────────── Failure handling ─────────────────────

    [Fact]
    public void MalformedJson_ThrowsLoadException()
    {
        File.WriteAllText(_catalogPath, "this is not json {{");

        Assert.Throws<CatalogStoreLoadException>(() => OpenCatalog());
    }

    [Fact]
    public void NewerVersion_ThrowsLoadException()
    {
        // v3 onwards, mismatched manifest versions are a hard fail so a
        // newer-version writer can't silently lose state when read by an
        // older binary. The exception message tells the user to start fresh.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 999,
              "udfs": [{"name": "should_be_ignored", "parameters": [], "body": "1"}]
            }
            """);

        CatalogStoreLoadException ex = Assert.Throws<CatalogStoreLoadException>(() => OpenCatalog());
        Assert.Contains("999", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Delete the catalog directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderVersion_ThrowsLoadException()
    {
        // No v1/v2/v3 reader — schemas require v4 (post-S7c). Older
        // manifests are explicitly rejected; the user is expected to
        // delete the catalog directory and start fresh.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 1,
              "udfs": [{"name": "old", "parameters": [], "body": "1"}]
            }
            """);

        CatalogStoreLoadException ex = Assert.Throws<CatalogStoreLoadException>(() => OpenCatalog());
        Assert.Contains("version 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FlatFileBackendState_RoundTripsAcrossReopen()
    {
        // Exercises the v3 backends.flat_file.tables shape end-to-end:
        // CREATE TABLE → save → reopen → table is rehydrated with its
        // schema, indexes, and constraint name intact.
        string firstSchema;
        using (TableCatalog first = CreateCatalog(_catalogPath))
        {
            first.Plan("CREATE TABLE products (sku Int32 CONSTRAINT pk_products PRIMARY KEY, name String)");
            first.Plan("CREATE INDEX idx_products_name ON products (name)");
            firstSchema = string.Join(",",
                first["products"].GetSchema().Columns.Select(c => c.Name));
        }

        using TableCatalog reopened = CreateCatalog(_catalogPath);

        // Table came back.
        Assert.True(reopened.HasTable("products"));
        Assert.True(reopened.HasTable("public.products"));
        Assert.Equal(firstSchema,
            string.Join(",", reopened["products"].GetSchema().Columns.Select(c => c.Name)));

        // Index entry rehydrated under the same schema → same composite-index
        // sidecar gets reopened.
        IReadOnlyList<IndexDescriptor>? indexes = reopened.GetTableIndexes("products");
        Assert.NotNull(indexes);
        Assert.Single(indexes!);
        Assert.Equal("idx_products_name", indexes![0].Name);

        // Custom PK constraint name survived the round-trip.
        Assert.Equal("pk_products", reopened.GetPrimaryKeyConstraintName("products"));
    }

    [Fact]
    public void EntryWithUnparseableBody_IsSkipped_OthersLoad()
    {
        // The bad entry's .sql file contains invalid SQL — should be skipped
        // with a warning, the good entry should still load.
        string udfDir = Path.Combine(_scratchDir, "udfs", "public");
        Directory.CreateDirectory(udfDir);
        File.WriteAllText(Path.Combine(udfDir, "good.sql"),
            "CREATE OR REPLACE FUNCTION good(x INT32) AS x + 1");
        File.WriteAllText(Path.Combine(udfDir, "bad.sql"),
            "this is not a valid CREATE FUNCTION statement");

        File.WriteAllText(_catalogPath,
            """
            {
              "version": 7,
              "udfs": [
                {"schema": "public", "name": "good", "file_path": "udfs/public/good.sql"},
                {"schema": "public", "name": "bad", "file_path": "udfs/public/bad.sql"}
              ]
            }
            """);

        TableCatalog catalog = OpenCatalog();

        Assert.True(catalog.Udfs.TryGet("good", out _));
        Assert.False(catalog.Udfs.TryGet("bad", out _));
        Assert.Equal(1, catalog.CatalogLoadReport!.LoadedUdfs);
        Assert.Equal(1, catalog.CatalogLoadReport.SkippedUdfs);
        Assert.Contains(catalog.CatalogLoadReport.Warnings,
            w => w.Contains("bad", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EntryWithMissingFile_IsSkipped()
    {
        // file_path points at a .sql that doesn't exist — load skips with a
        // warning rather than aborting the whole catalog.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 7,
              "udfs": [
                {"schema": "public", "name": "ghost", "file_path": "udfs/public/ghost.sql"}
              ]
            }
            """);

        TableCatalog catalog = OpenCatalog();

        Assert.False(catalog.Udfs.TryGet("ghost", out _));
        Assert.Equal(0, catalog.CatalogLoadReport!.LoadedUdfs);
        Assert.Equal(1, catalog.CatalogLoadReport.SkippedUdfs);
        Assert.Contains(catalog.CatalogLoadReport.Warnings,
            w => w.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void EntryWithMissingName_IsSkipped()
    {
        string udfDir = Path.Combine(_scratchDir, "udfs", "public");
        Directory.CreateDirectory(udfDir);
        File.WriteAllText(Path.Combine(udfDir, "good.sql"),
            "CREATE OR REPLACE FUNCTION good() AS 1");

        File.WriteAllText(_catalogPath,
            """
            {
              "version": 7,
              "udfs": [
                {"schema": "public", "name": "", "file_path": "udfs/public/good.sql"},
                {"schema": "public", "name": "good", "file_path": "udfs/public/good.sql"}
              ]
            }
            """);

        TableCatalog catalog = OpenCatalog();

        Assert.True(catalog.Udfs.TryGet("good", out _));
        Assert.Equal(1, catalog.CatalogLoadReport!.LoadedUdfs);
        Assert.Equal(1, catalog.CatalogLoadReport.SkippedUdfs);
    }

    [Fact]
    public void NoCatalogPath_DoesNotPersist()
    {
        // Default constructor (no path) → in-memory only. No file should
        // be created when the catalog path is null.
        TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");

        Assert.False(File.Exists(_catalogPath));
        Assert.Null(catalog.CatalogLoadReport);
    }

    [Fact]
    public void CatalogDirectory_CreatedOnFirstSave()
    {
        // Use a path under a directory that doesn't exist yet. Save should
        // create it on first write.
        string nestedDir = Path.Combine(_scratchDir, "deep", "nested", "path");
        string nestedPath = Path.Combine(nestedDir, CatalogStore.DefaultFileName);
        Assert.False(Directory.Exists(nestedDir));

        TableCatalog catalog = CreateCatalog(nestedPath);
        catalog.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");

        Assert.True(File.Exists(nestedPath));
    }

    // ───────────────────── Atomic write ─────────────────────

    [Fact]
    public void Save_DoesNotLeaveTempFile()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE FUNCTION shout(name STRING) AS upper(name)");

        string tempPath = _catalogPath + ".tmp";
        Assert.False(File.Exists(tempPath));
        Assert.True(File.Exists(_catalogPath));
    }

    [Fact]
    public void RepeatedSaves_OverwriteCleanly()
    {
        TableCatalog catalog = OpenCatalog();
        for (int i = 0; i < 5; i++)
        {
            catalog.Plan($"CREATE FUNCTION fn_{i}(x INT32) AS x + {i}");
        }

        string contents = File.ReadAllText(_catalogPath);
        for (int i = 0; i < 5; i++)
        {
            Assert.Contains($"\"fn_{i}\"", contents);
        }
        // Last write should NOT contain the .tmp residue.
        Assert.False(File.Exists(_catalogPath + ".tmp"));
    }

    // ───────────────────── On-disk layout ─────────────────────

    [Fact]
    public void CreateFunction_WritesSqlFileUnderUdfsDir()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        string udfPath = Path.Combine(_scratchDir, "udfs", "public", "shout.sql");
        Assert.True(File.Exists(udfPath));
        string source = File.ReadAllText(udfPath);
        Assert.StartsWith("CREATE OR REPLACE FUNCTION", source);
    }

    [Fact]
    public void CreateOrReplace_PreservedVerbatim()
    {
        // If the user wrote CREATE OR REPLACE explicitly, Save should leave
        // it alone — the regex only rewrites plain CREATE.
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE OR REPLACE FUNCTION shout(s STRING) AS upper(s)");

        string udfPath = Path.Combine(_scratchDir, "udfs", "public", "shout.sql");
        Assert.Equal(
            "CREATE OR REPLACE FUNCTION shout(s STRING) AS upper(s)",
            File.ReadAllText(udfPath));
    }

    [Fact]
    public void DropFunction_RemovesSqlFile()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");

        string udfPath = Path.Combine(_scratchDir, "udfs", "public", "shout.sql");
        Assert.True(File.Exists(udfPath));

        catalog.Plan("DROP FUNCTION shout");

        Assert.False(File.Exists(udfPath));
        // The schema directory was the last occupant — should be pruned too.
        Assert.False(Directory.Exists(Path.Combine(_scratchDir, "udfs", "public")));
    }

    [Fact]
    public void DropProcedure_RemovesSqlFile()
    {
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE PROCEDURE noop() AS BEGIN SELECT 1 END");

        string procPath = Path.Combine(_scratchDir, "procedures", "public", "noop.sql");
        Assert.True(File.Exists(procPath));

        catalog.Plan("DROP PROCEDURE noop");

        Assert.False(File.Exists(procPath));
    }

    [Fact]
    public void OrphanSqlFile_ReconciledOnNextSave()
    {
        // A .sql file that's not in the registry — e.g. left behind by a
        // hand-edit or a crash mid-rename — gets cleaned up on the next
        // Save so the disk state matches the registry exactly.
        TableCatalog catalog = OpenCatalog();
        catalog.Plan("CREATE FUNCTION known(x INT32) AS x + 1");

        string orphan = Path.Combine(_scratchDir, "udfs", "public", "ghost.sql");
        File.WriteAllText(orphan, "CREATE FUNCTION ghost(x INT32) AS x");
        Assert.True(File.Exists(orphan));

        // Trigger another Save by performing any catalog mutation.
        catalog.Plan("CREATE FUNCTION other(x INT32) AS x * 2");

        Assert.False(File.Exists(orphan));
    }
}
