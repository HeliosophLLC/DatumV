using System.Text.Json;
using System.Text.Json.Serialization;
using DatumIngest.Web.Hosting;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.ModelLibrary;

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

    // licenseId -> resolved absolute path to the textFile. Pre-resolved at
    // load time so GetLicenseText is a plain File.ReadAllText.
    private readonly Dictionary<string, string> _licenseTextPaths;
    private readonly ILogger<ManifestStore> _logger;

    public ManifestStore(WebHostOptions options, ILogger<ManifestStore> logger)
    {
        _logger = logger;

        string manifestPath = ResolveManifestPath(options);
        string manifestDir = Path.GetDirectoryName(manifestPath)!;

        _logger.LogInformation("Loading model catalog from {Path}", manifestPath);

        using FileStream stream = File.OpenRead(manifestPath);
        CatalogManifest? manifest = JsonSerializer.Deserialize<CatalogManifest>(stream, JsonOptions);
        if (manifest is null)
        {
            throw new InvalidOperationException($"Failed to deserialize {manifestPath}.");
        }

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

    // Resolution order:
    //   1. explicit override on WebHostOptions (future-proofing â€” not wired today)
    //   2. <AppContext.BaseDirectory>/models/catalog.json  (ship layout)
    //   3. walk up parent directories looking for models/catalog.json (dev layout â€”
    //      bin/Debug/netN.N/ is several levels below the repo root)
    // The dev-fallback path is necessary because `dotnet run` from the project
    // directory puts cwd at the project, not at the repo root, and the
    // catalog is checked in at the repo root's models/ folder.
    private static string ResolveManifestPath(WebHostOptions options)
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
