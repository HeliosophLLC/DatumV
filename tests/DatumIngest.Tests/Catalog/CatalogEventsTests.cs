using DatumIngest.Catalog;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Smoke tests for the <see cref="CatalogEvents"/> bus. Verifies that each
/// wired DDL statement raises exactly one event from the corresponding typed
/// channel, with the right payload kind (Created vs Altered for the
/// register-style verbs, Dropped for the unregister-style ones).
/// </summary>
/// <remarks>
/// These tests don't probe the LSP manifest or any subscriber side-effects —
/// they just confirm the wiring. Subscriber-specific behaviour (e.g.
/// LanguageManifestService applying a function-added delta) lives in those
/// subscribers' own test files when they land.
/// </remarks>
public sealed class CatalogEventsTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public CatalogEventsTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-catalog-events-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try
        {
            if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true);
        }
        catch { /* best-effort */ }
    }

    [Fact]
    public async Task CreateFunction_FiresFunctionCreated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        FunctionCreatedEvent? captured = null;
        catalog.Events.FunctionCreated += e => captured = e;

        await catalog.ExecuteStatementAsync("CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        Assert.NotNull(captured);
        Assert.Equal("public", captured!.Name.Schema);
        Assert.Equal("dbl", captured.Name.Name);
        Assert.NotNull(captured.After);
    }

    [Fact]
    public async Task CreateOrReplaceFunction_FiresFunctionAltered_WhenPreviousExists()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        FunctionAlteredEvent? captured = null;
        catalog.Events.FunctionAltered += e => captured = e;

        await catalog.ExecuteStatementAsync("CREATE OR REPLACE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x + x");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
        Assert.NotNull(captured.After);
    }

    [Fact]
    public async Task DropFunction_FiresFunctionDropped()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        FunctionDroppedEvent? captured = null;
        catalog.Events.FunctionDropped += e => captured = e;

        await catalog.ExecuteStatementAsync("DROP FUNCTION public.dbl");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
        Assert.Equal("dbl", captured.Name.Name);
    }

    [Fact]
    public async Task CreateProcedure_FiresProcedureCreated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        ProcedureCreatedEvent? captured = null;
        catalog.Events.ProcedureCreated += e => captured = e;

        await catalog.ExecuteStatementAsync("CREATE PROCEDURE public.noop() AS BEGIN SELECT 1 END");

        Assert.NotNull(captured);
        Assert.Equal("noop", captured!.Name.Name);
    }

    [Fact]
    public async Task DropProcedure_FiresProcedureDropped()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE PROCEDURE public.noop() AS BEGIN SELECT 1 END");

        ProcedureDroppedEvent? captured = null;
        catalog.Events.ProcedureDropped += e => captured = e;

        await catalog.ExecuteStatementAsync("DROP PROCEDURE public.noop");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
    }

    [Fact]
    public async Task CreateSchema_FiresSchemaCreated()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        SchemaCreatedEvent? captured = null;
        catalog.Events.SchemaCreated += e => captured = e;

        await catalog.ExecuteStatementAsync("CREATE SCHEMA myapp");

        Assert.NotNull(captured);
        Assert.Equal("myapp", captured!.SchemaName);
    }

    [Fact]
    public async Task DropSchema_FiresSchemaDropped()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE SCHEMA myapp");

        SchemaDroppedEvent? captured = null;
        catalog.Events.SchemaDropped += e => captured = e;

        await catalog.ExecuteStatementAsync("DROP SCHEMA myapp");

        Assert.NotNull(captured);
        Assert.Equal("myapp", captured!.SchemaName);
    }

    [Fact]
    public async Task CreateTable_FiresTableCreated_WithSchemaPayload()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        TableCreatedEvent? captured = null;
        catalog.Events.TableCreated += e => captured = e;

        await catalog.ExecuteStatementAsync("CREATE TABLE public.things (id INT32 NOT NULL, name STRING)");

        Assert.NotNull(captured);
        Assert.Equal("things", captured!.Name.Name);
        Assert.Equal(2, captured.After.Columns.Count);
        Assert.Equal("id", captured.After.Columns[0].Name);
    }

    [Fact]
    public async Task DropTable_FiresTableDropped_WithBeforeSchema()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE TABLE public.things (id INT32 NOT NULL, name STRING)");

        TableDroppedEvent? captured = null;
        catalog.Events.TableDropped += e => captured = e;

        await catalog.ExecuteStatementAsync("DROP TABLE public.things");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
        Assert.Equal(2, captured.Before!.Columns.Count);
    }

    [Fact]
    public async Task AlterTableAddColumn_FiresTableAltered()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE TABLE public.things (id INT32 NOT NULL)");

        TableAlteredEvent? captured = null;
        catalog.Events.TableAltered += e => captured = e;

        await catalog.ExecuteStatementAsync("ALTER TABLE public.things ADD COLUMN name STRING");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
        Assert.Single(captured.Before!.Columns);
        Assert.Equal(2, captured.After.Columns.Count);
    }

    [Fact]
    public async Task IfNotExistsCreateFunction_DoesNotFire_WhenAlreadyExists()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        await catalog.ExecuteStatementAsync("CREATE FUNCTION public.dbl(x INT32) RETURNS INT32 AS x * 2");

        bool fired = false;
        catalog.Events.FunctionCreated += _ => fired = true;
        catalog.Events.FunctionAltered += _ => fired = true;

        await catalog.ExecuteStatementAsync("CREATE FUNCTION IF NOT EXISTS public.dbl(x INT32) RETURNS INT32 AS x * 99");

        Assert.False(fired, "IF NOT EXISTS hit on an existing function should not raise any event.");
    }
}
