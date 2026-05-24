// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using System.Text.Json.Serialization;

using Heliosoph.DatumV.Catalog.Registries;

using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

internal sealed class ManifestStore : IManifestStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public CatalogManifest Manifest { get; }
    public string ManifestDirectory { get; }
    public ICatalogVocabulary Vocabulary { get; }

    // entryName -> absolute path to its cardFile. Populated at load time
    // from entries that declare cardFile + the file exists on disk.
    private readonly Dictionary<string, string> _entryCardPaths;
    // entryName -> absolute path to its hero image. Same pattern.
    private readonly Dictionary<string, string> _heroImagePaths;
    // variantId -> (entry, variant) lookup index built at load time. Read-
    // mostly, so a plain Dictionary suffices.
    private readonly Dictionary<string, (CatalogEntry Entry, CatalogVariant Variant)> _variantIndex;
    private readonly ILicenseRegistry _licenses;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(ILicenseRegistry licenses, ILogger<ManifestStore> logger)
    {
        _licenses = licenses;
        _logger = logger;

        string manifestPath = ResolveManifestPath();
        string manifestDir = Path.GetDirectoryName(manifestPath)!;
        ManifestDirectory = manifestDir;

        _logger.LogInformation("Loading model catalog from {Path}", manifestPath);

        using FileStream stream = File.OpenRead(manifestPath);
        CatalogManifest? manifest = JsonSerializer.Deserialize<CatalogManifest>(stream, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException($"Failed to deserialize {manifestPath}.");
        }

        ValidateEntries(manifest, manifestPath, _licenses);
        Manifest = manifest;
        Vocabulary = new CatalogVocabulary(manifest);

        _variantIndex = new Dictionary<string, (CatalogEntry, CatalogVariant)>(StringComparer.Ordinal);
        foreach (CatalogEntry e in manifest.Entries)
        {
            foreach (CatalogVariant v in e.Variants)
            {
                _variantIndex[v.Id] = (e, v);
            }
        }

        _entryCardPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogEntry e in manifest.Entries)
        {
            if (string.IsNullOrEmpty(e.CardFile)) { continue; }
            // ValidateEntries already enforced file existence so this is a
            // straight resolve.
            _entryCardPaths[e.Name] =
                Path.GetFullPath(Path.Combine(manifestDir, e.CardFile));
        }

        _heroImagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CatalogEntry e in manifest.Entries)
        {
            if (string.IsNullOrEmpty(e.HeroImageFile)) { continue; }
            string heroPath = Path.GetFullPath(Path.Combine(manifestDir, e.HeroImageFile));
            if (File.Exists(heroPath))
            {
                _heroImagePaths[e.Name] = heroPath;
            }
            else
            {
                _logger.LogWarning(
                    "Catalog entry '{Name}' declares heroImageFile '{File}' but no file exists at {Path}",
                    e.Name, e.HeroImageFile, heroPath);
            }
        }

        int variantCount = 0;
        foreach (CatalogEntry e in manifest.Entries) { variantCount += e.Variants.Count; }
        _logger.LogInformation(
            "Catalog loaded: {Entries} entries / {Variants} variants, {Cards} entry cards",
            manifest.Entries.Count, variantCount, _entryCardPaths.Count);
    }

    public (CatalogEntry Entry, CatalogVariant Variant)? TryResolveVariant(string variantId)
    {
        return _variantIndex.TryGetValue(variantId, out (CatalogEntry, CatalogVariant) hit) ? hit : null;
    }

    public string? GetEntryCardMarkdown(string entryName)
    {
        return _entryCardPaths.TryGetValue(entryName, out string? path)
            ? File.ReadAllText(path)
            : null;
    }

    public string? ResolveEntryCardAssetPath(string entryName, string relativePath)
    {
        if (!_entryCardPaths.TryGetValue(entryName, out string? cardPath))
        {
            return null;
        }
        // Assets live in a sibling directory named after the card's
        // basename — for a card at `cards/yolox/index.md`, assets live
        // at `cards/yolox/`. Authors reference them in markdown as
        // `street-detections.png` and the asset-rewriter on the client
        // maps that to the served URL.
        string cardDir = Path.GetDirectoryName(cardPath)!;
        string requested = Path.GetFullPath(Path.Combine(cardDir, relativePath));
        // Path-traversal guard: resolved path must stay inside the
        // manifest directory's `cards/` subtree.
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

    private static readonly HashSet<string> AllowedPreferredProviders =
        new(StringComparer.OrdinalIgnoreCase) { "cpu", "cuda", "directml", "coreml", "any" };

    private static void ValidateEntries(
        CatalogManifest manifest, string manifestPath, ILicenseRegistry licenses)
    {
        if (manifest.SchemaVersion != 3)
        {
            throw new InvalidOperationException(
                $"Catalog manifest at {manifestPath} declares schemaVersion={manifest.SchemaVersion}; " +
                "this engine build expects schemaVersion=3 (entry/variant hierarchy). " +
                "Regenerate or upgrade the manifest.");
        }

        // Tracks every declared identifier so cross-variant collisions
        // surface at load.
        Dictionary<string, CatalogVariant> declaredIdentifierOwner =
            new(StringComparer.OrdinalIgnoreCase);

        // Tracks every materialised pinnedAs string across the whole
        // catalog so cross-variant collisions surface at load.
        Dictionary<string, (string VariantId, string Version, string Identifier)> globalPinnedAs =
            new(StringComparer.OrdinalIgnoreCase);

        // Track entry names and variant ids for global uniqueness.
        HashSet<string> entryNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> variantIds = new(StringComparer.Ordinal);

        string manifestDir = Path.GetDirectoryName(manifestPath)!;

        foreach (CatalogEntry e in manifest.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name))
            {
                throw new InvalidOperationException(
                    $"Catalog entry in {manifestPath} has missing/blank 'name' field.");
            }
            if (!entryNames.Add(e.Name))
            {
                throw new InvalidOperationException(
                    $"Catalog entry name '{e.Name}' is declared twice in {manifestPath}. " +
                    "Entry names must be unique.");
            }
            if (string.IsNullOrWhiteSpace(e.Summary))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{e.Name}' in {manifestPath} has summary missing or blank. " +
                    "Every catalog entry needs a plain-English summary for the model card.");
            }
            if (e.Tasks is null || e.Tasks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{e.Name}' in {manifestPath} has an empty tasks array. " +
                    "Every catalog entry must declare at least one task from system.task_contracts.");
            }
            foreach (string task in e.Tasks)
            {
                if (TaskTypeRegistry.TryGet(task) is null)
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{e.Name}' in {manifestPath} declares unknown task '{task}'. " +
                        "Tasks must match a contract from TaskTypeRegistry " +
                        "(see `SELECT name FROM system.task_contracts`).");
                }
            }
            foreach (string licenseId in e.LicenseIds)
            {
                if (licenses.GetMetadata(licenseId) is null)
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{e.Name}' in {manifestPath} references unknown license id " +
                        $"'{licenseId}'. Licenses must be declared in the central licenses/index.json.");
                }
            }
            if (!string.IsNullOrEmpty(e.CardFile))
            {
                string cardPath = Path.GetFullPath(Path.Combine(manifestDir, e.CardFile));
                if (!File.Exists(cardPath))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{e.Name}' in {manifestPath} references cardFile " +
                        $"'{e.CardFile}' but no file exists at '{cardPath}'.");
                }
            }
            if (e.Variants is null || e.Variants.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{e.Name}' in {manifestPath} declares an empty variants[] array. " +
                    "Every entry must have at least one variant.");
            }

            foreach (CatalogVariant v in e.Variants)
            {
                ValidateVariant(
                    v, e, manifestPath,
                    variantIds, declaredIdentifierOwner, globalPinnedAs);
            }
        }
    }

    private static void ValidateVariant(
        CatalogVariant v,
        CatalogEntry parent,
        string manifestPath,
        HashSet<string> variantIds,
        Dictionary<string, CatalogVariant> declaredIdentifierOwner,
        Dictionary<string, (string VariantId, string Version, string Identifier)> globalPinnedAs)
    {
        if (string.IsNullOrWhiteSpace(v.Id))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{parent.Name}' in {manifestPath} has a variant with missing/blank id.");
        }
        if (!variantIds.Add(v.Id))
        {
            throw new InvalidOperationException(
                $"Variant id '{v.Id}' (entry '{parent.Name}') is declared twice in {manifestPath}. " +
                "Variant ids must be globally unique.");
        }
        if (!AllowedPreferredProviders.Contains(v.Hardware.Preferred))
        {
            throw new InvalidOperationException(
                $"Variant '{v.Id}' in {manifestPath} has unknown hardware.preferred " +
                $"'{v.Hardware.Preferred}'. Allowed: cpu, cuda, directml, coreml, any.");
        }
        if (v.Versions is null || v.Versions.Count == 0)
        {
            throw new InvalidOperationException(
                $"Variant '{v.Id}' in {manifestPath} has an empty versions[] array. " +
                "Every variant must declare at least one version.");
        }
        if (v.Versions[0].Deprecated)
        {
            throw new InvalidOperationException(
                $"Variant '{v.Id}' in {manifestPath} has versions[0] marked deprecated. " +
                "versions[0] is the recommended cut and may not be deprecated — promote a newer version " +
                "or remove the flag.");
        }

        bool isPython = string.Equals(v.Kind, "python", StringComparison.OrdinalIgnoreCase);
        if (isPython && v.Python is null)
        {
            throw new InvalidOperationException(
                $"Variant '{v.Id}' in {manifestPath} declares kind=\"python\" but has no `python` block.");
        }
        if (!isPython && v.Python is not null)
        {
            throw new InvalidOperationException(
                $"Variant '{v.Id}' in {manifestPath} has a `python` block but kind=\"{v.Kind}\". " +
                "Set kind to \"python\" or remove the python section.");
        }
        if (v.Python is { } py)
        {
            if (string.IsNullOrWhiteSpace(py.WorkerScript))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' in {manifestPath} has python.workerScript missing or blank.");
            }
            if (string.IsNullOrWhiteSpace(py.PythonVersion))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' in {manifestPath} has python.pythonVersion missing or blank.");
            }
            if (string.IsNullOrWhiteSpace(py.Signature.OutputKind))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' in {manifestPath} has python.signature.outputKind missing or blank.");
            }
        }

        HashSet<string> versionStrings = new(StringComparer.Ordinal);
        foreach (CatalogVersion ver in v.Versions)
        {
            if (string.IsNullOrWhiteSpace(ver.Version))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' in {manifestPath} has a version with missing/blank 'version' field.");
            }
            if (!versionStrings.Add(ver.Version))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' in {manifestPath} declares duplicate version '{ver.Version}'.");
            }
            if (ver.Sources is null || (ver.Sources.Count == 0 && !isPython))
            {
                throw new InvalidOperationException(
                    $"Variant '{v.Id}' version '{ver.Version}' in {manifestPath} has an empty sources[] array. " +
                    "Every non-python version must declare at least one source.");
            }
            if (ver.Models is not null)
            {
                HashSet<string> identifiersThisVersion = new(StringComparer.OrdinalIgnoreCase);
                HashSet<string> pinnedThisVersion = new(StringComparer.OrdinalIgnoreCase);
                foreach (CatalogVersionModel vm in ver.Models)
                {
                    if (string.IsNullOrWhiteSpace(vm.Identifier))
                    {
                        throw new InvalidOperationException(
                            $"Variant '{v.Id}' version '{ver.Version}' in {manifestPath} " +
                            "has a versions[].models entry with missing/blank identifier.");
                    }
                    if (!identifiersThisVersion.Add(vm.Identifier))
                    {
                        throw new InvalidOperationException(
                            $"Variant '{v.Id}' version '{ver.Version}' in {manifestPath} " +
                            $"declares identifier '{vm.Identifier}' twice in versions[].models. " +
                            "Identifiers must be unique within a single version.");
                    }
                    if (declaredIdentifierOwner.TryGetValue(vm.Identifier, out CatalogVariant? prevOwner)
                        && !ReferenceEquals(prevOwner, v))
                    {
                        throw new InvalidOperationException(
                            $"Model identifier '{vm.Identifier}' is declared by both variants " +
                            $"'{prevOwner.Id}' and '{v.Id}' in {manifestPath}. Identifiers must be " +
                            "globally unique across the catalog.");
                    }
                    declaredIdentifierOwner[vm.Identifier] = v;

                    string effective = vm.EffectivePinnedAs(ver.Version);
                    if (!IsValidPinnedAs(effective, out string? pinnedErr))
                    {
                        throw new InvalidOperationException(
                            $"Variant '{v.Id}' version '{ver.Version}' identifier '{vm.Identifier}' " +
                            $"in {manifestPath} has pinnedAs='{effective}' which is malformed: {pinnedErr}. " +
                            "Expected '<identifier>@<digits>'.");
                    }
                    if (!pinnedThisVersion.Add(effective))
                    {
                        throw new InvalidOperationException(
                            $"Variant '{v.Id}' version '{ver.Version}' in {manifestPath} " +
                            $"yields duplicate pinnedAs '{effective}'. Override one of the colliding " +
                            "identifiers' pinnedAs explicitly.");
                    }
                    if (globalPinnedAs.TryGetValue(effective, out (string VariantId, string Version, string Identifier) prior))
                    {
                        throw new InvalidOperationException(
                            $"pinnedAs '{effective}' is claimed by both " +
                            $"'{prior.VariantId}' version '{prior.Version}' identifier '{prior.Identifier}' " +
                            $"and '{v.Id}' version '{ver.Version}' identifier '{vm.Identifier}' " +
                            $"in {manifestPath}. Pinned-version SQL syntax requires a globally unique " +
                            "pinnedAs; override one explicitly.");
                    }
                    globalPinnedAs[effective] = (v.Id, ver.Version, vm.Identifier);
                }
            }
        }
    }

    private static string ResolveManifestPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string shipPath = Path.Combine(baseDir, "models", "catalog.json");
        if (File.Exists(shipPath))
        {
            return shipPath;
        }

        DirectoryInfo? dir = new(baseDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "models", "catalog.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"models/catalog.json not found. Searched {shipPath} and parent directories. " +
            $"Ensure the models/ folder is alongside the binary or beneath the repo root.");
    }

    private static bool IsValidPinnedAs(string value, out string? error)
    {
        int at = value.IndexOf('@');
        if (at <= 0)
        {
            error = "missing '@' separator (or empty identifier before it)";
            return false;
        }
        if (at == value.Length - 1)
        {
            error = "no version digits after '@'";
            return false;
        }
        for (int i = at + 1; i < value.Length; i++)
        {
            char c = value[i];
            if (c < '0' || c > '9')
            {
                error = $"non-digit '{c}' after '@'; SQL pin syntax accepts only [0-9]+";
                return false;
            }
        }
        error = null;
        return true;
    }
}
