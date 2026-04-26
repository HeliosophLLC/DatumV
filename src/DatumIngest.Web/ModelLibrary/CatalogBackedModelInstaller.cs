using DatumIngest.Catalog;
using DatumIngest.ModelLibrary;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;

using Microsoft.Extensions.Logging;

using Superpower.Model;

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
            sql = RewriteIdentifiersForPinnedInstall(sql, version);
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

        // Apply each statement in order. DDL applies as a side effect inside
        // ExecuteStatementAsync (Routines.ApplyCreateModelAsync runs there),
        // so we don't need to also iterate the returned plan for CREATE MODEL.
        // Throw early on the first failure — partial install would leave the
        // catalog in an inconsistent state.
        List<string> observed = [];
        foreach ((Statement statement, string sourceText) in statements)
        {
            ct.ThrowIfCancellationRequested();
            await _catalog.ExecuteStatementAsync(statement, sourceText).ConfigureAwait(false);
            if (statement is CreateModelStatement create)
            {
                observed.Add(create.Name);
            }
        }
        return observed;
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
                Statement stmt = SqlParser.ParseStatement($"DROP MODEL IF EXISTS {identifier}");
                await _catalog.ExecuteStatementAsync(stmt, $"DROP MODEL IF EXISTS {identifier}")
                    .ConfigureAwait(false);
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

    // Source-level rewrite for pinned installs. Walks tokens until a
    // `CREATE [OR REPLACE] MODEL <Identifier>` sequence appears, then
    // substitutes the identifier with its <see cref="CatalogVersionModel.PinnedAs"/>
    // form. Only the identifier source range is touched — comments and
    // every other identifier mention pass through untouched. USING-path
    // resolution to the pinned version's folder is handled separately by
    // `ModelInstallContext.CurrentVersionPin` (set by the download service
    // for the duration of the install), so we don't rewrite USING strings
    // here.
    private static string RewriteIdentifiersForPinnedInstall(string sql, CatalogVersion version)
    {
        if (version.Models is null || version.Models.Count == 0) { return sql; }

        Dictionary<string, string> pinMap = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogVersionModel vm in version.Models)
        {
            string pinnedAs = vm.EffectivePinnedAs(version.Version);
            if (!string.Equals(pinnedAs, vm.Identifier, StringComparison.Ordinal))
            {
                pinMap[vm.Identifier] = pinnedAs;
            }
        }
        if (pinMap.Count == 0) { return sql; }

        TokenList<SqlToken> tokens;
        try
        {
            tokens = SqlTokenizer.Instance.Tokenize(sql);
        }
        catch (Superpower.ParseException)
        {
            // Tokeniser refused: let the downstream ParseBatchWithText
            // surface the diagnostic against the original source, since
            // any rewriting we do here would be against a malformed
            // baseline anyway.
            return sql;
        }

        // Collect (start, length, replacement) substitutions to apply in
        // reverse order. Walking sequentially keeps offset arithmetic
        // simple — reverse application means earlier offsets stay valid.
        List<(int Start, int Length, string Replacement)> subs = [];
        Token<SqlToken>[] arr = tokens.ToArray();
        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i].Kind != SqlToken.Create) { continue; }
            int j = i + 1;
            // Skip optional OR REPLACE.
            if (j + 1 < arr.Length
                && arr[j].Kind == SqlToken.Or
                && arr[j + 1].Kind == SqlToken.Replace)
            {
                j += 2;
            }
            // Soft `MODEL` keyword tokenises as an Identifier with text "MODEL".
            if (j >= arr.Length
                || arr[j].Kind != SqlToken.Identifier
                || !arr[j].ToStringValue().Equals("MODEL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            int identIndex = j + 1;
            if (identIndex >= arr.Length || arr[identIndex].Kind != SqlToken.Identifier)
            {
                continue;
            }
            Token<SqlToken> identTok = arr[identIndex];
            string identText = identTok.ToStringValue();
            if (!pinMap.TryGetValue(identText, out string? pinnedAs))
            {
                continue;
            }
            subs.Add((
                Start: identTok.Span.Position.Absolute,
                Length: identTok.Span.Length,
                Replacement: pinnedAs));
        }

        if (subs.Count == 0) { return sql; }

        subs.Sort(static (a, b) => b.Start.CompareTo(a.Start));
        System.Text.StringBuilder builder = new(sql);
        foreach ((int start, int length, string replacement) in subs)
        {
            builder.Remove(start, length);
            builder.Insert(start, replacement);
        }
        return builder.ToString();
    }
}
