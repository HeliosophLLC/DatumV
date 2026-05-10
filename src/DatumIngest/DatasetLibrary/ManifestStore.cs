// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using System.Text.Json.Serialization;

using DatumIngest.Catalog.Registries;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging;

namespace DatumIngest.DatasetLibrary;

internal sealed class ManifestStore : IManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public DatasetCatalogManifest Manifest { get; }
    public string ManifestDirectory { get; }

    // entry name -> resolved absolute path to the entry's card markdown.
    private readonly Dictionary<string, string> _entryCardPaths;
    // entry name -> resolved absolute path to the entry's hero image.
    // Entries that declared a hero whose file is missing don't get a
    // path here so ResolveHeroImagePath returns null.
    private readonly Dictionary<string, string> _heroImagePaths;
    // variant id -> (entry, variant). Built once at load so the install
    // service doesn't need to scan the entire manifest on every probe.
    private readonly Dictionary<string, (DatasetEntry, DatasetVariant)> _variantsById;
    private readonly ILicenseRegistry _licenses;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(ILicenseRegistry licenses, ILogger<ManifestStore> logger)
    {
        _licenses = licenses;
        _logger = logger;

        string manifestPath = ResolveManifestPath();
        string manifestDir = Path.GetDirectoryName(manifestPath)!;
        ManifestDirectory = manifestDir;

        _logger.LogInformation("Loading dataset catalog from {Path}", manifestPath);

        using FileStream stream = File.OpenRead(manifestPath);
        DatasetCatalogManifest? manifest = JsonSerializer.Deserialize<DatasetCatalogManifest>(stream, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException($"Failed to deserialize {manifestPath}.");
        }

        ValidateDatasets(manifest, manifestPath, _licenses);
        Manifest = manifest;

        _entryCardPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _heroImagePaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        _variantsById = new Dictionary<string, (DatasetEntry, DatasetVariant)>(StringComparer.Ordinal);
        foreach (DatasetEntry e in manifest.Datasets)
        {
            if (!string.IsNullOrEmpty(e.CardFile))
            {
                // ValidateDatasets already enforced existence.
                _entryCardPaths[e.Name] =
                    Path.GetFullPath(Path.Combine(manifestDir, e.CardFile));
            }
            if (!string.IsNullOrEmpty(e.HeroImageFile))
            {
                string heroPath = Path.GetFullPath(Path.Combine(manifestDir, e.HeroImageFile));
                if (File.Exists(heroPath))
                {
                    _heroImagePaths[e.Name] = heroPath;
                }
                else
                {
                    _logger.LogWarning(
                        "Dataset entry {Name} declares heroImageFile '{File}' but no file exists at {Path}",
                        e.Name, e.HeroImageFile, heroPath);
                }
            }
            foreach (DatasetVariant v in e.Variants)
            {
                _variantsById[v.Id] = (e, v);
            }
        }

        _logger.LogInformation(
            "Dataset catalog loaded: {Entries} entries ({Variants} variants), {Cards} cards",
            manifest.Datasets.Count, _variantsById.Count, _entryCardPaths.Count);
    }

    public string? GetEntryCardMarkdown(string entryName)
    {
        return _entryCardPaths.TryGetValue(entryName, out string? path)
            ? File.ReadAllText(path)
            : null;
    }

    public string? ResolveEntryAssetPath(string entryName, string relativePath)
    {
        if (!_entryCardPaths.TryGetValue(entryName, out string? cardPath))
        {
            return null;
        }
        // Assets live in a sibling directory named after the card file's
        // basename — for a card at `cards/coco2017.md`, assets live at
        // `cards/coco2017/`. Authors reference them in markdown as
        // `coco2017/screenshot.png` and the asset rewriter on the client
        // maps that to the served URL.
        string cardDir = Path.GetDirectoryName(cardPath)!;
        string requested = Path.GetFullPath(Path.Combine(cardDir, relativePath));
        // Path-traversal guard: resolved path must stay inside the
        // manifest directory's `cards/` subtree. ManifestDirectory has
        // no trailing separator; append one for the prefix check so
        // `<dir>cards/foo` doesn't match `<dir>cards-foo`.
        string cardsRoot = Path.GetFullPath(Path.Combine(ManifestDirectory, "cards"))
            + Path.DirectorySeparatorChar;
        if (!requested.StartsWith(cardsRoot, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        return File.Exists(requested) ? requested : null;
    }

    public string? ResolveHeroImagePath(string entryName)
    {
        return _heroImagePaths.TryGetValue(entryName, out string? path) ? path : null;
    }

    public (DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId)
    {
        return _variantsById.TryGetValue(variantId, out (DatasetEntry, DatasetVariant) pair)
            ? pair
            : null;
    }

    /// <summary>
    /// Cross-field invariants enforced at load so a manifest typo
    /// surfaces as one clear startup error instead of a confusing
    /// failure mid-install. Exposed as internal so tests can exercise
    /// the rules without writing a temp file and going through the
    /// disk-reading constructor.
    /// </summary>
    internal static void ValidateDatasets(
        DatasetCatalogManifest manifest, string manifestPath, ILicenseRegistry licenses)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"Dataset catalog manifest at {manifestPath} declares schemaVersion={manifest.SchemaVersion}; " +
                "this engine build expects schemaVersion=1. Regenerate or upgrade the manifest.");
        }

        string manifestDir = Path.GetDirectoryName(manifestPath)!;

        HashSet<string> entryNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> variantIds = new(StringComparer.OrdinalIgnoreCase);

        foreach (DatasetEntry e in manifest.Datasets)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
            {
                throw new InvalidOperationException(
                    $"Dataset entry in {manifestPath} has missing/blank name.");
            }
            if (!entryNames.Add(e.Name))
            {
                throw new InvalidOperationException(
                    $"Dataset catalog has duplicate entry name '{e.Name}' in {manifestPath}.");
            }
            if (string.IsNullOrWhiteSpace(e.Summary))
            {
                throw new InvalidOperationException(
                    $"Dataset entry '{e.Name}' in {manifestPath} has summary missing or blank. "
                    + "Every entry needs a plain-English summary for the list row + detail card.");
            }
            if (e.Modalities is null || e.Modalities.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Dataset entry '{e.Name}' in {manifestPath} has an empty modalities[] array. "
                    + "Every entry must declare at least one modality (Image / Text / Audio / "
                    + "Video / Tabular / 3D / Geospatial / Document / TimeSeries).");
            }
            foreach (string modality in e.Modalities)
            {
                if (!ModalityRegistry.IsKnown(modality))
                {
                    throw new InvalidOperationException(
                        $"Dataset entry '{e.Name}' in {manifestPath} declares unknown modality "
                        + $"'{modality}'. Allowed: {string.Join(", ", ModalityRegistry.Canonical)}.");
                }
            }
            if (e.SuitableForTasks is { } tasks)
            {
                foreach (string task in tasks)
                {
                    if (TaskTypeRegistry.TryGet(task) is null)
                    {
                        throw new InvalidOperationException(
                            $"Dataset entry '{e.Name}' in {manifestPath} declares unknown "
                            + $"suitableForTask '{task}'. Tasks must match a contract from "
                            + "TaskTypeRegistry (see `SELECT name FROM system.task_contracts`).");
                    }
                }
            }
            foreach (string licenseId in e.LicenseIds)
            {
                if (licenses.GetMetadata(licenseId) is null)
                {
                    throw new InvalidOperationException(
                        $"Dataset entry '{e.Name}' in {manifestPath} references unknown license id " +
                        $"'{licenseId}'. Licenses must be declared in the central licenses/index.json.");
                }
            }
            if (!string.IsNullOrEmpty(e.CardFile))
            {
                string cardPath = Path.GetFullPath(Path.Combine(manifestDir, e.CardFile));
                if (!File.Exists(cardPath))
                {
                    throw new InvalidOperationException(
                        $"Dataset entry '{e.Name}' in {manifestPath} references cardFile '{e.CardFile}' " +
                        $"but no file exists at '{cardPath}'.");
                }
            }
            if (!IsValidSqlIdentifier(e.Schema))
            {
                throw new InvalidOperationException(
                    $"Dataset entry '{e.Name}' in {manifestPath} declares schema '{e.Schema}', " +
                    "which is not a valid SQL identifier. Use a single snake_case / lowercase " +
                    "name (default 'datasets'); the engine binds installed variants into this " +
                    "schema as `<schema>.<variantId>`.");
            }
            if (e.Variants is null || e.Variants.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Dataset entry '{e.Name}' in {manifestPath} has an empty variants[] array. " +
                    "Every entry must declare at least one variant.");
            }
            foreach (DatasetVariant v in e.Variants)
            {
                ValidateVariant(e, v, manifestPath, variantIds);
            }
        }
    }

    private static void ValidateVariant(
        DatasetEntry entry,
        DatasetVariant variant,
        string manifestPath,
        HashSet<string> variantIdsAcrossManifest)
    {
        if (string.IsNullOrWhiteSpace(variant.Id))
        {
            throw new InvalidOperationException(
                $"Dataset entry '{entry.Name}' in {manifestPath} has a variant with missing/blank id.");
        }
        if (!IsValidSqlIdentifier(variant.Id))
        {
            throw new InvalidOperationException(
                $"Dataset variant id '{variant.Id}' (in entry '{entry.Name}', {manifestPath}) " +
                "is not a valid SQL identifier. Variant ids double as table names in the bound " +
                "schema, so they must be snake_case / alphanumeric + underscore (no hyphens, " +
                "no leading digit). Rename in catalog.json.");
        }
        if (!variantIdsAcrossManifest.Add(variant.Id))
        {
            throw new InvalidOperationException(
                $"Dataset variant id '{variant.Id}' (in entry '{entry.Name}', {manifestPath}) " +
                "is declared more than once. Variant ids must be unique across the manifest — " +
                "they're the install handle and the SQL-resolvable name.");
        }
        if (string.IsNullOrWhiteSpace(variant.DisplayName))
        {
            throw new InvalidOperationException(
                $"Dataset variant '{variant.Id}' (entry '{entry.Name}', {manifestPath}) has " +
                "displayName missing or blank — that's the subtitle shown on the variant tab.");
        }
        if (variant.Versions is null || variant.Versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Dataset variant '{variant.Id}' (entry '{entry.Name}', {manifestPath}) has an " +
                "empty versions[] array. Every variant must declare at least one version.");
        }
        if (variant.Versions[0].Deprecated)
        {
            throw new InvalidOperationException(
                $"Dataset variant '{variant.Id}' (entry '{entry.Name}', {manifestPath}) has " +
                "versions[0] marked deprecated. versions[0] is the recommended cut — promote a " +
                "newer version or remove the flag.");
        }
        HashSet<string> versionStrings = new(StringComparer.Ordinal);
        foreach (CatalogDatasetVersion v in variant.Versions)
        {
            if (string.IsNullOrWhiteSpace(v.Version))
            {
                throw new InvalidOperationException(
                    $"Dataset variant '{variant.Id}' in {manifestPath} has a version with " +
                    "missing/blank 'version' field.");
            }
            if (!versionStrings.Add(v.Version))
            {
                throw new InvalidOperationException(
                    $"Dataset variant '{variant.Id}' in {manifestPath} declares duplicate " +
                    $"version '{v.Version}'.");
            }
            if (v.Sources is null || v.Sources.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Dataset variant '{variant.Id}' version '{v.Version}' in {manifestPath} has " +
                    "an empty sources[] array. Every version must declare at least one source.");
            }
            if (v.Ingest is null || v.Ingest.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Dataset variant '{variant.Id}' version '{v.Version}' in {manifestPath} has " +
                    "an empty ingest[] array. Every version must declare at least one ingest job.");
            }
            HashSet<string> tableNames = new(StringComparer.OrdinalIgnoreCase);
            foreach (CatalogIngestJob job in v.Ingest)
            {
                if (string.IsNullOrWhiteSpace(job.TableName))
                {
                    throw new InvalidOperationException(
                        $"Dataset variant '{variant.Id}' version '{v.Version}' in {manifestPath} " +
                        "has an ingest job with missing/blank tableName.");
                }
                bool hasDirect = !string.IsNullOrWhiteSpace(job.SourcePath);
                bool hasSql = !string.IsNullOrWhiteSpace(job.SqlFile);
                if (hasDirect == hasSql)
                {
                    throw new InvalidOperationException(
                        $"Dataset variant '{variant.Id}' version '{v.Version}' ingest job " +
                        $"'{job.TableName}' in {manifestPath} must set exactly one of " +
                        "`sourcePath` (direct ingest) or `sqlFile` (SQL ingest); " +
                        (hasDirect ? "both are set." : "neither is set."));
                }
                if (hasSql && string.IsNullOrWhiteSpace(job.Archive))
                {
                    throw new InvalidOperationException(
                        $"Dataset variant '{variant.Id}' version '{v.Version}' ingest job " +
                        $"'{job.TableName}' in {manifestPath} declares `sqlFile` but no " +
                        "`archive` field. SQL ingests need an archive name so the install " +
                        "pipeline can bind $archive + $archive_stem.");
                }
                if (!tableNames.Add(job.TableName))
                {
                    throw new InvalidOperationException(
                        $"Dataset variant '{variant.Id}' version '{v.Version}' in {manifestPath} " +
                        $"declares duplicate tableName '{job.TableName}' in ingest[]. Table names " +
                        "must be unique within a single version.");
                }
            }
        }
    }

    // Valid SQL identifier: starts with [a-z_], rest is [a-z0-9_]. Kept
    // strict (lowercase only) so manifest authors can't accidentally
    // emit camelCase ids that quote differently across SQL dialects.
    // No reserved-word check — the catalog rejects collisions at bind
    // time if a variant id ever matches a keyword.
    private static bool IsValidSqlIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        char first = s[0];
        if (!((first >= 'a' && first <= 'z') || first == '_')) return false;
        for (int i = 1; i < s.Length; i++)
        {
            char c = s[i];
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_'))
            {
                return false;
            }
        }
        return true;
    }

    // Resolution order:
    //   1. <AppContext.BaseDirectory>/datasets/catalog.json (ship layout)
    //   2. walk up parent directories looking for datasets/catalog.json
    //      (dev layout — bin/Debug/netN.N/ is several levels below the
    //      repo root)
    private static string ResolveManifestPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string shipPath = Path.Combine(baseDir, "datasets", "catalog.json");
        if (File.Exists(shipPath))
        {
            return shipPath;
        }

        DirectoryInfo? dir = new(baseDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "datasets", "catalog.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"datasets/catalog.json not found. Searched {shipPath} and parent directories. " +
            $"Ensure the datasets/ folder is alongside the binary or beneath the repo root.");
    }
}
