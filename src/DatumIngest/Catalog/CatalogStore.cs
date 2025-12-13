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
/// File format — UTF-8 JSON, written atomically (write-temp + rename) so a
/// crash mid-write never leaves a partial file:
/// </para>
/// <code>
/// {
///   "version": 1,
///   "udfs": [
///     {
///       "name": "shout",
///       "parameters": [{"name": "s", "type": "STRING"}],
///       "return_type": null,
///       "body": "upper(s)"
///     }
///   ]
/// }
/// </code>
/// <para>
/// Failure handling at load:
/// </para>
/// <list type="bullet">
///   <item><description>File not present → empty registry, no error.</description></item>
///   <item><description>File present but unreadable / malformed JSON →
///     surfaces as <see cref="CatalogStoreLoadException"/> so the host
///     can decide whether to abort or continue with a fresh catalog.
///   </description></item>
///   <item><description>Individual UDF entry that fails to re-parse, fails
///     validation, or references an unresolved name → that entry is
///     skipped with a warning collected on the load report. Other entries
///     load successfully. A single corrupt UDF doesn't take down a
///     session.
///   </description></item>
///   <item><description>Version higher than this binary supports → the file
///     is treated as opaque and the registry stays empty, so an older
///     binary doesn't crash on a newer file.
///   </description></item>
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

    /// <summary>The schema version this binary writes.</summary>
    public const int CurrentVersion = 1;

    private readonly string _path;
    private readonly object _writeLock = new();

    /// <summary>Creates a store rooted at <paramref name="path"/>.</summary>
    /// <param name="path">Absolute path to the catalog JSON file.</param>
    public CatalogStore(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
    }

    /// <summary>The absolute path to the persisted catalog file.</summary>
    public string Path => _path;

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

        // Forward-compat: a higher version means the writer is newer than
        // this reader. Refusing to crash is the friendlier behaviour — log
        // and start fresh; any subsequent save will downgrade the file.
        if (parsed.Version > CurrentVersion)
        {
            return new CatalogStoreLoadReport(
                LoadedUdfs: 0,
                SkippedUdfs: 0,
                LoadedProcedures: 0,
                SkippedProcedures: 0,
                Warnings:
                [
                    $"Catalog file '{_path}' has version {parsed.Version}; " +
                    $"this binary supports up to version {CurrentVersion}. The file is ignored.",
                ]);
        }

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

        return new ProcedureDescriptor(
            create.Name,
            create.Parameters,
            create.Body,
            entry.SourceText!);
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

        IReadOnlyList<UdfParameter> parameters = entry.Parameters is null
            ? []
            : entry.Parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.Type))
                .Select(p => new UdfParameter(p.Name!, p.Type!, p.IsNotNull))
                .ToList();

        UdfDescriptor descriptor = new(
            entry.Name!,
            parameters,
            entry.ReturnType,
            body,
            entry.ReturnIsNotNull);

        // Validate against the partially-loaded registry. This catches
        // unresolved UDF references in the body and direct cycles.
        try
        {
            UdfInliner.Inline(body, udfs);
        }
        catch (InvalidOperationException ex)
        {
            warnings.Add($"Skipping UDF '{entry.Name}': {ex.Message}");
            return null;
        }

        return descriptor;
    }

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
            file = new CatalogFile
            {
                Version = CurrentVersion,
                Udfs = udfs.Entries.Values
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new CatalogFileUdfEntry
                    {
                        Name = e.Name,
                        Parameters = e.Parameters
                            .Select(p => new CatalogFileUdfParameterEntry
                            {
                                Name = p.Name,
                                Type = p.TypeName,
                                IsNotNull = p.IsNotNull,
                            })
                            .ToList(),
                        ReturnType = e.ReturnTypeName,
                        ReturnIsNotNull = e.ReturnIsNotNull,
                        Body = QueryExplainer.FormatExpression(e.Body),
                    })
                    .ToList(),
                Procedures = procedures.Entries.Values
                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => new CatalogFileProcedureEntry
                    {
                        Name = e.Name,
                        SourceText = e.SourceText,
                    })
                    .ToList(),
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
/// Wire-format root for the persisted catalog. Internal so the
/// <see cref="CatalogJsonContext"/> source generator can reference it.
/// </summary>
internal sealed class CatalogFile
{
    public int Version { get; set; }
    public List<CatalogFileUdfEntry>? Udfs { get; set; }
    public List<CatalogFileProcedureEntry>? Procedures { get; set; }
}

/// <summary>One UDF entry in the persisted catalog.</summary>
internal sealed class CatalogFileUdfEntry
{
    public string? Name { get; set; }
    public List<CatalogFileUdfParameterEntry>? Parameters { get; set; }
    public string? ReturnType { get; set; }
    public bool ReturnIsNotNull { get; set; }
    public string? Body { get; set; }
}

/// <summary>One declared parameter in a persisted UDF entry.</summary>
internal sealed class CatalogFileUdfParameterEntry
{
    public string? Name { get; set; }
    public string? Type { get; set; }
    public bool IsNotNull { get; set; }
}

/// <summary>
/// One procedure entry in the persisted catalog. Stores the original
/// CREATE PROCEDURE source verbatim so the user's formatting and
/// comments survive a round-trip.
/// </summary>
internal sealed class CatalogFileProcedureEntry
{
    public string? Name { get; set; }
    public string? SourceText { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for the catalog file. Required
/// for trimming/AOT support — reflection-based serialization is gated by the
/// project's IL trim warnings.
/// </summary>
[JsonSerializable(typeof(CatalogFile))]
[JsonSerializable(typeof(CatalogFileUdfEntry))]
[JsonSerializable(typeof(CatalogFileUdfParameterEntry))]
[JsonSerializable(typeof(CatalogFileProcedureEntry))]
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
/// but cannot be read or is structurally invalid (not parseable as JSON).
/// Per-UDF errors don't throw — they're collected on the load report.
/// </summary>
public sealed class CatalogStoreLoadException : Exception
{
    /// <summary>Creates a load exception.</summary>
    public CatalogStoreLoadException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
