// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using System.Text.Json.Serialization;

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
    /// Sanity-checks every entry's Kind / Python invariant at load time
    /// so a manifest typo surfaces as one clear startup error instead of
    /// a confusing failure mid-install. Today's rules:
    /// <list type="bullet">
    /// <item><c>Kind == "python"</c> requires a non-null <see cref="CatalogPythonSpec"/>;
    /// every other kind requires it to be null.</item>
    /// <item>Python entries must declare a non-empty WorkerScript and PythonVersion.</item>
    /// </list>
    /// Loose validation on purpose — the registrar layer will type-check
    /// the signature kinds against the runtime <c>DataKind</c> enum
    /// later. This pass only catches the cross-field invariants.
    /// </summary>
    private static void ValidateModels(CatalogManifest manifest, string manifestPath)
    {
        foreach (CatalogModel m in manifest.Models)
        {
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
