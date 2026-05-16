using DatumIngest.Catalog;
using DatumIngest.Data;
using DatumIngest.ModelLibrary;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.ModelLibrary;

// Web-host implementation of IModelInstaller. Reads the .sql file named
// by CatalogVersion.InstallSql (resolved against the manifest directory)
// and runs each top-level statement through TableCatalog.ExecuteStatementAsync —
// the same dispatch the SQL editor uses, so CREATE MODEL applies as a normal
// DDL side effect and registers the model in catalog.DeclaredModels (and is
// persisted to the on-disk catalog file, so a restart preserves it).
//
// Pinned-install mode rewrites the SQL source before executing so a version
// installed alongside a different active one registers under the suffixed
// `<bare>@<digits>` identifier. USING-path resolution to the pinned
// version's folder is handled by `ModelInstallContext.CurrentVersionPin`
// (set by ModelDownloadService for the duration of the install) — the
// installer doesn't rewrite USING paths itself.
internal sealed class CatalogBackedModelInstaller : IModelInstaller
{
    private const string ModelsSchema = "models";

    private readonly TableCatalog _catalog;
    private readonly IManifestStore _manifest;
    private readonly ILogger<CatalogBackedModelInstaller> _logger;

    public CatalogBackedModelInstaller(
        TableCatalog catalog,
        IManifestStore manifest,
        ILogger<CatalogBackedModelInstaller> logger)
    {
        _catalog = catalog;
        _manifest = manifest;
        _logger = logger;
    }

    public ValueTask<bool> IsInstalledAsync(CatalogModel model, CancellationToken ct)
    {
        string conventionalName = ToConventionalModelName(model.Id);
        bool registered = _catalog.DeclaredModels.TryGet(
            new QualifiedName(ModelsSchema, conventionalName),
            descriptor: out _);
        return ValueTask.FromResult(registered);
    }

    public async ValueTask<IReadOnlyList<string>> InstallAsync(
        CatalogModel model,
        CatalogVersion version,
        bool pinnedMode,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(version.InstallSql))
        {
            // ModelDownloadService gates on this, but defensively no-op so
            // a direct caller doesn't get a NullReferenceException reading
            // a null relative path.
            return [];
        }

        string sqlPath = Path.GetFullPath(
            Path.Combine(_manifest.ManifestDirectory, version.InstallSql));
        if (!File.Exists(sqlPath))
        {
            throw new FileNotFoundException(
                $"Install SQL for model '{model.Id}' version '{version.Version}' not found at {sqlPath}.",
                sqlPath);
        }

        _logger.LogInformation(
            "Installing {ModelId} (version {Version}, pinned={Pinned}) from {SqlPath}",
            model.Id, version.Version, pinnedMode, sqlPath);

        string sql = await File.ReadAllTextAsync(sqlPath, ct).ConfigureAwait(false);
        if (pinnedMode)
        {
            sql = PinnedInstallSqlRewriter.Rewrite(sql, version);
        }

        // ParseBatchWithText returns each top-level statement with its
        // source-text slice — CREATE MODEL needs the slice to round-trip
        // through the catalog file on reload, same as CREATE FUNCTION.
        IReadOnlyList<(Statement Statement, string SourceText)> statements =
            SqlParser.ParseBatchWithText(sql);
        if (statements.Count == 0)
        {
            throw new InvalidOperationException(
                $"Install SQL for model '{model.Id}' parsed to zero statements.");
        }

        // Push catalog provenance onto the install context so each registered
        // ModelDescriptor knows which catalog entry / version it came from
        // — stamped into the persisted catalog row so rehydrate can resolve
        // the originating installSql without re-saving the verbatim source.
        string? previousCatalogId = ModelInstallContext.CurrentCatalogId;
        bool previousIsPinned = ModelInstallContext.CurrentInstallIsPinned;
        ModelInstallContext.CurrentCatalogId = model.Id;
        ModelInstallContext.CurrentInstallIsPinned = pinnedMode;
        try
        {
            // Apply each statement in order via a per-statement command on
            // a shared connection. CREATE MODEL routes through ModelPlan,
            // whose ExecuteImplAsync applies the registrar mutation. Throw
            // early on the first failure — partial install would leave the
            // catalog in an inconsistent state.
            List<string> observed = [];
            using InProcessDatumDbConnection conn = new(_catalog);
            foreach ((Statement statement, string sourceText) in statements)
            {
                ct.ThrowIfCancellationRequested();
                using InProcessDatumDbCommand cmd = conn.CreateCommand();
                cmd.Statement = statement;
                cmd.SourceText = sourceText;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (statement is CreateModelStatement create)
                {
                    observed.Add(create.Name);
                }
            }
            return observed;
        }
        finally
        {
            ModelInstallContext.CurrentInstallIsPinned = previousIsPinned;
            ModelInstallContext.CurrentCatalogId = previousCatalogId;
        }
    }

    public async ValueTask DropModelsAsync(IReadOnlyList<string> identifiers, CancellationToken ct)
    {
        foreach (string identifier in identifiers)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Round-trip through the SQL surface so the same DROP path
                // the user invokes manually runs here — persisted catalog
                // state, lazy-disposal of any leases, and the ModelRegistry
                // unregister all happen as a single side effect.
                using InProcessDatumDbConnection conn = new(_catalog);
                using InProcessDatumDbCommand cmd = conn.CreateCommand(
                    $"DROP MODEL IF EXISTS {identifier}");
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The caller is unwinding a failed install; a follow-on
                // throw here would mask the original cross-check diagnostic.
                // Log and proceed so the loop drops everything it can.
                _logger.LogWarning(
                    ex, "Cross-check cleanup: failed to DROP MODEL '{Identifier}'", identifier);
            }
        }
    }

    // catalog id is kebab-case (paddleocr-v4-det); SQL identifiers are
    // snake_case (paddleocr_v4_det). Dashes and dots both become
    // underscores: `bge-small-en-v1.5` → `bge_small_en_v1_5`, since dots
    // would otherwise be parsed as a schema-qualified name. The
    // convention is hard — if a SQL author registers a different name,
    // the probe will see Downloaded forever after a successful install
    // (clearly broken; fix the SQL).
    private static string ToConventionalModelName(string id) =>
        id.Replace('-', '_').Replace('.', '_');
}
