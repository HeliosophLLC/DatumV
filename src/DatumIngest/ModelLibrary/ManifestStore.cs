// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using System.Text.Json.Serialization;

using DatumIngest.Catalog.Registries;

using Microsoft.Extensions.Logging;

namespace DatumIngest.ModelLibrary;

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

    // licenseId -> resolved absolute path to the textFile. Pre-resolved at
    // load time so GetLicenseText is a plain File.ReadAllText.
    private readonly Dictionary<string, string> _licenseTextPaths;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(ILogger<ManifestStore> logger)
    {
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

        ValidateModels(manifest, manifestPath);
        Manifest = manifest;

        _licenseTextPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string id, CatalogLicense license) in manifest.Licenses)
        {
            // textFile is relative to the manifest's directory (models/).
            string textPath = Path.GetFullPath(Path.Combine(manifestDir, license.TextFile));
            if (File.Exists(textPath))
            {
                _licenseTextPaths[id] = textPath;
            }
            else
            {
                _logger.LogWarning("License {Id} textFile not found at {Path}", id, textPath);
            }
        }

        _logger.LogInformation(
            "Catalog loaded: {Models} models, {Licenses} licenses",
            manifest.Models.Count, manifest.Licenses.Count);
    }

    public string? GetLicenseText(string licenseId)
    {
        return _licenseTextPaths.TryGetValue(licenseId, out string? path)
            ? File.ReadAllText(path)
            : null;
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

    private static void ValidateModels(CatalogManifest manifest, string manifestPath)
    {
        if (manifest.SchemaVersion != 2)
        {
            throw new InvalidOperationException(
                $"Catalog manifest at {manifestPath} declares schemaVersion={manifest.SchemaVersion}; " +
                "this engine build expects schemaVersion=2 (catalog substrate, npm-style versions[] arrays). " +
                "Regenerate or upgrade the manifest.");
        }

        // Build the union of declared model identifiers across every
        // entry's versions[].models. Used by tasks.recommended
        // validation below: every recommended identifier must appear in
        // some entry's declared set.
        Dictionary<string, CatalogModel> declaredIdentifierOwner = new(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogModel m in manifest.Models)
        {
            if (m.Versions is null || m.Versions.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Catalog entry '{m.Id}' in {manifestPath} has an empty versions[] array. " +
                    "Every catalog entry must declare at least one version under the npm-style schema.");
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
                    foreach (string id in v.Models)
                    {
                        // Track the owner of each declared identifier
                        // so duplicate declarations across entries
                        // surface at load.
                        if (declaredIdentifierOwner.TryGetValue(id, out CatalogModel? prevOwner)
                            && !ReferenceEquals(prevOwner, m))
                        {
                            throw new InvalidOperationException(
                                $"Model identifier '{id}' is declared by both catalog entries '{prevOwner.Id}' " +
                                $"and '{m.Id}' in {manifestPath}. Identifiers must be globally unique across the catalog.");
                        }
                        declaredIdentifierOwner[id] = m;
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
                    + "Every catalog entry must declare at least one task from datum_catalog.tasks.");
            }
            
            foreach (string task in m.Tasks)
            {
                if (TaskTypeRegistry.TryGet(task) is null)
                {
                    throw new InvalidOperationException(
                        $"Catalog entry '{m.Id}' in {manifestPath} declares unknown task '{task}'. "
                        + "Tasks must match a contract from TaskTypeRegistry "
                        + "(see `SELECT name FROM datum_catalog.tasks`).");
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

        // tasks.recommended validation: every recommended identifier must
        // appear in some entry's declared versions[].models, the owning
        // entry must declare the corresponding task contract, and the
        // entry must not be entry-level deprecated.
        if (manifest.Tasks?.Recommended is { Count: > 0 } recs)
        {
            foreach ((string taskName, string modelIdentifier) in recs)
            {
                if (TaskTypeRegistry.TryGet(taskName) is null)
                {
                    throw new InvalidOperationException(
                        $"tasks.recommended in {manifestPath} maps task '{taskName}' (which is not a TaskTypeRegistry contract) " +
                        $"to model '{modelIdentifier}'. Drop the row or rename the task.");
                }
                if (!declaredIdentifierOwner.TryGetValue(modelIdentifier, out CatalogModel? owner))
                {
                    throw new InvalidOperationException(
                        $"tasks.recommended['{taskName}'] = '{modelIdentifier}' in {manifestPath} but no catalog entry " +
                        "declares that model identifier in any versions[].models array.");
                }
                if (owner.Deprecated)
                {
                    throw new InvalidOperationException(
                        $"tasks.recommended['{taskName}'] = '{modelIdentifier}' in {manifestPath} resolves to deprecated " +
                        $"catalog entry '{owner.Id}'. Recommend a non-deprecated alternative.");
                }
                if (!owner.Tasks.Any(t => string.Equals(t, taskName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new InvalidOperationException(
                        $"tasks.recommended['{taskName}'] = '{modelIdentifier}' in {manifestPath} resolves to catalog entry " +
                        $"'{owner.Id}' but that entry's tasks[] does not include '{taskName}'.");
                }
            }
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
}
