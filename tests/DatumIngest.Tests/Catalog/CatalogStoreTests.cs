using DatumIngest.Catalog;

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

    private TableCatalog OpenCatalog() =>
        new TableCatalog(GetService<DatumIngest.Pooling.Pool>(), _catalogPath);

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
        string body = DatumIngest.Execution.QueryExplainer.FormatExpression(udf!.Body);
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
        first.Plan("CREATE FUNCTION outer_macro(s STRING) AS udf.inner_macro(s)");

        TableCatalog second = OpenCatalog();
        Assert.True(second.Udfs.TryGet("inner_macro", out _));
        Assert.True(second.Udfs.TryGet("outer_macro", out _));
        Assert.Equal(2, second.CatalogLoadReport!.LoadedUdfs);
    }

    // ───────────────────── Failure handling ─────────────────────

    [Fact]
    public void MalformedJson_ThrowsLoadException()
    {
        File.WriteAllText(_catalogPath, "this is not json {{");

        Assert.Throws<CatalogStoreLoadException>(() => OpenCatalog());
    }

    [Fact]
    public void NewerVersion_IsIgnoredWithWarning()
    {
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 999,
              "udfs": [{"name": "should_be_ignored", "parameters": [], "body": "1"}]
            }
            """);

        TableCatalog catalog = OpenCatalog();

        Assert.Empty(catalog.Udfs.Entries);
        Assert.NotNull(catalog.CatalogLoadReport);
        Assert.NotEmpty(catalog.CatalogLoadReport!.Warnings);
        Assert.Contains(catalog.CatalogLoadReport.Warnings,
            w => w.Contains("999", StringComparison.Ordinal));
    }

    [Fact]
    public void EntryWithUnparseableBody_IsSkipped_OthersLoad()
    {
        // The bad entry's body is not valid SQL — should be skipped, the
        // good entry should still load.
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 1,
              "udfs": [
                {
                  "name": "good",
                  "parameters": [{"name": "x", "type": "INT32"}],
                  "body": "x + 1"
                },
                {
                  "name": "bad",
                  "parameters": [],
                  "body": "this is not a valid expression !!!"
                }
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
    public void EntryWithMissingName_IsSkipped()
    {
        File.WriteAllText(_catalogPath,
            """
            {
              "version": 1,
              "udfs": [
                {"name": "", "parameters": [], "body": "1"},
                {"name": "good", "parameters": [], "body": "1"}
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
        TableCatalog catalog = new(GetService<DatumIngest.Pooling.Pool>());
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

        TableCatalog catalog = new(GetService<DatumIngest.Pooling.Pool>(), nestedPath);
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
}
