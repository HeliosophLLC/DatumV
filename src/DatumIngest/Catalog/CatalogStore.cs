using System.Text.Json;
using System.Text.Json.Serialization;

using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Loads and saves the persistent on-disk representation of a
/// <see cref="TableCatalog"/>'s session-survivable state. The first
/// version persists the <see cref="UdfRegistry"/> only; the format reserves
/// space for future sections (bound files, fingerprints, materialised
/// views) without breaking older readers.
/// </summary>
/// <remarks>
/// <para>
/// File format — UTF-8 JSON v3, written atomically (write-temp + rename)
/// so a crash mid-write never leaves a partial file:
/// </para>
/// <code>
/// {
///   "version": 5,
///   "udfs":       [{ "schema": "udf",  "name": "shout", "parameters": [...], "body_kind": "macro", "body": "upper(s)" }],
///   "procedures": [{ "schema": "proc", "name": "...", "source_text": "..." }],
///   "models":     [{ "schema": "models", "name": "paddleocr_v4_det", "source_text": "CREATE OR REPLACE MODEL paddleocr_v4_det(...)" }],
///   "backends": {
///     "flat_file": {
///       "tables": [
///         { "schema": "public", "name": "users", "file_path": "users.datum",
///           "indexes": [...], "primary_key_constraint_name": null }
///       ]
///     }
///   }
/// }
/// </code>
/// <para>
/// Per-backend state lives under <c>backends.&lt;key&gt;</c>; the file's
/// top-level structure stays stable across backend additions. Each
/// <see cref="ITableCatalog"/> implementation owns the shape under its
/// own key. Today only <c>flat_file</c> persists state; system / virtual
/// schemas are reconstructed at startup with no per-instance persistence.
/// </para>
/// <para>
/// <strong>No backward compatibility.</strong> A manifest whose version
/// is not <c>5</c> is rejected with <see cref="CatalogStoreLoadException"/>
/// — the caller must delete the catalog directory and start fresh.
/// Schema support requires v3; real schema membership for UDFs /
/// procedures requires v4; persistence of SQL-defined models requires v5.
/// </para>
/// <para>
/// Failure handling at load:
/// </para>
/// <list type="bullet">
///   <item><description>File not present → empty registry, no error (first-time startup).</description></item>
///   <item><description>File present but unreadable / malformed JSON / wrong version →
///     <see cref="CatalogStoreLoadException"/>.</description></item>
///   <item><description>Individual UDF / procedure entry that fails to re-parse →
///     skipped with a warning on the load report.</description></item>
/// </list>
/// </remarks>
public sealed class CatalogStore
{
    /// <summary>
    /// Conventional file name for the catalog's session-survivable state.
    /// Hosts may choose any path; this constant exists so callers that
    /// follow the default convention agree on the suffix.
    /// </summary>
    public const string DefaultFileName = ".datum-catalog.json";

    /// <summary>
    /// The schema version this binary reads and writes.
    /// <list type="bullet">
    ///   <item><description><c>1</c>: udfs + procedures only.</description></item>
    ///   <item><description><c>2</c>: adds top-level <c>tables</c> section.</description></item>
    ///   <item><description><c>3</c>: schema-aware. UDF / procedure entries
    ///     gain a placeholder <c>schema</c> field that is persisted-but-ignored.
    ///     Persistent table state moves under <c>backends.flat_file</c>.</description></item>
    ///   <item><description><c>4</c>: UDF / procedure entries gain real schema
    ///     membership — the <c>schema</c> field becomes load-bearing and
    ///     determines the <see cref="QualifiedName"/> the registry stores
    ///     the entry under. The <c>udf</c> / <c>proc</c> placeholder schemas
    ///     are abandoned.</description></item>
    ///   <item><description><c>5</c>: adds a top-level <c>models</c> section.
    ///     SQL-defined models created via <c>CREATE MODEL</c> persist their
    ///     verbatim source text so they can be re-applied (via the inference
    ///     dispatcher's <c>LoadBundleAsync</c>) on the next process start.
    ///     Rehydration is deferred to <see cref="TableCatalog.RehydrateModelsAsync"/>
    ///     because session loading is async — Load only captures the
    ///     pending entries.</description></item>
    ///   <item><description><c>6</c>: model rows bifurcate by origin.
    ///     <em>Catalog-installed rows</em> persist
    ///     <c>(catalog_id, catalog_version, pinned_as?)</c> pointers instead
    ///     of source text — rehydrate resolves the installSql by
    ///     <c>(catalog_id, catalog_version)</c> against the current manifest
    ///     so edits to the on-disk SQL file flow through on the next process
    ///     start. <em>User-authored rows</em> (no catalog parent) keep the
    ///     v5 verbatim-source-text shape because the source has nowhere
    ///     else to live.</description></item>
    ///   <item><description><c>7</c>: user-authored UDF / procedure / model
    ///     bodies move out of the JSON manifest into per-object <c>.sql</c>
    ///     files at <c>udfs/&lt;schema&gt;/&lt;name&gt;.sql</c>,
    ///     <c>procedures/&lt;schema&gt;/&lt;name&gt;.sql</c>, and
    ///     <c>models/&lt;name&gt;.sql</c> respectively. The manifest entry
    ///     shrinks to <c>(schema, name, file_path)</c>; the file holds the
    ///     verbatim <c>CREATE OR REPLACE …</c> SQL. The source-text-bearing
    ///     UDF / procedure / user-model fields from earlier versions are
    ///     removed. Catalog-installed model rows keep the v6 provenance shape.
    ///     </description></item>
    ///   <item><description><c>8</c>: adds a top-level <c>views</c> section
    ///     for SQL views registered via <c>CREATE VIEW</c>. Each entry
    ///     carries <c>(schema, name, file_path)</c> following the v7 UDF
    ///     shape; the body lives at <c>views/&lt;schema&gt;/&lt;name&gt;.sql</c>
    ///     as verbatim <c>CREATE OR REPLACE VIEW</c> SQL. Rehydration runs
    ///     synchronously inside <see cref="Load"/> — re-parsing a view body
    ///     has no async dependencies.</description></item>
    /// </list>
    /// Only v8 is accepted; older and newer versions both throw at load.
    /// </summary>
    public const int CurrentVersion = 8;

