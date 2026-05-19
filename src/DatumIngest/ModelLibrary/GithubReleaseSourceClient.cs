// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

// IModelSourceClient implementation for GitHub release assets. URL shape:
//   https://github.com/{owner}/{name}/releases/download/{tag}/{file}
//
// GitHub's release-download endpoint serves a 302 to a github-objects.com
// presigned URL — HttpClient follows redirects transparently. No tree/list
// API call: the catalog entry's `Files` field IS the inventory, so
// ListFilesAsync just projects each name onto the download URL and reports
// size as 0 (we don't pre-flight a HEAD here — the size only matters for
// progress display, and we surface it once Content-Length comes back on
// the GET).
//
// No hash verification beyond HTTPS — GitHub releases don't expose a
// per-asset checksum API. Catalog-side per-file sha256 declarations are a
// v2 follow-up; for v1 the rule is "trust HTTPS, verify locally only when
// the source itself surfaced a hash."
//
// Resume semantics: same Range-based protocol as HuggingFaceSourceClient,
// minus the LFS / hash-seeding complexity (no expected upstream hash so
// we don't bother seeding; on resume we just re-hash the .part bytes
// before continuing).
internal sealed class GithubReleaseSourceClient : IModelSourceClient
{
    public string SupportedType => "github-release";

    private readonly HttpClient _http;
    private readonly ILogger<GithubReleaseSourceClient> _logger;

    public GithubReleaseSourceClient(HttpClient http, ILogger<GithubReleaseSourceClient> logger)
    {
        _http = http;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://github.com/");
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Heliosoph.DatumV/0.1 (+https://github.com/Heliosoph.DatumV)");
    }

    public ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct)
    {
        GithubReleaseSource gh = Cast(source);

        // No tree call — the manifest's Files list is the inventory.
        // Size is unknown until we hit the GET; report 0 here. Progress
        // events will populate the real total once Content-Length lands.
        // Sha256 is null because GitHub releases don't surface checksums.
        List<SourceFile> files = new(gh.Files.Count);
        foreach (string name in gh.Files)
        {
            files.Add(new SourceFile(Path: name, Size: 0, Sha256: null));
        }
        return ValueTask.FromResult<IReadOnlyList<SourceFile>>(files);
    }

    public async ValueTask<string> DownloadFileAsync(
        CatalogSource source,
        SourceFile file,
        string destPath,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken ct)
    {
        GithubReleaseSource gh = Cast(source);

        string url = $"{gh.Repo}/releases/download/{gh.Tag}/{file.Path}";
        _logger.LogDebug("GitHub release fetch: {Url}", url);

        return await HttpFileDownloader.DownloadAsync(
            _http, url, destPath, progress, _logger, ct).ConfigureAwait(false);
    }

    private static GithubReleaseSource Cast(CatalogSource source) =>
        source as GithubReleaseSource
            ?? throw new InvalidOperationException(
                $"GithubReleaseSourceClient cannot handle {source.GetType().Name}; " +
                "the source registry routed this entry to the wrong client.");
}
