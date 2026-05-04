using DatumIngest.Catalog;
using DatumIngest.DatasetLibrary;
using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

namespace DatumIngest.Web.Lsp;

/// <summary>
/// Process-wide host for the SQL <see cref="LanguageService"/>. Owns one
/// service instance, initialises its <see cref="LanguageServerManifest"/>
/// from the live <see cref="TableCatalog"/> at construction, and keeps it
/// in sync by subscribing to <see cref="TableCatalog.Events"/>. Every DDL
/// statement that touches the LSP-visible surface (functions, procedures,
/// models, schemas, tables, indexes) triggers a manifest rebuild.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Concurrency.</strong> Rebuilds happen on the DDL caller's
/// thread (Events fire synchronously, see <see cref="CatalogEvents"/>
/// remarks). A monitor lock serialises rebuilds with each other so two
/// concurrent DDL statements don't trample the manifest. LSP method
/// calls don't take the lock — they're pure-over-manifest reads and a
/// brief race during an Initialize swap returns either the old or new
/// snapshot, which is fine for an IDE feature where the next keystroke
/// retries anyway.
/// </para>
/// <para>
/// <strong>Batching.</strong> A 100-statement DDL script fires 100
/// events and triggers 100 rebuilds. Each rebuild is cheap (single-
/// digit milliseconds for typical catalogs) so the overhead is
/// acceptable for v1. If batch DDL becomes a hot spot, debounce by
/// flipping a dirty flag and rebuilding on a short timer.
/// </para>
/// </remarks>
public sealed class LanguageManifestService
{
    private readonly TableCatalog _catalog;
    private readonly DatasetSchemaBinder? _datasetBinder;
    private readonly LanguageService _service = new();
    private readonly object _rebuildLock = new();

    public LanguageManifestService(TableCatalog catalog)
        : this(catalog, datasetBinder: null) { }

    public LanguageManifestService(TableCatalog catalog, DatasetSchemaBinder? datasetBinder)
    {
        _catalog = catalog;
        _datasetBinder = datasetBinder;
        if (datasetBinder is not null)
        {
            datasetBinder.BindingsChanged += Rebuild;
        }
        Rebuild();
        Subscribe(catalog.Events);
    }

    /// <summary>
    /// The language service backing every <c>/api/lang/*</c> endpoint.
    /// Callers should treat its methods as pure over a slowly-changing
    /// manifest — the snapshot may swap mid-call but never tear.
    /// </summary>
    public LanguageService Service => _service;

    /// <summary>
    /// Most recent built manifest. Exposed so non-LSP surfaces (e.g.
    /// the future catalog sidebar that lists all registered functions)
    /// can read the snapshot without going through a provider method.
    /// </summary>
    public LanguageServerManifest CurrentManifest { get; private set; } = null!;

    private void Rebuild()
    {
        lock (_rebuildLock)
        {
            LanguageServerManifest manifest = CatalogManifestBuilder.Build(
                _catalog, _catalog.Functions, _datasetBinder);
            _service.Initialize(manifest);
            CurrentManifest = manifest;
        }
    }

    // Subscribes one rebuild handler per event kind. Verbose but explicit —
    // a meta "any-change" event on CatalogEvents would be nicer but doesn't
    // exist yet and isn't worth adding for one consumer.
    private void Subscribe(CatalogEvents events)
    {
        events.SchemaCreated += _ => Rebuild();
        events.SchemaDropped += _ => Rebuild();

        events.TableCreated += _ => Rebuild();
        events.TableAltered += _ => Rebuild();
        events.TableDropped += _ => Rebuild();

        events.IndexCreated += _ => Rebuild();
        events.IndexDropped += _ => Rebuild();

        events.FunctionCreated += _ => Rebuild();
        events.FunctionAltered += _ => Rebuild();
        events.FunctionDropped += _ => Rebuild();

        events.ProcedureCreated += _ => Rebuild();
        events.ProcedureAltered += _ => Rebuild();
        events.ProcedureDropped += _ => Rebuild();

        events.ModelCreated += _ => Rebuild();
        events.ModelAltered += _ => Rebuild();
        events.ModelDropped += _ => Rebuild();

        events.ViewCreated += _ => Rebuild();
        events.ViewAltered += _ => Rebuild();
        events.ViewDropped += _ => Rebuild();
    }
}
