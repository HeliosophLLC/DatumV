using DatumIngest.Catalog;
using DatumIngest.ModelLibrary;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.ModelLibrary;

// Web-host implementation of IModelInstaller. Reads the .sql file named
// by CatalogModel.InstallSql (resolved against the manifest directory) and
// runs each top-level statement through TableCatalog.PlanAsync — the same
// dispatch the SQL editor uses, so CREATE MODEL applies as a normal DDL
// side effect and registers the model in catalog.DeclaredModels (and is
// persisted to the on-disk catalog file, so a restart preserves it).
//
// The probe-time IsInstalledAsync check uses a naming convention rather
// than introspecting the SQL: the .sql file is expected to register a
// model whose qualified name is `models.<id with '-' replaced by '_'>`
// (CREATE MODEL hard-locks the schema to `models`). A single file may
// include additional CREATE MODEL statements (helper sub-models registered
// by the same install pass) — those get registered too, but their
// presence isn't part of "installed?" The catalog id is the entry-point
// model and the one we track.
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

    public async ValueTask InstallAsync(CatalogModel model, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            // ModelDownloadService gates on this, but defensively no-op so
            // a direct caller doesn't get a NullReferenceException reading
            // a null relative path.
            return;
        }

        string sqlPath = Path.GetFullPath(
            Path.Combine(_manifest.ManifestDirectory, model.InstallSql));
        if (!File.Exists(sqlPath))
        {
            throw new FileNotFoundException(
                $"Install SQL for model '{model.Id}' not found at {sqlPath}.", sqlPath);
        }

        _logger.LogInformation("Installing {ModelId} from {SqlPath}", model.Id, sqlPath);

        string sql = await File.ReadAllTextAsync(sqlPath, ct).ConfigureAwait(false);

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

        // Apply each statement in order. DDL applies as a side effect inside
        // ExecuteStatementAsync (Routines.ApplyCreateModelAsync runs there),
        // so we don't need to also iterate the returned plan for CREATE MODEL.
        // Throw early on the first failure — partial install would leave the
        // catalog in an inconsistent state.
        foreach ((Statement statement, string sourceText) in statements)
        {
            ct.ThrowIfCancellationRequested();
            await _catalog.ExecuteStatementAsync(statement, sourceText).ConfigureAwait(false);
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