    private readonly string _path;
    private readonly object _writeLock = new();

    /// <summary>
    /// Optional callback that returns <see cref="FlatFileCatalog"/>'s
    /// current persistent state at <see cref="Save"/> time. Wired up by
    /// <see cref="TableCatalog"/> at construction; <see langword="null"/>
    /// means the FlatFile backend has nothing to persist (e.g. no catalog
    /// path was supplied).
    /// </summary>
    private Func<FlatFileBackendState>? _flatFileStateProvider;

    /// <summary>
    /// FlatFile backend state captured at the last <see cref="Load"/>
    /// call. <see cref="TableCatalog"/> reads this at construction to
    /// rehydrate persistent tables. <see langword="null"/> before
    /// <see cref="Load"/> runs or when the file had no FlatFile state.
    /// </summary>
    private FlatFileBackendState? _loadedFlatFileState;

    /// <summary>
    /// Pending SQL-defined-model entries captured at the last <see cref="Load"/>
    /// call. Empty before <see cref="Load"/> runs or when the file had no
    /// model entries. Consumed by <see cref="TableCatalog.RehydrateModelsAsync"/>
    /// after the inference dispatcher and models directory are wired
    /// — loading bundles is async and can't happen inside the synchronous
    /// catalog constructor.
    /// </summary>
    private IReadOnlyList<PendingModelEntry> _loadedModelEntries = [];

    /// <summary>Creates a store rooted at <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path to the catalog JSON file.</param>
    public CatalogStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The absolute path to the persisted catalog file.</summary>
    public string Path => _path;

    /// <summary>
    /// Wires <paramref name="provider"/> as the callback that supplies
    /// <see cref="FlatFileCatalog"/>'s persistent state at every
    /// <see cref="Save"/>. <see cref="TableCatalog"/> calls this once at
    /// construction so UDF / procedure save call-sites don't need to
    /// know about tables — they all round-trip through one file.
    /// </summary>
    public void SetFlatFileBackendStateProvider(Func<FlatFileBackendState> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _flatFileStateProvider = provider;
    }

    /// <summary>
    /// Returns the persisted FlatFile backend state captured by the most
    /// recent <see cref="Load"/> call, or <see langword="null"/> when no
    /// state is available (file missing or no FlatFile entries).
    /// </summary>
    /// <remarks>
    /// Called by <see cref="TableCatalog"/> right after
    /// <see cref="Load(UdfRegistry, ProcedureRegistry, ViewRegistry)"/> so the
    /// FlatFile backend can rehydrate every persistent table.
    /// </remarks>
    public FlatFileBackendState? LoadedFlatFileBackendState => _loadedFlatFileState;

    /// <summary>
    /// Pending model entries captured by the most recent <see cref="Load"/>
    /// call. The list is in (schema, name) sort order — same order they
    /// were written. <see cref="TableCatalog.RehydrateModelsAsync"/>
    /// iterates this collection after the inference dispatcher is wired
    /// to re-execute each persisted <c>CREATE MODEL</c> statement.
    /// </summary>
    public IReadOnlyList<PendingModelEntry> LoadedModelEntries => _loadedModelEntries;

    /// <summary>
    /// Loads the persisted state into <paramref name="udfs"/>,
    /// <paramref name="procedures"/>, and <paramref name="views"/>. Returns
    /// a report describing what loaded and what was skipped. Does not
    /// throw for a missing file — that's a normal first-time-startup case.
    /// </summary>
    /// <param name="udfs">The UDF registry to populate.</param>
    /// <param name="procedures">The procedure registry to populate.</param>
    /// <param name="views">The view registry to populate.</param>
    /// <exception cref="CatalogStoreLoadException">
    /// The file exists but cannot be read or is not valid JSON. Individual
    /// bad entries are skipped (and reported) rather than throwing.
    /// </exception>
    public CatalogStoreLoadReport Load(UdfRegistry udfs, ProcedureRegistry procedures, ViewRegistry views)
    {
        if (!File.Exists(_path))
        {
            return new CatalogStoreLoadReport(
                LoadedUdfs: 0, SkippedUdfs: 0,
                LoadedProcedures: 0, SkippedProcedures: 0,
                LoadedViews: 0, SkippedViews: 0,
                Warnings: []);
        }

        string json;
        try
        {
            json = File.ReadAllText(_path);
        }
        catch (Exception ex)
        {
            throw new CatalogStoreLoadException(
                $"Failed to read catalog file '{_path}': {ex.Message}", ex);
        }

        CatalogFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(json, CatalogJsonContext.Default.CatalogFile);
        }
        catch (JsonException ex)
        {
            throw new CatalogStoreLoadException(
                $"Catalog file '{_path}' is not valid JSON: {ex.Message}", ex);
        }

        if (parsed is null)
        {
            return new CatalogStoreLoadReport(
                LoadedUdfs: 0, SkippedUdfs: 0,
                LoadedProcedures: 0, SkippedProcedures: 0,
                LoadedViews: 0, SkippedViews: 0,
                Warnings: []);
        }

        // Strict version enforcement. Schema support requires v3; older
        // and newer manifests are both rejected so a mismatched binary
        // can't silently start fresh and lose state.
        if (parsed.Version != CurrentVersion)
        {
            throw new CatalogStoreLoadException(
                $"Catalog file '{_path}' has version {parsed.Version}; this " +
                $"binary requires version {CurrentVersion}. Delete the catalog " +
                "directory to start fresh.");
        }

