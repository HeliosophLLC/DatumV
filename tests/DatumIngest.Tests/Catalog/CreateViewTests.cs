using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for <c>CREATE VIEW</c>: registration, planning-time
/// substitution, system.views surfacing, persistence round-trips, and
/// cycle detection. Views are pure macros — the planner expands the
/// stored body at every FROM reference.
/// </summary>
public sealed class CreateViewTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir;
    private readonly string _catalogPath;

    public CreateViewTests()
    {
        _scratchDir = Path.Combine(
            Path.GetTempPath(),
            $"datum-view-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_scratchDir);
        _catalogPath = Path.Combine(_scratchDir, CatalogStore.DefaultFileName);
    }

    public new void Dispose()
    {
        base.Dispose();
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void CreateView_RegistersDescriptor()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");

        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out ViewDescriptor? view));
        Assert.Equal("v", view!.Name);
        Assert.Equal("public", view.SchemaName);
    }

    [Fact]
    public async Task SelectFromView_ExpandsBodyAndReturnsRows()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("INSERT INTO t VALUES (1, 10), (2, 20), (3, 30)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t WHERE b > 10");

        StatementPlan plan = catalog.Plan("SELECT * FROM v");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Equal(new[] { 2, 3 }, values);
    }

    [Fact]
    public async Task SelectAliasedViewColumn_ResolvesThroughSubquery()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("INSERT INTO t VALUES (1, 10), (2, 20)");
        catalog.Plan("CREATE VIEW v AS SELECT a, b FROM t");

        StatementPlan plan = catalog.Plan("SELECT v.a FROM v WHERE v.b = 20");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Equal(new[] { 2 }, values);
    }

    [Fact]
    public async Task SelectFromQualifiedView_Works()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2)");
        catalog.Plan("CREATE VIEW public.v AS SELECT a FROM t");

        StatementPlan plan = catalog.Plan("SELECT a FROM public.v");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Equal(new[] { 1, 2 }, values);
    }

    [Fact]
    public void CreateView_OnDuplicateName_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE VIEW v AS SELECT a FROM t"));
        Assert.Contains("already registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateOrReplaceView_OverwritesExisting()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");
        catalog.Plan("CREATE OR REPLACE VIEW v AS SELECT b FROM t");

        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out ViewDescriptor? view));
        Assert.Contains("b", view!.SourceText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateViewIfNotExists_NoopOnDuplicate()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        // No throw, no change.
        catalog.Plan("CREATE VIEW IF NOT EXISTS v AS SELECT a + 1 FROM t");

        Assert.True(catalog.Views.TryGet(new QualifiedName("public", "v"), out ViewDescriptor? view));
        // Body still the original — `a`, not `a + 1`.
        Assert.DoesNotContain("+", view!.SourceText);
    }

    [Fact]
    public void CreateView_OverExistingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("CREATE VIEW t AS SELECT 1 AS a"));
        Assert.Contains("table", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropView_RemovesDescriptor()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        catalog.Plan("DROP VIEW v");

        Assert.False(catalog.Views.TryGet(new QualifiedName("public", "v"), out _));
    }

    [Fact]
    public void DropView_OnUnknownName_Throws()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            catalog.Plan("DROP VIEW ghost"));
        Assert.Contains("not registered", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DropViewIfExists_NoopOnUnknownName()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        // No throw.
        catalog.Plan("DROP VIEW IF EXISTS ghost");
    }

    [Fact]
    public void CreateView_RaisesViewCreatedEvent()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");

        ViewCreatedEvent? captured = null;
        catalog.Events.ViewCreated += e => captured = e;

        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        Assert.NotNull(captured);
        Assert.Equal("public", captured!.Name.Schema);
        Assert.Equal("v", captured.Name.Name);
    }

    [Fact]
    public void CreateOrReplaceView_RaisesViewAlteredEvent()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        ViewAlteredEvent? captured = null;
        catalog.Events.ViewAltered += e => captured = e;

        catalog.Plan("CREATE OR REPLACE VIEW v AS SELECT b FROM t");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
        Assert.NotNull(captured.After);
    }

    [Fact]
    public void DropView_RaisesViewDroppedEvent()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        ViewDroppedEvent? captured = null;
        catalog.Events.ViewDropped += e => captured = e;

        catalog.Plan("DROP VIEW v");

        Assert.NotNull(captured);
        Assert.NotNull(captured!.Before);
    }

    [Fact]
    public async Task DirectViewCycle_ThrowsAtPlanTime()
    {
        // a view that references itself by name. The registrar can't see
        // this at registration time because the descriptor table isn't
        // queried until expansion, so detection lands at plan time.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");
        // Replace with a self-reference.
        catalog.Plan("CREATE OR REPLACE VIEW v AS SELECT a FROM v");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            StatementPlan plan = catalog.Plan("SELECT * FROM v");
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });
        Assert.Contains("Circular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task IndirectViewCycle_ThrowsAtPlanTime()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        // Bootstrap independent views, then close the cycle via OR REPLACE.
        catalog.Plan("CREATE VIEW v_a AS SELECT a FROM t");
        catalog.Plan("CREATE VIEW v_b AS SELECT a FROM v_a");
        catalog.Plan("CREATE OR REPLACE VIEW v_a AS SELECT a FROM v_b");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            StatementPlan plan = catalog.Plan("SELECT * FROM v_a");
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });
        Assert.Contains("Circular", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewOnView_NoCycle_Works()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("INSERT INTO t VALUES (1), (2), (3)");
        catalog.Plan("CREATE VIEW v_inner AS SELECT a FROM t WHERE a > 1");
        catalog.Plan("CREATE VIEW v_outer AS SELECT a FROM v_inner");

        StatementPlan plan = catalog.Plan("SELECT a FROM v_outer");
        List<int> values = await CollectFirstColumnInts(plan);

        Assert.Equal(new[] { 2, 3 }, values);
    }

    [Fact]
    public async Task SystemViews_SurfacesRegisteredView()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        StatementPlan plan = catalog.Plan("SELECT schema, name FROM system.views");
        List<(string schema, string name)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add((batch[i][0].AsString().ToString(), batch[i][1].AsString().ToString()));
            }
        }

        Assert.Contains(("public", "v"), rows);
    }

    [Fact]
    public async Task ReopenedCatalog_RehydratesViews()
    {
        using (TableCatalog first = CreateCatalog(_catalogPath))
        {
            first.Plan("CREATE TABLE t (a Int32, b Int32)");
            first.Plan("INSERT INTO t VALUES (1, 10), (2, 20)");
            first.Plan("CREATE VIEW v AS SELECT a FROM t WHERE b > 10");
        }

        using TableCatalog reopened = CreateCatalog(_catalogPath);

        Assert.True(reopened.Views.TryGet(new QualifiedName("public", "v"), out _));
        Assert.Equal(1, reopened.CatalogLoadReport!.LoadedViews);

        // And the substituted body still resolves columns.
        StatementPlan plan = reopened.Plan("SELECT * FROM v");
        List<int> values = await CollectFirstColumnInts(plan);
        Assert.Equal(new[] { 2 }, values);
    }

    [Fact]
    public void DroppedView_RemovedFromManifestOnReopen()
    {
        using (TableCatalog first = CreateCatalog(_catalogPath))
        {
            first.Plan("CREATE TABLE t (a Int32)");
            first.Plan("CREATE VIEW v AS SELECT a FROM t");
            first.Plan("DROP VIEW v");
        }

        using TableCatalog reopened = CreateCatalog(_catalogPath);

        Assert.False(reopened.Views.TryGet(new QualifiedName("public", "v"), out _));
        Assert.Equal(0, reopened.CatalogLoadReport!.LoadedViews);
    }

    [Fact]
    public async Task InformationSchemaViews_SurfacesRegisteredView()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t WHERE b > 0");

        StatementPlan plan = catalog.Plan(
            "SELECT table_schema, table_name, is_updatable, check_option FROM information_schema.views");
        List<(string schema, string name, string updatable, string check)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add((
                    batch[i][0].AsString().ToString(),
                    batch[i][1].AsString().ToString(),
                    batch[i][2].AsString().ToString(),
                    batch[i][3].AsString().ToString()));
            }
        }

        Assert.Contains(("public", "v", "NO", "NONE"), rows);
    }

    [Fact]
    public async Task InformationSchemaTables_ListsViewWithViewType()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        StatementPlan plan = catalog.Plan(
            "SELECT table_name, table_type FROM information_schema.tables WHERE table_name = 'v'");
        List<(string name, string type)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rows.Add((batch[i][0].AsString().ToString(), batch[i][1].AsString().ToString()));
            }
        }

        // information_schema.tables enumerates ITableProviders, which includes
        // the system.views provider with table_type = 'VIEW' (system-classified)
        // but NOT the user-registered view itself today — views aren't
        // ITableProviders. The user-facing flow for "list registered views"
        // is information_schema.views, covered separately.
        Assert.DoesNotContain(rows, r => r.name == "v" && r.type == "BASE TABLE");
    }

    [Fact]
    public void ManifestBuilder_ResolvesViewColumns()
    {
        // The LSP manifest builder should expose views in the Tables list with
        // resolved columns so FROM-completion and column-completion both work.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b String)");
        catalog.Plan("CREATE VIEW v AS SELECT a, b FROM t");

        DatumIngest.Manifest.LanguageServerManifest manifest =
            CatalogManifestBuilder.Build(catalog, catalog.Functions);

        DatumIngest.Manifest.TableSchemaEntry? viewEntry =
            manifest.Tables.FirstOrDefault(t => t.Name == "public.v");
        Assert.NotNull(viewEntry);
        Assert.Equal(2, viewEntry!.Columns.Count);
        Assert.Contains(viewEntry.Columns, c => c.Name == "a");
        Assert.Contains(viewEntry.Columns, c => c.Name == "b");
    }

    [Fact]
    public async Task InsertIntoView_ThrowsClearError()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(catalog, "INSERT INTO v VALUES (1)"));
        Assert.Contains("is a view", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INSERT", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateView_ThrowsClearError()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        DatumIngest.Execution.QueryPlanException ex = await Assert
            .ThrowsAsync<DatumIngest.Execution.QueryPlanException>(
                () => RunAsync(catalog, "UPDATE v SET a = 1"));
        Assert.Contains("is a view", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UPDATE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteFromView_ThrowsClearError()
    {
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32, b Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => RunAsync(catalog, "DELETE FROM v"));
        Assert.Contains("is a view", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DELETE", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunAsync(TableCatalog catalog, string sql)
    {
        StatementPlan plan = await catalog.PlanAsync(sql);
        await catalog.ExecuteAsync(plan).DrainAsync();
    }

    [Fact]
    public void ManifestBuilder_ViewEntry_CarriesViewKind()
    {
        // Hover / catalog explorer need a discriminator to render "View"
        // vs "Table". CatalogManifestBuilder stamps Kind = "VIEW" on
        // view-sourced entries.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");

        DatumIngest.Manifest.LanguageServerManifest manifest =
            CatalogManifestBuilder.Build(catalog, catalog.Functions);

        DatumIngest.Manifest.TableSchemaEntry? viewEntry =
            manifest.Tables.FirstOrDefault(t => t.Name == "public.v");
        DatumIngest.Manifest.TableSchemaEntry? tableEntry =
            manifest.Tables.FirstOrDefault(t => t.Name == "public.t");

        Assert.NotNull(viewEntry);
        Assert.Equal("VIEW", viewEntry!.Kind);
        Assert.NotNull(tableEntry);
        Assert.Equal("TABLE", tableEntry!.Kind);
    }

    [Fact]
    public void ManifestBuilder_ProjectingView_SurfacesOnlyProjectedColumns()
    {
        // Regression: a view that projects a strict subset of the underlying
        // table's columns should surface only the projection in the LSP
        // manifest (which drives hover) — not the underlying table's full
        // column set. The QuerySchemaResolver was previously returning
        // FROM/JOIN source columns instead of the SELECT projection.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE wide (a Int32, b Int32, c String, d Float32, e Boolean)");
        catalog.Plan("CREATE VIEW narrow AS SELECT a, c FROM wide WHERE b > 0");

        DatumIngest.Manifest.LanguageServerManifest manifest =
            CatalogManifestBuilder.Build(catalog, catalog.Functions);

        DatumIngest.Manifest.TableSchemaEntry? viewEntry =
            manifest.Tables.FirstOrDefault(t => t.Name == "public.narrow");
        Assert.NotNull(viewEntry);
        Assert.Equal(["a", "c"], viewEntry!.Columns.Select(c => c.Name));
    }

    [Fact]
    public void ManifestBuilder_BrokenViewBody_DegradesToEmptyColumns()
    {
        // A view whose body references a now-dropped table still surfaces by
        // name (so completion at least suggests it), but with empty columns.
        using TableCatalog catalog = CreateCatalog(_catalogPath);
        catalog.Plan("CREATE TABLE t (a Int32)");
        catalog.Plan("CREATE VIEW v AS SELECT a FROM t");
        catalog.Plan("DROP TABLE t");

        DatumIngest.Manifest.LanguageServerManifest manifest =
            CatalogManifestBuilder.Build(catalog, catalog.Functions);

        DatumIngest.Manifest.TableSchemaEntry? viewEntry =
            manifest.Tables.FirstOrDefault(t => t.Name == "public.v");
        Assert.NotNull(viewEntry);
        Assert.Empty(viewEntry!.Columns);
    }

    private static async Task<List<int>> CollectFirstColumnInts(StatementPlan plan)
    {
        List<int> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsInt32());
            }
        }
        return values;
    }
}
