using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

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
///   "version": 3,
///   "udfs":       [{ "schema": "udf",  "name": "shout", "parameters": [...], "body_kind": "macro", "body": "upper(s)" }],
///   "procedures": [{ "schema": "proc", "name": "...", "source_text": "..." }],
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
/// is not <c>4</c> is rejected with <see cref="CatalogStoreLoadException"/>
/// — the caller must delete the catalog directory and start fresh.
/// Schema support requires v3; real schema membership for UDFs /
/// procedures requires v4.
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
    /// </list>
    /// Only v4 is accepted; older and newer versions both throw at load.
    /// </summary>
    public const int CurrentVersion = 4;

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
    /// <see cref="Load(UdfRegistry, ProcedureRegistry)"/> so the
    /// FlatFile backend can rehydrate every persistent table.
    /// </remarks>
    public FlatFileBackendState? LoadedFlatFileBackendState => _loadedFlatFileState;

    /// <summary>
    /// Loads the persisted state into <paramref name="udfs"/> and
    /// <paramref name="procedures"/>. Returns a report describing what
    /// loaded and what was skipped. Does not throw for a missing file —
    /// that's a normal first-time-startup case.
    /// </summary>
    /// <param name="udfs">The UDF registry to populate.</param>
    /// <param name="procedures">The procedure registry to populate.</param>
    /// <exception cref="CatalogStoreLoadException">
    /// The file exists but cannot be read or is not valid JSON. Individual
    /// bad entries are skipped (and reported) rather than throwing.
    /// </exception>
    public CatalogStoreLoadReport Load(UdfRegistry udfs, ProcedureRegistry procedures)
    {
        if (!File.Exists(_path))
        {
            return new CatalogStoreLoadReport(
                LoadedUdfs: 0, SkippedUdfs: 0,
                LoadedProcedures: 0, SkippedProcedures: 0,
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

        int loadedUdfs = 0;
        int skippedUdfs = 0;
        int loadedProcedures = 0;
        int skippedProcedures = 0;
        List<string> warnings = new();

        foreach (CatalogFileUdfEntry entry in parsed.Udfs ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                skippedUdfs++;
                warnings.Add("Skipping UDF entry with missing or empty 'name'.");
                continue;
            }

            UdfDescriptor? descriptor = TryRehydrate(entry, udfs, warnings);
            if (descriptor is null)
            {
                skippedUdfs++;
                continue;
            }

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

            ProcedureDescriptor? descriptor = TryRehydrateProcedure(entry, warnings);
            if (descriptor is null)
            {
                skippedProcedures++;
                continue;
            }

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

        return new CatalogStoreLoadReport(
            loadedUdfs, skippedUdfs,
            loadedProcedures, skippedProcedures,
            warnings);
    }

    /// <summary>
    /// Re-parses a persisted procedure entry's source text into a
    /// <see cref="CreateProcedureStatement"/>, extracts the body, and
    /// returns a fresh descriptor. Returns <see langword="null"/> when
    /// the entry can't be rehydrated; <paramref name="warnings"/>
    /// collects the reason.
    /// </summary>
    private static ProcedureDescriptor? TryRehydrateProcedure(
        CatalogFileProcedureEntry entry, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceText))
        {
            warnings.Add($"Skipping procedure '{entry.Name}': source text is missing.");
            return null;
        }

        Statement parsed;
        try
        {
            parsed = SqlParser.ParseStatement(entry.SourceText!);
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException)
        {
            warnings.Add(
                $"Skipping procedure '{entry.Name}': source failed to parse — {ex.Message}");
            return null;
        }

        if (parsed is not CreateProcedureStatement create)
        {
            warnings.Add(
                $"Skipping procedure '{entry.Name}': persisted source is not a CREATE PROCEDURE statement.");
            return null;
        }

        // v4: the entry carries a real schema. Use it as the descriptor's
        // SchemaName (the parser-resolved Schema on `create` is whatever
        // the user wrote at original CREATE time; the manifest is the
        // canonical record of where the procedure lives).
        return new ProcedureDescriptor(
            SchemaName: entry.Schema ?? "public",
            Name: create.Name,
            Parameters: create.Parameters,
            Body: create.Body,
            SourceText: entry.SourceText!);
    }

    /// <summary>
    /// Parses a serialised expression fragment back into an AST by wrapping
    /// it in a synthetic <c>SELECT</c> and pulling the first column —
    /// the same trick used to round-trip UDF bodies.
    /// </summary>
    private static Expression ParseExpressionFragment(string fragment)
    {
        QueryExpression q = SqlParser.Parse($"SELECT {fragment}");
        return ((SelectQueryExpression)q).Statement.Columns[0].Expression;
    }

    /// <summary>
    /// Re-parses the entry's body into an AST and validates it via the
    /// inliner against the partially-loaded registry, so cycles introduced
    /// in the file surface here. Returns <see langword="null"/> when the
    /// entry can't be rehydrated; <paramref name="warnings"/> collects the
    /// reason.
    /// </summary>
    private static UdfDescriptor? TryRehydrate(
        CatalogFileUdfEntry entry, UdfRegistry udfs, List<string> warnings)
    {
        // body_kind selects the rehydration path. The default ("macro") keeps
        // forward-compatibility with files written by binaries that predate the
        // procedural-UDF format; a missing or unrecognised kind falls back to
        // macro because that's the original (and still most common) shape.
        string kind = entry.BodyKind ?? UdfBodyKindMacro;
        return kind.Equals(UdfBodyKindProcedural, StringComparison.OrdinalIgnoreCase)
            ? TryRehydrateProcedural(entry, warnings)
            : TryRehydrateMacro(entry, udfs, warnings);
    }

    private static UdfDescriptor? TryRehydrateMacro(
        CatalogFileUdfEntry entry, UdfRegistry udfs, List<string> warnings)
    {
        if (entry.Body is null)
        {
            warnings.Add($"Skipping UDF '{entry.Name}': body is missing.");
            return null;
        }

        Expression body;
        try
        {
            // The body is parsed as the expression in a synthetic SELECT so
            // we can reuse the public parser entry point. Wrapping is safe
            // because we only consume the first column expression.
            QueryExpression q = SqlParser.Parse($"SELECT {entry.Body}");
            body = ((SelectQueryExpression)q).Statement.Columns[0].Expression;
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException)
        {
            // Tokenizer failures surface as Superpower.ParseException; parser
            // failures as DatumIngest.Parsing.ParseException. Treat both as
            // "this entry is corrupt" and continue with the remaining
            // entries instead of aborting the whole load.
            warnings.Add(
                $"Skipping UDF '{entry.Name}': body failed to parse — {ex.Message}");
            return null;
        }

        IReadOnlyList<UdfParameter>? parameters = TryRehydrateParameters(entry, warnings);
        if (parameters is null) return null;

        UdfDescriptor descriptor = new(
            SchemaName: entry.Schema ?? "public",
            Name: entry.Name!,
            Parameters: parameters,
            ReturnTypeName: entry.ReturnType,
            ExpressionBody: body,
            ReturnIsNotNull: entry.ReturnIsNotNull);

        // Validate against the partially-loaded registry. This catches
        // unresolved UDF references in the body and direct cycles. The
        // walk uses the default search_path; richer search_path-aware
        // load-time validation can come later.
        try
        {
            UdfInliner.Inline(body, udfs, new[] { "public", "system" });
        }
        catch (InvalidOperationException ex)
        {
            warnings.Add($"Skipping UDF '{entry.Name}': {ex.Message}");
            return null;
        }

        return descriptor;
    }

    /// <summary>
    /// Re-parses a persisted procedural UDF entry from its source text. The
    /// source text is the entire <c>CREATE FUNCTION</c> SQL (matching the
    /// procedure pattern), which lets the existing <see cref="SqlParser"/>
    /// reconstruct the statement body with its full validation pass — no
    /// separate Statement formatter is needed.
    /// </summary>
    private static UdfDescriptor? TryRehydrateProcedural(
        CatalogFileUdfEntry entry, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(entry.SourceText))
        {
            warnings.Add(
                $"Skipping UDF '{entry.Name}': procedural source text is missing.");
            return null;
        }

        Statement parsed;
        try
        {
            parsed = SqlParser.ParseStatement(entry.SourceText!);
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException || ex is FormatException)
        {
            warnings.Add(
                $"Skipping UDF '{entry.Name}': source failed to parse — {ex.Message}");
            return null;
        }

        if (parsed is not CreateFunctionStatement create || create.StatementBody is null)
        {
            warnings.Add(
                $"Skipping UDF '{entry.Name}': persisted source did not parse as a procedural CREATE FUNCTION statement.");
            return null;
        }

        return new UdfDescriptor(
            SchemaName: entry.Schema ?? "public",
            Name: create.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            ExpressionBody: null,
            ReturnIsNotNull: create.ReturnIsNotNull,
            StatementBody: create.StatementBody,
            IsPure: create.IsPure,
            SourceText: entry.SourceText);
    }

    /// <summary>
    /// Parses the persisted parameter list back into <see cref="UdfParameter"/>
    /// instances. Returns <see langword="null"/> on failure with a warning
    /// recorded; shared between macro and procedural rehydration.
    /// </summary>
    private static IReadOnlyList<UdfParameter>? TryRehydrateParameters(
        CatalogFileUdfEntry entry, List<string> warnings)
    {
        try
        {
            return entry.Parameters is null
                ? []
                : entry.Parameters
                    .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Type))
                    .Select(p => new UdfParameter(
                        p.Name!,
                        p.Type!,
                        p.IsNotNull,
                        p.Default is null ? null : ParseExpressionFragment(p.Default)))
                    .ToList();
        }
        catch (Exception ex) when (ex is ParseException || ex is Superpower.ParseException)
        {
            warnings.Add(
                $"Skipping UDF '{entry.Name}': default-value expression failed to parse — {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Tag stored in <see cref="CatalogFileUdfEntry.BodyKind"/> for macro UDFs
    /// (body is an inline expression). Default when the field is absent so
    /// older catalog files keep loading.
    /// </summary>
    private const string UdfBodyKindMacro = "macro";

    /// <summary>
    /// Tag stored in <see cref="CatalogFileUdfEntry.BodyKind"/> for procedural
    /// UDFs (body is a <c>BEGIN…END</c> statement sequence reparsed from
    /// <see cref="CatalogFileUdfEntry.SourceText"/>).
    /// </summary>
    private const string UdfBodyKindProcedural = "procedural";

    /// <summary>
    /// Atomically persists the current state of <paramref name="udfs"/>
    /// and <paramref name="procedures"/> to the file. Writes to a sibling
    /// <c>.tmp</c> path and renames into place so a crash never leaves a
    /// half-written file at the canonical location.
    /// </summary>
    public void Save(UdfRegistry udfs, ProcedureRegistry procedures)
    {
        // Snapshot under the write lock so a concurrent CREATE / DROP
        // doesn't observe a partial state mid-serialisation.
        CatalogFile file;
        lock (_writeLock)
        {
            FlatFileBackendState? flatFileState = _flatFileStateProvider?.Invoke();
            file = new CatalogFile
            {
                Version = CurrentVersion,
                Udfs = udfs.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new CatalogFileUdfEntry
                    {
                        // v4: real schema membership. The descriptor's
                        // SchemaName is the canonical record.
                        Schema = e.SchemaName,
                        Name = e.Name,
                        Parameters = e.Parameters
                            .Select(p => new CatalogFileUdfParameterEntry
                            {
                                Name = p.Name,
                                Type = p.TypeName,
                                IsNotNull = p.IsNotNull,
                                Default = p.Default is null
                                    ? null
                                    : QueryExplainer.FormatExpression(p.Default),
                            })
                            .ToList(),
                        ReturnType = e.ReturnTypeName,
                        ReturnIsNotNull = e.ReturnIsNotNull,
                        // Procedural UDFs persist the verbatim CREATE FUNCTION
                        // text and a kind tag — round-tripping through the
                        // parser is cheaper than carrying a Statement
                        // formatter, and mirrors how procedures already
                        // persist. Macros keep the existing per-field shape.
                        BodyKind = e.IsProcedural ? UdfBodyKindProcedural : UdfBodyKindMacro,
                        Body = e.IsProcedural ? null : QueryExplainer.FormatExpression(e.ExpressionBody!),
                        SourceText = e.IsProcedural ? e.SourceText : null,
                        IsPure = e.IsPure,
                    })
                    .ToList(),
                Procedures = procedures.Entries
                    .OrderBy(e => e.SchemaName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new CatalogFileProcedureEntry
                    {
                        // v4: real schema membership.
                        Schema = e.SchemaName,
                        Name = e.Name,
                        SourceText = e.SourceText,
                    })
                    .ToList(),
                Backends = flatFileState is null
                    ? null
                    : new CatalogFileBackends { FlatFile = flatFileState },
            };
        }

        string json = JsonSerializer.Serialize(file, CatalogJsonContext.Default.CatalogFile);

        string? directory = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = _path + ".tmp";

        // Lock the file-system mutation so two concurrent saves don't
        // overwrite each other's temp file. The single in-process lock is
        // sufficient because each TableCatalog owns its own CatalogStore.
        lock (_writeLock)
        {
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _path, overwrite: true);
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

/// <summary>One UDF entry in the persisted catalog.</summary>
internal sealed class CatalogFileUdfEntry
{
    /// <summary>
    /// Schema placeholder. v3 stores <c>"udf"</c> for every entry today;
    /// real schema membership is an S7 follow-up. Ignored on load.
    /// </summary>
    public string? Schema { get; set; }

    public string? Name { get; set; }
    public List<CatalogFileUdfParameterEntry>? Parameters { get; set; }
    public string? ReturnType { get; set; }
    public bool ReturnIsNotNull { get; set; }

    /// <summary>
    /// Macro body rendered as a SQL fragment via
    /// <see cref="QueryExplainer.FormatExpression"/>. <see langword="null"/>
    /// for procedural UDFs (their body lives in <see cref="SourceText"/>).
    /// </summary>
    public string? Body { get; set; }

    /// <summary>
    /// Body shape tag: <c>"macro"</c> for inline-expression UDFs,
    /// <c>"procedural"</c> for <c>BEGIN…END</c> bodies.
    /// </summary>
    public string? BodyKind { get; set; }

    /// <summary>
    /// Verbatim <c>CREATE FUNCTION</c> SQL for procedural UDFs. Reparsed on
    /// load to reconstruct the statement-level body. <see langword="null"/>
    /// for macros (whose body round-trips through <see cref="Body"/>).
    /// </summary>
    public string? SourceText { get; set; }

    /// <summary>Mirrors <see cref="UdfDescriptor.IsPure"/>.</summary>
    public bool IsPure { get; set; }
}

/// <summary>One declared parameter in a persisted UDF entry.</summary>
internal sealed class CatalogFileUdfParameterEntry
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool IsNotNull { get; set; }

    /// <summary>
    /// Persisted default-value expression, rendered as a SQL fragment via
    /// <see cref="QueryExplainer.FormatExpression"/>. Re-parsed on load
    /// using the same trick as <see cref="CatalogFileUdfEntry.Body"/>:
    /// wrap in a synthetic <c>SELECT</c> and pull the first column.
    /// <see langword="null"/> when the parameter has no default.
    /// </summary>
    public string? Default { get; set; }
}

/// <summary>
/// One procedure entry in the persisted catalog. Stores the original
/// CREATE PROCEDURE source verbatim so the user's formatting and
/// comments survive a round-trip.
/// </summary>
internal sealed class CatalogFileProcedureEntry
{
    /// <summary>
    /// Schema placeholder. v3 stores <c>"proc"</c> for every entry today;
    /// real schema membership is an S7 follow-up. Ignored on load.
    /// </summary>
    public string? Schema { get; set; }

    public string? Name { get; set; }
    public string? SourceText { get; set; }
}

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
[JsonSerializable(typeof(CatalogFileUdfParameterEntry))]
[JsonSerializable(typeof(CatalogFileProcedureEntry))]
[JsonSerializable(typeof(List<FlatFileTableEntry>))]
[JsonSerializable(typeof(List<FlatFileIndexEntry>))]
[JsonSerializable(typeof(List<CatalogFileUdfEntry>))]
[JsonSerializable(typeof(List<CatalogFileUdfParameterEntry>))]
[JsonSerializable(typeof(List<CatalogFileProcedureEntry>))]
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
/// <param name="Warnings">
/// Human-readable reasons each skipped entry was rejected. May also
/// include version-mismatch notices.
/// </param>
public sealed record CatalogStoreLoadReport(
    int LoadedUdfs,
    int SkippedUdfs,
    int LoadedProcedures,
    int SkippedProcedures,
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