        // Capture the FlatFile backend's state so TableCatalog can rehydrate it.
        _loadedFlatFileState = parsed.Backends?.FlatFile;

        string catalogDirectory = System.IO.Path.GetDirectoryName(_path)
            ?? Environment.CurrentDirectory;

        // Capture SQL-defined model entries. They're rehydrated later via
        // TableCatalog.RehydrateModelsAsync (after the dispatcher + models
        // directory are wired). Two row shapes are accepted:
        //   * catalog-installed rows carry (catalog_id, catalog_version)
        //     and resolve their installSql against the live manifest.
        //   * user-authored rows carry file_path and the source is read
        //     from disk at rehydrate time.
        List<PendingModelEntry> models = new();
        List<string> warnings = new();
        foreach (CatalogFileModelEntry entry in parsed.Models ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Name)) { continue; }
            bool isCatalogRow = !string.IsNullOrWhiteSpace(entry.CatalogId)
                && !string.IsNullOrWhiteSpace(entry.CatalogVersion);
            bool isUserRow = !string.IsNullOrWhiteSpace(entry.FilePath);
            if (!isCatalogRow && !isUserRow) { continue; }

            string? sourceText = null;
            if (isUserRow)
            {
                sourceText = TryReadSqlFile(catalogDirectory, entry.FilePath!, $"model '{entry.Schema}.{entry.Name}'", warnings);
                if (sourceText is null) continue;
            }

            models.Add(new PendingModelEntry(
                Schema: entry.Schema ?? "models",
                Name: entry.Name!,
                SourceText: sourceText,
                CatalogId: entry.CatalogId,
                CatalogVersion: entry.CatalogVersion,
                PinnedAs: entry.PinnedAs));
        }
        _loadedModelEntries = models;

        int loadedUdfs = 0;
        int skippedUdfs = 0;
        int loadedProcedures = 0;
        int skippedProcedures = 0;

        foreach (CatalogFileUdfEntry entry in parsed.Udfs ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                skippedUdfs++;
                warnings.Add("Skipping UDF entry with missing or empty 'name'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.FilePath))
            {
                skippedUdfs++;
                warnings.Add($"Skipping UDF '{entry.Name}': manifest entry has no file_path.");
                continue;
            }

            string? sourceText = TryReadSqlFile(catalogDirectory, entry.FilePath!, $"UDF '{entry.Schema}.{entry.Name}'", warnings);
            if (sourceText is null) { skippedUdfs++; continue; }

            UdfDescriptor? descriptor = TryRehydrateUdfFromSource(
                entry.Schema ?? "public", entry.Name!, sourceText, udfs, warnings);
            if (descriptor is null) { skippedUdfs++; continue; }

            try
            {
                udfs.Register(descriptor, replace: false);
                loadedUdfs++;
            }
            catch (InvalidOperationException ex)
            {
                skippedUdfs++;
                warnings.Add($"Skipping UDF '{entry.Name}': {ex.Message}");
            }
        }

        foreach (CatalogFileProcedureEntry entry in parsed.Procedures ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                skippedProcedures++;
                warnings.Add("Skipping procedure entry with missing or empty 'name'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.FilePath))
            {
                skippedProcedures++;
                warnings.Add($"Skipping procedure '{entry.Name}': manifest entry has no file_path.");
                continue;
            }

            string? sourceText = TryReadSqlFile(catalogDirectory, entry.FilePath!, $"procedure '{entry.Schema}.{entry.Name}'", warnings);
            if (sourceText is null) { skippedProcedures++; continue; }

            ProcedureDescriptor? descriptor = TryRehydrateProcedureFromSource(
                entry.Schema ?? "public", entry.Name!, sourceText, warnings);
            if (descriptor is null) { skippedProcedures++; continue; }

            try
            {
                procedures.Register(descriptor, replace: false);
                loadedProcedures++;
            }
            catch (InvalidOperationException ex)
            {
                skippedProcedures++;
                warnings.Add($"Skipping procedure '{entry.Name}': {ex.Message}");
            }
        }

        int loadedViews = 0;
        int skippedViews = 0;

        foreach (CatalogFileViewEntry entry in parsed.Views ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                skippedViews++;
                warnings.Add("Skipping view entry with missing or empty 'name'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.FilePath))
            {
                skippedViews++;
                warnings.Add($"Skipping view '{entry.Name}': manifest entry has no file_path.");
                continue;
            }

            string? sourceText = TryReadSqlFile(catalogDirectory, entry.FilePath!, $"view '{entry.Schema}.{entry.Name}'", warnings);
            if (sourceText is null) { skippedViews++; continue; }

            ViewDescriptor? descriptor = TryRehydrateViewFromSource(
                entry.Schema ?? "public", entry.Name!, sourceText, warnings);
            if (descriptor is null) { skippedViews++; continue; }

            try
            {
                views.Register(descriptor, replace: false);
                loadedViews++;
            }
            catch (InvalidOperationException ex)
            {
                skippedViews++;
                warnings.Add($"Skipping view '{entry.Name}': {ex.Message}");
            }
        }

        return new CatalogStoreLoadReport(
            loadedUdfs, skippedUdfs,
            loadedProcedures, skippedProcedures,
            loadedViews, skippedViews,
            warnings);
    }

    /// <summary>
    /// Reads <paramref name="relativePath"/> resolved against
    /// <paramref name="catalogDirectory"/> as UTF-8 text. Records a warning
    /// and returns <see langword="null"/> when the file is missing or
    /// unreadable; load continues with the remaining entries so one missing
    /// file doesn't abort the whole catalog.
    /// </summary>
    private static string? TryReadSqlFile(
        string catalogDirectory, string relativePath, string contextLabel, List<string> warnings)
    {
        string resolved = System.IO.Path.GetFullPath(
            System.IO.Path.Combine(catalogDirectory, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        try
        {
            return File.ReadAllText(resolved);
        }
        catch (FileNotFoundException)
        {
            warnings.Add($"Skipping {contextLabel}: backing file '{relativePath}' is missing.");
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            warnings.Add($"Skipping {contextLabel}: backing file '{relativePath}' is missing.");
            return null;
        }
        catch (IOException ex)
        {
            warnings.Add($"Skipping {contextLabel}: could not read backing file '{relativePath}' — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Re-parses the verbatim <c>CREATE FUNCTION</c> SQL read from disk into
    /// a fresh <see cref="UdfDescriptor"/>. Handles both macro and procedural
    /// forms — the parser surfaces both as <see cref="CreateFunctionStatement"/>
    /// with either <see cref="CreateFunctionStatement.ExpressionBody"/> or
    /// <see cref="CreateFunctionStatement.StatementBody"/> populated.
    /// </summary>
    private static UdfDescriptor? TryRehydrateUdfFromSource(
        string schemaName, string name, string sourceText, UdfRegistry udfs, List<string> warnings)
    {
        Statement parsed;
        try
        {
            parsed = SqlParser.ParseStatement(sourceText);
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException || ex is FormatException)
        {
            warnings.Add($"Skipping UDF '{name}': source failed to parse — {ex.Message}");
            return null;
        }

        if (parsed is not CreateFunctionStatement create)
        {
            warnings.Add($"Skipping UDF '{name}': persisted source did not parse as a CREATE FUNCTION statement.");
            return null;
        }

        bool isProcedural = create.StatementBody is not null;
        UdfDescriptor descriptor = new(
            SchemaName: schemaName,
            Name: create.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            ExpressionBody: isProcedural ? null : create.ExpressionBody,
            ReturnIsNotNull: create.ReturnIsNotNull,
            StatementBody: create.StatementBody,
            IsPure: create.IsPure,
            SourceText: sourceText);

        // For macros, validate the body resolves against the partially-loaded
        // registry. Procedurals get reconciled later via
        // RoutineRegistrar.SyncProceduralAdaptersFromRegistry once the whole
        // registry is in place.
        if (!isProcedural && descriptor.ExpressionBody is not null)
        {
            try
            {
                UdfInliner.Inline(descriptor.ExpressionBody, udfs, new[] { "public", "system" });
            }
            catch (InvalidOperationException ex)
            {
                warnings.Add($"Skipping UDF '{name}': {ex.Message}");
                return null;
            }
        }

        return descriptor;
    }

    /// <summary>
    /// Re-parses the verbatim <c>CREATE PROCEDURE</c> SQL read from disk
    /// into a fresh <see cref="ProcedureDescriptor"/>.
    /// </summary>
    private static ProcedureDescriptor? TryRehydrateProcedureFromSource(
        string schemaName, string name, string sourceText, List<string> warnings)
    {
        Statement parsed;
        try
        {
            parsed = SqlParser.ParseStatement(sourceText);
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException)
        {
            warnings.Add($"Skipping procedure '{name}': source failed to parse — {ex.Message}");
            return null;
        }

        if (parsed is not CreateProcedureStatement create)
        {
            warnings.Add($"Skipping procedure '{name}': persisted source is not a CREATE PROCEDURE statement.");
            return null;
        }

        return new ProcedureDescriptor(
            SchemaName: schemaName,
            Name: create.Name,
            Parameters: create.Parameters,
            Body: create.Body,
            SourceText: sourceText);
    }

    /// <summary>
    /// Re-parses the verbatim <c>CREATE VIEW</c> SQL read from disk into a
    /// fresh <see cref="ViewDescriptor"/>. A view body that no longer parses
    /// (e.g. because a referenced syntax was removed) skips the view with a
    /// warning rather than aborting the catalog load.
    /// </summary>
    private static ViewDescriptor? TryRehydrateViewFromSource(
        string schemaName, string name, string sourceText, List<string> warnings)
    {
        Statement parsed;
        try
        {
            parsed = SqlParser.ParseStatement(sourceText);
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException || ex is FormatException)
        {
            warnings.Add($"Skipping view '{name}': source failed to parse — {ex.Message}");
            return null;
        }

        if (parsed is not CreateViewStatement create)
        {
            warnings.Add($"Skipping view '{name}': persisted source did not parse as a CREATE VIEW statement.");
            return null;
        }

        return new ViewDescriptor(
            SchemaName: schemaName,
            Name: create.Name,
            Body: create.Body,
            SourceText: sourceText);
    }

    /// <summary>
    /// Atomically persists the current state of <paramref name="udfs"/>,
    /// <paramref name="procedures"/>, <paramref name="models"/>, and
    /// <paramref name="views"/> to the file. Writes to a sibling
    /// <c>.tmp</c> path and renames into place so a crash never leaves a
    /// half-written file at the canonical location.
    /// </summary>
    /// <param name="udfs">UDF registry to snapshot into the <c>udfs</c> section.</param>
    /// <param name="procedures">Procedure registry to snapshot into the <c>procedures</c> section.</param>
    /// <param name="models">SQL-defined model registry. May be <see langword="null"/>
    /// when the caller has no models to persist (e.g. a test fixture
    /// stripped down to UDFs); the <c>models</c> section is then omitted
    /// from the output instead of written as an empty list.</param>
    /// <param name="views">View registry to snapshot into the <c>views</c>
    /// section. May be <see langword="null"/> for test fixtures with no
    /// views to persist.</param>
    public void Save(UdfRegistry udfs, ProcedureRegistry procedures, ModelRegistry? models, ViewRegistry? views)
    {
        lock (_writeLock)
        {
            string directory = System.IO.Path.GetDirectoryName(_path)
                ?? throw new InvalidOperationException(
                    $"CatalogStore path '{_path}' has no directory; cannot persist.");

            Directory.CreateDirectory(directory);
            SeedGitignoreIfMissing(directory);

            FlatFileBackendState? flatFileState = _flatFileStateProvider?.Invoke();

            // Project each user-authored entry to (relative-path, source-text)
            // so the per-file writer and the manifest builder see one ordered
            // snapshot. Path uses forward slashes — the manifest is platform-
            // neutral. Catalog-installed model rows have no file backing and
            // appear separately below.
            List<(string Schema, string Name, string FilePath, string SourceText)> udfFiles =
                udfs.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => (
                        e.SchemaName,
                        e.Name,
                        FilePath: UdfRelativePath(e.SchemaName, e.Name),
                        SourceText: EnsureOrReplace(e.SourceText ?? string.Empty)))
                    .ToList();

            List<(string Schema, string Name, string FilePath, string SourceText)> procFiles =
                procedures.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => (
                        e.SchemaName,
                        e.Name,
                        FilePath: ProcedureRelativePath(e.SchemaName, e.Name),
                        SourceText: EnsureOrReplace(e.SourceText)))
                    .ToList();

            List<ModelDescriptor> orderedModels = models is null
                ? new List<ModelDescriptor>()
                : models.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

            List<(string Schema, string Name, string FilePath, string SourceText)> userModelFiles =
                orderedModels
                    .Where(e => e.CatalogId is null)
                    .Select(e => (
                        e.SchemaName,
                        e.Name,
                        FilePath: ModelRelativePath(e.Name),
                        SourceText: EnsureOrReplace(e.SourceText ?? string.Empty)))
                    .ToList();

            List<(string Schema, string Name, string FilePath, string SourceText)> viewFiles = views is null
                ? new List<(string, string, string, string)>()
                : views.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => (
                        e.SchemaName,
                        e.Name,
                        FilePath: ViewRelativePath(e.SchemaName, e.Name),
                        SourceText: EnsureOrReplace(e.SourceText)))
                    .ToList();

            // Write per-file SQL before the JSON manifest — if we crash midway,
            // the manifest still points at the previous (consistent) set of
            // files until the rename below completes.
            foreach (var f in udfFiles) WriteSqlFile(directory, f.FilePath, f.SourceText);
            foreach (var f in procFiles) WriteSqlFile(directory, f.FilePath, f.SourceText);
            foreach (var f in userModelFiles) WriteSqlFile(directory, f.FilePath, f.SourceText);
            foreach (var f in viewFiles) WriteSqlFile(directory, f.FilePath, f.SourceText);

            // Reconcile orphans: anything in udfs/, procedures/, models/, or
            // views/ that isn't in the current registry snapshot gets deleted.
            // Drops and renames take effect through this pass — Save is the
            // single source of disk truth.
            ReconcileOrphans(directory, "udfs", udfFiles.Select(f => f.FilePath));
            ReconcileOrphans(directory, "procedures", procFiles.Select(f => f.FilePath));
            ReconcileOrphans(directory, "models", userModelFiles.Select(f => f.FilePath));
            ReconcileOrphans(directory, "views", viewFiles.Select(f => f.FilePath));

            CatalogFile file = new()
            {
                Version = CurrentVersion,
                Udfs = udfFiles
                    .Select(f => new CatalogFileUdfEntry
                    {
                        Schema = f.Schema,
                        Name = f.Name,
                        FilePath = f.FilePath,
                    })
                    .ToList(),
                Procedures = procFiles
                    .Select(f => new CatalogFileProcedureEntry
                    {
                        Schema = f.Schema,
                        Name = f.Name,
                        FilePath = f.FilePath,
                    })
                    .ToList(),
                Models = models?.Entries.Any() == true
                    ? orderedModels
                        .Select(e => new CatalogFileModelEntry
                        {
                            Schema = e.SchemaName,
                            Name = e.Name,
                            // Catalog-installed rows: provenance pointers only.
                            CatalogId = e.CatalogId,
                            CatalogVersion = e.CatalogVersion,
                            PinnedAs = e.PinnedAs,
                            // User-authored rows: file_path points at the .sql
                            // we just wrote. Recognised as user-authored
                            // exactly when CatalogId is null.
                            FilePath = e.CatalogId is null ? ModelRelativePath(e.Name) : null,
                        })
                        .ToList()
                    : null,
                Views = viewFiles.Count == 0
                    ? null
                    : viewFiles
                        .Select(f => new CatalogFileViewEntry
                        {
                            Schema = f.Schema,
                            Name = f.Name,
                            FilePath = f.FilePath,
                        })
                        .ToList(),
                Backends = flatFileState is null
                    ? null
                    : new CatalogFileBackends { FlatFile = flatFileState },
            };

            string json = JsonSerializer.Serialize(file, CatalogJsonContext.Default.CatalogFile);
            string tempPath = _path + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
        }
    }

    /// <summary>
    /// Catalog-relative <c>.sql</c> path for a UDF: <c>udfs/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// Forward slashes — the manifest is platform-neutral.
    /// </summary>
    internal static string UdfRelativePath(string schema, string name)
        => $"udfs/{schema}/{name}.sql";

    /// <summary>
    /// Catalog-relative <c>.sql</c> path for a procedure:
    /// <c>procedures/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// </summary>
    internal static string ProcedureRelativePath(string schema, string name)
        => $"procedures/{schema}/{name}.sql";

    /// <summary>
    /// Catalog-relative <c>.sql</c> path for a user-authored model:
    /// <c>models/&lt;name&gt;.sql</c>. Schema is fixed to <c>models</c> so it's
    /// elided from the path.
    /// </summary>
    internal static string ModelRelativePath(string name)
        => $"models/{name}.sql";

    /// <summary>
    /// Catalog-relative <c>.sql</c> path for a view:
    /// <c>views/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// </summary>
    internal static string ViewRelativePath(string schema, string name)
        => $"views/{schema}/{name}.sql";

    /// <summary>
    /// Resolves a forward-slash catalog-relative path against
    /// <paramref name="catalogDirectory"/>, atomically replacing the file's
    /// contents with <paramref name="contents"/>. The parent directory is
    /// created on demand.
    /// </summary>
    private static void WriteSqlFile(string catalogDirectory, string relativePath, string contents)
    {
        string fullPath = System.IO.Path.Combine(
            catalogDirectory,
            relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        string? dir = System.IO.Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, contents);
        File.Move(tempPath, fullPath, overwrite: true);
    }

    /// <summary>
    /// Deletes <c>.sql</c> files under <c>&lt;catalogDirectory&gt;/&lt;rootDir&gt;/</c>
    /// that aren't in <paramref name="keepRelativePaths"/>. Catches drops,
    /// renames, and stale entries in one pass. Empty schema subdirectories
    /// left behind are pruned too.
    /// </summary>
    private static void ReconcileOrphans(
        string catalogDirectory, string rootDir, IEnumerable<string> keepRelativePaths)
    {
        string rootFull = System.IO.Path.Combine(catalogDirectory, rootDir);
        if (!Directory.Exists(rootFull)) return;

        HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
        foreach (string rel in keepRelativePaths)
        {
            string full = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(catalogDirectory,
                    rel.Replace('/', System.IO.Path.DirectorySeparatorChar)));
            keep.Add(full);
        }

        foreach (string file in Directory.EnumerateFiles(rootFull, "*.sql", SearchOption.AllDirectories))
        {
            string normalized = System.IO.Path.GetFullPath(file);
            if (!keep.Contains(normalized))
            {
                try { File.Delete(file); } catch { /* best-effort */ }
            }
        }

        // Prune any now-empty subdirectories (e.g. a schema dir whose last
        // routine was dropped) but leave the root dir in place so subsequent
        // writes don't have to recreate it.
        foreach (string subdir in Directory.EnumerateDirectories(rootFull, "*", SearchOption.AllDirectories)
            .OrderByDescending(p => p.Length))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(subdir).Any())
                {
                    Directory.Delete(subdir);
                }
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Rewrites a leading <c>CREATE FUNCTION|PROCEDURE|MODEL</c> to its
    /// <c>CREATE OR REPLACE …</c> form so the persisted file is self-applying
    /// when a user pipes it back into a session. <c>OR REPLACE</c> /
    /// <c>OR ALTER</c> already present pass through unchanged.
    /// </summary>
    private static string EnsureOrReplace(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        return CreatePrefix.Replace(source, "CREATE OR REPLACE", 1);
    }

    private static readonly System.Text.RegularExpressions.Regex CreatePrefix =
        new(@"\bCREATE\b(?!\s+OR\s+(?:REPLACE|ALTER)\b)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase
            | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Writes a default <c>.gitignore</c> into the catalog directory if one
    /// doesn't already exist. Ignores rebuildable sidecar artefacts; data
    /// (<c>.datum</c>, <c>.datum-blob</c>) is left committable so users can
    /// decide whether to track row content in git or scope their ignore to
    /// <c>data/</c> themselves.
    /// </summary>
    private static void SeedGitignoreIfMissing(string catalogDirectory)
    {
        string gitignorePath = System.IO.Path.Combine(catalogDirectory, ".gitignore");
        if (File.Exists(gitignorePath)) return;

        const string contents =
            "# Heliosoph.DatumV catalog — auto-seeded. Edit freely.\n" +
            "# Rebuildable artefacts:\n" +
            "*.tmp\n" +
            "*.datum-cindex-*\n" +
            "*.datum-fts-*\n" +
            "*.datum-pkindex\n" +
            "*.datum-manifest\n";
        try
        {
            File.WriteAllText(gitignorePath, contents);
        }
        catch
        {
            // Best-effort. A failure here doesn't break catalog save; the
            // user can always add their own .gitignore.
        }
    }

}

/// <summary>
/// Wire-format root for the persisted catalog (v3). Internal so the
/// <see cref="CatalogJsonContext"/> source generator can reference it.
/// </summary>
internal sealed class CatalogFile
{
    public int Version { get; set; }
    public List<CatalogFileUdfEntry>? Udfs { get; set; }
    public List<CatalogFileProcedureEntry>? Procedures { get; set; }
    public List<CatalogFileModelEntry>? Models { get; set; }
    public List<CatalogFileViewEntry>? Views { get; set; }

    /// <summary>
    /// Per-backend persistent state. Each <see cref="ITableCatalog"/>
    /// implementation that has anything to persist owns one slot here;
    /// system / virtual schemas don't write anything (their providers
    /// are reconstructed on every startup).
    /// </summary>
    public CatalogFileBackends? Backends { get; set; }
}

/// <summary>
/// Per-backend persistent state container. Today only <c>flat_file</c>
/// is populated; a future <c>DatumDbCatalog</c> would add its own slot
/// alongside without touching the rest of the file shape.
/// </summary>
public sealed class CatalogFileBackends
{
    /// <summary>State owned by <see cref="FlatFileCatalog"/>.</summary>
    public FlatFileBackendState? FlatFile { get; set; }
}

/// <summary>
/// Persistent state for <see cref="FlatFileCatalog"/>: the set of
/// persistent <c>.datum</c>-backed tables plus their indexes and PK
/// constraint names. Only tables created via <c>CREATE TABLE</c> appear;
/// TEMP / system / virtual tables don't persist.
/// </summary>
public sealed class FlatFileBackendState
{
    /// <summary>Persistent table entries owned by this backend.</summary>
    public List<FlatFileTableEntry>? Tables { get; set; }
}

/// <summary>
/// One persistent table entry inside <see cref="FlatFileBackendState"/>.
/// </summary>
public sealed class FlatFileTableEntry
{
    /// <summary>Schema portion of the canonical name. Today always <c>public</c> until CREATE SCHEMA ships.</summary>
    public string? Schema { get; set; }

    /// <summary>Unqualified table name within the schema.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Path to the backing <c>.datum</c> file. Stored relative to the
    /// catalog directory when possible (so the catalog moves with the
    /// data); absolute when the table was created with an explicit
    /// <c>AT 'path'</c> outside the catalog tree.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// User-defined secondary indexes created via <c>CREATE INDEX</c>.
    /// <see langword="null"/> when the table has no user-defined indexes.
    /// </summary>
    public List<FlatFileIndexEntry>? Indexes { get; set; }

    /// <summary>
    /// User-supplied PRIMARY KEY constraint name from a
    /// <c>CONSTRAINT name PRIMARY KEY</c> clause. <see langword="null"/>
    /// when the user didn't supply one — the facade derives
    /// <c>&lt;table&gt;_pkey</c> at lookup time.
    /// </summary>
    public string? PrimaryKeyConstraintName { get; set; }
}

/// <summary>
/// One user-defined secondary index entry within a
/// <see cref="FlatFileTableEntry"/>. The backing
/// <c>.datum-cindex-{Name}</c> sidecar lives next to the table's
/// <c>.datum</c> file; the entry is the catalog's record of which
/// indexes should be opened at provider construction.
/// </summary>
public sealed class FlatFileIndexEntry
{
    /// <summary>The index's name (unique within the owning table).</summary>
    public string? Name { get; set; }

    /// <summary>The ordered list of column names covered by the index.</summary>
    public List<string>? Columns { get; set; }

    /// <summary>
    /// <see langword="true"/> for indexes created via <c>CREATE UNIQUE INDEX</c>.
    /// </summary>
    public bool IsUnique { get; set; }

    /// <summary>
    /// Index method string — lowercase, matches the <c>USING method</c>
    /// clause in DDL. <c>"composite"</c> for the default composite B+Tree,
    /// <c>"fulltext"</c> for the FTS inverted index.
    /// </summary>
    public string? Kind { get; set; }

    /// <summary>
    /// For full-text indexes: the analyzer name persisted at CREATE INDEX
    /// time. Used at provider-open time to look up the analyzer from
    /// <see cref="Indexing.Fts.FtsAnalyzerRegistry"/> for both query-time
    /// tokenization and incremental-insert tokenization.
    /// <see langword="null"/> for non-FTS indexes.
    /// </summary>
    public string? AnalyzerName { get; set; }
}

/// <summary>
/// One UDF entry in the persisted catalog (v7). Source text lives in the
/// <c>.sql</c> file referenced by <see cref="FilePath"/> — the manifest only
/// tracks identity + location.
/// </summary>
internal sealed class CatalogFileUdfEntry
{
    /// <summary>Schema portion of the canonical name.</summary>
    public string? Schema { get; set; }

    /// <summary>Unqualified UDF name within the schema.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Forward-slash path (catalog-relative) to the <c>CREATE OR REPLACE FUNCTION</c>
    /// <c>.sql</c> file. Conventionally <c>udfs/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// </summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// One procedure entry in the persisted catalog (v7). Source text lives in
/// the <c>.sql</c> file referenced by <see cref="FilePath"/>.
/// </summary>
internal sealed class CatalogFileProcedureEntry
{
    /// <summary>Schema portion of the canonical name.</summary>
    public string? Schema { get; set; }

    /// <summary>Unqualified procedure name within the schema.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Forward-slash path (catalog-relative) to the <c>CREATE OR REPLACE PROCEDURE</c>
    /// <c>.sql</c> file. Conventionally <c>procedures/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// </summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// One SQL-defined model entry in the persisted catalog. Two row shapes
/// share this type:
/// <list type="bullet">
///   <item><description><strong>Catalog-installed</strong>:
///   <see cref="CatalogId"/> + <see cref="CatalogVersion"/> are set;
///   <see cref="FilePath"/> is null. Rehydrate resolves the originating
///   installSql by <c>(CatalogId, CatalogVersion)</c> against the current
///   manifest and re-executes it, so edits to
///   <c>models/sql/&lt;catalog_id&gt;/&lt;catalog_version&gt;.sql</c>
///   flow through on the next process start without catalog file surgery.
///   <see cref="PinnedAs"/> is non-null when this row was installed in
///   pinned mode (coexisting with a different bare-form active version);
///   rehydrate applies the same pinned-identifier rewrite the original
///   install did.</description></item>
///   <item><description><strong>User-authored</strong>:
///   <see cref="FilePath"/> points at the on-disk <c>.sql</c>; the
///   catalog-pointer fields are null. The file is the canonical source
///   text — edits to the <c>.sql</c> flow through on the next process
///   start.</description></item>
/// </list>
/// </summary>
internal sealed class CatalogFileModelEntry
{
    /// <summary>Schema portion of the canonical name. Always <c>"models"</c> (CREATE MODEL enforces it).</summary>
    public string? Schema { get; set; }

    /// <summary>Unqualified model name within the schema.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Forward-slash path (catalog-relative) to the <c>CREATE OR REPLACE MODEL</c>
    /// <c>.sql</c> file. Conventionally <c>models/&lt;name&gt;.sql</c>. Set only
    /// for user-authored rows; catalog-installed rows leave this null and
    /// resolve their source from the on-disk installSql at rehydrate.
    /// </summary>
    public string? FilePath { get; set; }

    /// <summary>
    /// Parent catalog entry id (kebab-case, e.g. <c>"sd-turbo"</c>) when
    /// this row was registered by a catalog-driven install. Null for
    /// user-authored rows.
    /// </summary>
    public string? CatalogId { get; set; }

    /// <summary>
    /// Catalog version string the install belonged to (e.g.
    /// <c>"2026-05-29"</c>). Null when <see cref="CatalogId"/> is null.
    /// </summary>
    public string? CatalogVersion { get; set; }

    /// <summary>
    /// When non-null, the suffixed pinned-form identifier this row was
    /// registered under (e.g. <c>"foo@20260529"</c>) — meaning the row is
    /// a pinned-mode install coexisting with a different bare-form active
    /// version. Null for bare-mode installs and user-authored rows.
    /// </summary>
    public string? PinnedAs { get; set; }
}

/// <summary>
/// One view entry in the persisted catalog (v8). Source text lives in the
/// <c>.sql</c> file referenced by <see cref="FilePath"/> — the manifest only
/// tracks identity + location.
/// </summary>
internal sealed class CatalogFileViewEntry
{
    /// <summary>Schema portion of the canonical name.</summary>
    public string? Schema { get; set; }

    /// <summary>Unqualified view name within the schema.</summary>
    public string? Name { get; set; }

    /// <summary>
    /// Forward-slash path (catalog-relative) to the <c>CREATE OR REPLACE VIEW</c>
    /// <c>.sql</c> file. Conventionally <c>views/&lt;schema&gt;/&lt;name&gt;.sql</c>.
    /// </summary>
    public string? FilePath { get; set; }
}

/// <summary>
/// Decoded, validated form of a <see cref="CatalogFileModelEntry"/> after
/// <see cref="CatalogStore.Load"/>. Surfaced via
/// <see cref="CatalogStore.LoadedModelEntries"/> so
/// <see cref="TableCatalog.RehydrateModelsAsync"/> can re-execute each
/// persisted CREATE MODEL once the inference dispatcher and models
/// directory are configured.
/// </summary>
/// <param name="Schema">Schema name from the entry (always <c>"models"</c> today).</param>
/// <param name="Name">Unqualified model name.</param>
/// <param name="SourceText">
/// Verbatim CREATE MODEL SQL — populated only for user-authored rows.
/// Null for catalog-installed rows, which resolve their source from
/// the on-disk installSql via the manifest store at rehydrate time.
/// </param>
/// <param name="CatalogId">
/// Parent catalog entry id when this row was registered by a catalog
/// install; <see langword="null"/> for user-authored rows.
/// </param>
/// <param name="CatalogVersion">
/// Catalog version string the install belonged to; null when
/// <paramref name="CatalogId"/> is null.
/// </param>
/// <param name="PinnedAs">
/// Suffixed pinned-form identifier (e.g. <c>"foo@20260529"</c>) when this
/// row was installed in pinned mode; null otherwise. Drives whether
/// rehydrate applies the pinned-identifier rewrite to the resolved
/// installSql.
/// </param>
public sealed record PendingModelEntry(
    string Schema,
    string Name,
    string? SourceText,
    string? CatalogId = null,
    string? CatalogVersion = null,
    string? PinnedAs = null);

/// <summary>
/// Source-generated JSON serializer context for the catalog file. Required
/// for trimming/AOT support — reflection-based serialization is gated by the
/// project's IL trim warnings.
/// </summary>
[JsonSerializable(typeof(CatalogFile))]
[JsonSerializable(typeof(CatalogFileBackends))]
[JsonSerializable(typeof(FlatFileBackendState))]
[JsonSerializable(typeof(FlatFileTableEntry))]
[JsonSerializable(typeof(FlatFileIndexEntry))]
[JsonSerializable(typeof(CatalogFileUdfEntry))]
[JsonSerializable(typeof(CatalogFileProcedureEntry))]
[JsonSerializable(typeof(CatalogFileModelEntry))]
[JsonSerializable(typeof(CatalogFileViewEntry))]
[JsonSerializable(typeof(List<FlatFileTableEntry>))]
[JsonSerializable(typeof(List<FlatFileIndexEntry>))]
[JsonSerializable(typeof(List<CatalogFileUdfEntry>))]
[JsonSerializable(typeof(List<CatalogFileProcedureEntry>))]
[JsonSerializable(typeof(List<CatalogFileModelEntry>))]
[JsonSerializable(typeof(List<CatalogFileViewEntry>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CatalogJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Result of a <see cref="CatalogStore.Load"/> call: how many entries
/// of each kind loaded, how many were skipped, and any per-entry
/// warnings the host can surface.
/// </summary>
/// <param name="LoadedUdfs">Number of UDFs successfully registered.</param>
/// <param name="SkippedUdfs">Number of UDFs that could not be loaded.</param>
/// <param name="LoadedProcedures">Number of procedures successfully registered.</param>
/// <param name="SkippedProcedures">Number of procedures that could not be loaded.</param>
/// <param name="LoadedViews">Number of views successfully registered.</param>
/// <param name="SkippedViews">Number of views that could not be loaded.</param>
/// <param name="Warnings">
/// Human-readable reasons each skipped entry was rejected. May also
/// include version-mismatch notices.
/// </param>
public sealed record CatalogStoreLoadReport(
    int LoadedUdfs,
    int SkippedUdfs,
    int LoadedProcedures,
    int SkippedProcedures,
    int LoadedViews,
    int SkippedViews,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Thrown by <see cref="CatalogStore.Load"/> when the catalog file exists
/// but cannot be read, is structurally invalid (not parseable as JSON),
/// or is not the supported manifest version. Per-UDF errors don't throw —
/// they're collected on the load report.
/// </summary>
public sealed class CatalogStoreLoadException : Exception
{
    /// <summary>Creates a load exception without an inner cause.</summary>
    public CatalogStoreLoadException(string message)
        : base(message)
    {
    }

    /// <summary>Creates a load exception wrapping an inner cause.</summary>
    public CatalogStoreLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
