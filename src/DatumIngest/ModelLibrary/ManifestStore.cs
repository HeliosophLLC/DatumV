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

    // modelFamily -> resolved absolute path to the familyCardFile.
    // Populated at load time from the (validated) at-most-one mapping
    // discovered in ValidateFamilyCardFiles.
    private readonly Dictionary<string, string> _familyCardPaths;
    // modelId -> resolved absolute path to the hero image. Populated at
    // load time; entries with missing hero files log a warning at load
    // and don't get a path so ResolveHeroImagePath returns null.
    private readonly Dictionary<string, string> _heroImagePaths;
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

        ValidateModels(manifest, manifestPath, _licenses);
        Manifest = manifest;
        Vocabulary = new CatalogVocabulary(manifest);

        _familyCardPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel m in manifest.Models)
        {
            if (string.IsNullOrEmpty(m.FamilyCardFile)) { continue; }
            if (string.IsNullOrEmpty(m.ModelFamily)) { continue; }
            // ValidateFamilyCardFiles already enforced existence + per-family
            // uniqueness, so this is a straight resolve.
            _familyCardPaths[m.ModelFamily] =
                Path.GetFullPath(Path.Combine(manifestDir, m.FamilyCardFile));
        }

        _heroImagePaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CatalogModel m in manifest.Models)
        {
            if (string.IsNullOrEmpty(m.HeroImageFile)) { continue; }
            string heroPath = Path.GetFullPath(Path.Combine(manifestDir, m.HeroImageFile));
            if (File.Exists(heroPath))
            {
                _heroImagePaths[m.Id] = heroPath;
            }
            else
            {
                _logger.LogWarning(
                    "Model entry {Id} declares heroImageFile '{File}' but no file exists at {Path}",
                    m.Id, m.HeroImageFile, heroPath);
            }
        }

        _logger.LogInformation(
            "Catalog loaded: {Models} models, {Cards} family cards",
            manifest.Models.Count, _familyCardPaths.Count);
    }

    public string? GetFamilyCardMarkdown(string modelFamily)
    {
        return _familyCardPaths.TryGetValue(modelFamily, out string? path)
            ? File.ReadAllText(path)
            : null;
    }

    public string? ResolveFamilyCardAssetPath(string modelFamily, string relativePath)
    {
        if (!_familyCardPaths.TryGetValue(modelFamily, out string? cardPath))
        {
            return null;
        }
        // Assets live in a sibling directory named after the family
        // card's basename — for a card at `cards/yolox.md`, assets live
        // at `cards/yolox/`. Authors reference them in markdown as
        // `yolox/street-detections.png` and the asset-rewriter on the
        // client maps that to the served URL.
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

    public string? ResolveHeroImagePath(string modelId)
    {
        return _heroImagePaths.TryGetValue(modelId, out string? path) ? path : null;
    }

    /// <summary>
    /// Sanity-checks every entry's Kind / Python / Tasks / Summary
    /// invariants at load time so a manifest typo surfaces as one clear
    /// startup error instead of a confusing failure mid-install. Today's
    /// rules:
    /// <list type="bullet">
    /// <item><c>Kind == "python"</c> requires a non-null <see cref="CatalogPythonSpec"/>;
    /// every other kind requires it to be null.</item>
    /// <item>Python entries must declare a non-empty WorkerScript and PythonVersion.</item>
    /// <item><c>Tasks</c> must be non-empty and every entry must match a
    /// contract registered in <see cref="TaskTypeRegistry"/> (case-insensitive).</item>
    /// <item><c>Summary</c> must be a non-blank, plain-English line for
    /// the model-browser card.</item>
    /// <item><c>Hardware.Preferred</c> must name an allowed execution
    /// provider (cpu / cuda / directml / coreml / any).</item>
    /// </list>
    /// Loose validation on purpose — the registrar layer will type-check
    /// the signature kinds against the runtime <c>DataKind</c> enum
    /// later. This pass only catches the cross-field invariants.
    /// </summary>
    private static readonly HashSet<string> AllowedPreferredProviders =
        new(StringComparer.OrdinalIgnoreCase) { "cpu", "cuda", "directml", "coreml", "any" };

    private static void ValidateModels(
        CatalogManifest manifest, string manifestPath, ILicenseRegistry licenses)
    {
        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidOperationException(
                $"Catalog manifest at {manifestPath} declares schemaVersion={manifest.SchemaVersion}; " +
                "this engine build expects schemaVersion=2 (catalog substrate, npm-style versions[] arrays). " +
                "Regenerate or upgrade the manifest.");
        }

        // Tracks every declared identifier so cross-entry collisions
        // surface at load.
        Dictionary<string, CatalogModel> declaredIdentifierOwner = new(StringComparer.OrdinalIgnoreCase);

        // Tracks every materialised pinnedAs string across the whole
        // catalog so cross-entry collisions surface at load. The
        // per-version uniqueness check inside the inner loop catches
        // collisions within one cut; this catches the case where two
        // entries (or two versions of one entry) happen to materialise
        // the same `<bare>@<digits>` form. Value is the (entryId,
        // versionString, identifier) that first claimed it so the
        // diagnostic can name both colliders.
        Dictionary<string, (string EntryId, string Version, string Identifier)> globalPinnedAs =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogModel m in manifest.Models)
        {
            if (m.Versions is null || m.Versions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has an empty versions[] array. " +
                    "Every catalog entry must declare at least one version under the npm-style schema.");
            }
            foreach (string licenseId in m.LicenseIds)
            {
                if (licenses.GetMetadata(licenseId) is null)
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} references unknown license id " +
                        $"'{licenseId}'. Licenses must be declared in the central licenses/index.json.");
                }
            }
            if (m.Versions[0].Deprecated)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has versions[0] marked deprecated. " +
                    "versions[0] is the recommended cut and may not be deprecated — promote a newer version " +
                    "or remove the flag.");
            }
            HashSet<string> versionStrings = new(StringComparer.Ordinal);
            foreach (CatalogVersion v in m.Versions)
            {
                if (string.IsNullOrWhiteSpace(v.Version))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} has a version with missing/blank 'version' field.");
                }
                if (!versionStrings.Add(v.Version))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} declares duplicate version '{v.Version}'.");
                }
                if (v.Sources is null || (v.Sources.Count == 0 && !string.Equals(m.Kind, "python", StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' version '{v.Version}' in {manifestPath} has an empty sources[] array. " +
                        "Every non-python version must declare at least one source.");
                }
                if (v.Models is not null)
                {
                    HashSet<string> identifiersThisVersion = new(StringComparer.OrdinalIgnoreCase);
                    HashSet<string> pinnedThisVersion = new(StringComparer.OrdinalIgnoreCase);
                    foreach (CatalogVersionModel vm in v.Models)
                    {
                        if (string.IsNullOrWhiteSpace(vm.Identifier))
                        {
                            throw new InvalidOperationException(
                                $"Catalog entry '{m.Id}' version '{v.Version}' in {manifestPath} " +
                                "has a versions[].models entry with missing/blank identifier.");
                        }
                        if (!identifiersThisVersion.Add(vm.Identifier))
                        {
                            throw new InvalidOperationException(
                                $"Catalog entry '{m.Id}' version '{v.Version}' in {manifestPath} " +
                                $"declares identifier '{vm.Identifier}' twice in versions[].models. " +
                                "Identifiers must be unique within a single version.");
                        }
                        // Track the owner of each declared identifier so
                        // duplicate declarations across entries surface at load.
                        // Multiple versions of the same entry legitimately
                        // re-declare the same identifier, so the same-owner
                        // case isn't a duplicate.
                        if (declaredIdentifierOwner.TryGetValue(vm.Identifier, out CatalogModel? prevOwner)
                            && !ReferenceEquals(prevOwner, m))
                        {
                            throw new InvalidOperationException(
                                $"Model identifier '{vm.Identifier}' is declared by both catalog entries " +
                                $"'{prevOwner.Id}' and '{m.Id}' in {manifestPath}. Identifiers must be " +
                                "globally unique across the catalog.");
                        }
                        declaredIdentifierOwner[vm.Identifier] = m;

                        // pinnedAs validation: the suffixed identifier is the
                        // name the installer rewrites CREATE OR REPLACE MODEL
                        // to when this version is installed alongside a
                        // different active version. Must be unique within the
                        // version (alongside-version registrations would
                        // collide) and must match the parser-recognised form
                        // `<bareIdentifier>@<digits>` so `@<version>` SQL pin
                        // syntax resolves cleanly. We materialise the
                        // effective value (explicit or convention default)
                        // for both checks.
                        string effective = vm.EffectivePinnedAs(v.Version);
                        if (!IsValidPinnedAs(effective, out string? pinnedErr))
                        {
                            throw new InvalidOperationException(
                                $"Catalog entry '{m.Id}' version '{v.Version}' identifier '{vm.Identifier}' " +
                                $"in {manifestPath} has pinnedAs='{effective}' which is malformed: {pinnedErr}. " +
                                "Expected '<identifier>@<digits>'.");
                        }
                        if (!pinnedThisVersion.Add(effective))
                        {
                            throw new InvalidOperationException(
                                $"Catalog entry '{m.Id}' version '{v.Version}' in {manifestPath} " +
                                $"yields duplicate pinnedAs '{effective}'. Override one of the colliding " +
                                "identifiers' pinnedAs explicitly.");
                        }
                        if (globalPinnedAs.TryGetValue(effective, out (string EntryId, string Version, string Identifier) prior))
                        {
                            throw new InvalidOperationException(
                                $"pinnedAs '{effective}' is claimed by both " +
                                $"'{prior.EntryId}' version '{prior.Version}' identifier '{prior.Identifier}' " +
                                $"and '{m.Id}' version '{v.Version}' identifier '{vm.Identifier}' " +
                                $"in {manifestPath}. Pinned-version SQL syntax requires a globally unique " +
                                "pinnedAs; override one explicitly.");
                        }
                        globalPinnedAs[effective] = (m.Id, v.Version, vm.Identifier);
                    }
                }
            }

            if (!AllowedPreferredProviders.Contains(m.Hardware.Preferred))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has unknown hardware.preferred "
                    + $"'{m.Hardware.Preferred}'. Allowed: cpu, cuda, directml, coreml, any.");
            }
            if (string.IsNullOrWhiteSpace(m.Summary))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has summary missing or blank. "
                    + "Every catalog entry needs a plain-English summary for the model card.");
            }
            else if (m.Tasks is null || m.Tasks.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has an empty tasks array. "
                    + "Every catalog entry must declare at least one task from system.task_contracts.");
            }
            
            foreach (string task in m.Tasks)
            {
                if (TaskTypeRegistry.TryGet(task) is null)
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} declares unknown task '{task}'. "
                        + "Tasks must match a contract from TaskTypeRegistry "
                        + "(see `SELECT name FROM system.task_contracts`).");
                }
            }

            bool isPython = string.Equals(m.Kind, "python", StringComparison.OrdinalIgnoreCase);
            if (isPython && m.Python is null)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} declares kind=\"python\" but has no `python` block.");
            }
            if (!isPython && m.Python is not null)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has a `python` block but kind=\"{m.Kind}\". "
                    + "Set kind to \"python\" or remove the python section.");
            }
            if (m.Python is { } py)
            {
                if (string.IsNullOrWhiteSpace(py.WorkerScript))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} has python.workerScript missing or blank.");
                }
                if (string.IsNullOrWhiteSpace(py.PythonVersion))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} has python.pythonVersion missing or blank.");
                }
                if (string.IsNullOrWhiteSpace(py.Signature.OutputKind))
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} has python.signature.outputKind missing or blank.");
                }
            }
        }

        ValidateFamilyCardFiles(manifest, manifestPath);
    }

    /// <summary>
    /// Cross-entry validation for the <c>modelFamily</c> + <c>familyCardFile</c>
    /// pair. Rules:
    /// <list type="bullet">
    /// <item>At most one entry per family may declare <c>familyCardFile</c>
    /// — the family card is one document covering every variant.</item>
    /// <item>An entry that declares <c>familyCardFile</c> without
    /// <c>modelFamily</c> is rejected (no group to attach the card to).</item>
    /// <item>The referenced file must exist on disk under the manifest
    /// directory — broken pointers fail loudly at startup.</item>
    /// </list>
    /// </summary>
    private static void ValidateFamilyCardFiles(CatalogManifest manifest, string manifestPath)
    {
        string manifestDir = Path.GetDirectoryName(manifestPath)!;
        Dictionary<string, string> cardOwnerByFamily = new(StringComparer.OrdinalIgnoreCase);
        foreach (CatalogModel m in manifest.Models)
        {
            if (string.IsNullOrEmpty(m.FamilyCardFile)) { continue; }
            if (string.IsNullOrEmpty(m.ModelFamily))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} declares familyCardFile " +
                    "but no modelFamily. The card describes a family — set modelFamily first.");
            }
            if (cardOwnerByFamily.TryGetValue(m.ModelFamily, out string? prevOwner))
            {
                throw new InvalidOperationException(
                    $"Both '{prevOwner}' and '{m.Id}' in {manifestPath} declare familyCardFile " +
                    $"for modelFamily '{m.ModelFamily}'. At most one entry per family may own the card.");
            }
            string cardPath = Path.GetFullPath(Path.Combine(manifestDir, m.FamilyCardFile));
            if (!File.Exists(cardPath))
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} references familyCardFile " +
                    $"'{m.FamilyCardFile}' but no file exists at '{cardPath}'.");
            }
            cardOwnerByFamily[m.ModelFamily] = m.Id;
        }
    }

    // Resolution order:
    //   1. <AppContext.BaseDirectory>/models/catalog.json  (ship layout)
    //   2. walk up parent directories looking for models/catalog.json (dev layout —
    //      bin/Debug/netN.N/ is several levels below the repo root)
    // The dev-fallback path is necessary because `dotnet run` from the project
    // directory puts cwd at the project, not at the repo root, and the
    // catalog is checked in at the repo root's models/ folder. When a host
    // needs an explicit override (different ship layout, embedded resource,
    // etc.), add a property to ModelLibraryOptions and surface it here.
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

    // Validates the `<bareIdentifier>@<digits>` shape of an effective
    // pinnedAs string. Returns false + a one-line reason on failure so the
    // catalog-load error can identify the bad entry without a regex dump.
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
