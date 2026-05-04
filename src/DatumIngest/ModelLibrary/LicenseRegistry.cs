// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Logging;

namespace DatumIngest.ModelLibrary;

internal sealed class LicenseRegistry : ILicenseRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IReadOnlyDictionary<string, CatalogLicense> _licenses;
    // licenseId -> resolved absolute path. Pre-resolved at load time so
    // GetText is a plain File.ReadAllText.
    private readonly IReadOnlyDictionary<string, string> _textPaths;
    private readonly ILogger<LicenseRegistry> _logger;

    public LicenseRegistry(ILogger<LicenseRegistry> logger)
    {
        _logger = logger;

        string indexPath = ResolveIndexPath();
        string indexDir = Path.GetDirectoryName(indexPath)!;

        _logger.LogInformation("Loading license registry from {Path}", indexPath);

        using FileStream stream = File.OpenRead(indexPath);
        IndexFile? parsed = JsonSerializer.Deserialize<IndexFile>(stream, JsonOptions);
        if (parsed is null)
        {
            throw new InvalidOperationException($"Failed to deserialize {indexPath}.");
        }
        if (parsed.SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"License registry at {indexPath} declares schemaVersion={parsed.SchemaVersion}; " +
                "this engine build expects schemaVersion=1.");
        }

        _licenses = parsed.Licenses;
        Dictionary<string, string> textPaths = new(StringComparer.Ordinal);
        foreach ((string id, CatalogLicense license) in parsed.Licenses)
        {
            string textPath = Path.GetFullPath(Path.Combine(indexDir, license.TextFile));
            if (File.Exists(textPath))
            {
                textPaths[id] = textPath;
            }
            else
            {
                _logger.LogWarning("License {Id} textFile not found at {Path}", id, textPath);
            }
        }
        _textPaths = textPaths;

        _logger.LogInformation(
            "License registry loaded: {Licenses} entries, {TextFiles} text files present",
            _licenses.Count, _textPaths.Count);
    }

    /// <inheritdoc/>
    public IReadOnlyDictionary<string, CatalogLicense> All => _licenses;

    /// <inheritdoc/>
    public CatalogLicense? GetMetadata(string licenseId)
        => _licenses.TryGetValue(licenseId, out CatalogLicense? meta) ? meta : null;

    /// <inheritdoc/>
    public string? GetText(string licenseId)
        => _textPaths.TryGetValue(licenseId, out string? path)
            ? File.ReadAllText(path)
            : null;

    // Resolution mirrors the catalog manifests: ship-layout
    // <baseDir>/licenses/index.json, fall back to walking parents for the
    // dev layout where bin/Debug/netN.N/ sits below the repo root.
    private static string ResolveIndexPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string shipPath = Path.Combine(baseDir, "licenses", "index.json");
        if (File.Exists(shipPath))
        {
            return shipPath;
        }

        DirectoryInfo? dir = new(baseDir);
        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "licenses", "index.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"licenses/index.json not found. Searched {shipPath} and parent directories. " +
            $"Ensure the licenses/ folder is alongside the binary or beneath the repo root.");
    }

    private sealed record IndexFile(
        int SchemaVersion,
        IReadOnlyDictionary<string, CatalogLicense> Licenses);
}
