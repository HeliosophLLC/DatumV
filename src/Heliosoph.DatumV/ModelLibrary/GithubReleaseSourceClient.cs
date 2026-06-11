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
// ListFilesAsync projects each name onto the download URL and HEADs it in
// parallel to populate Size from Content-Length. A failed HEAD (timeout,
// 4xx, missing header) degrades to Size: 0 and the UI shows an
// indeterminate bar for that file.
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

    // Per-probe deadline. Github.com/{owner}/{repo}/releases/download/...
    // 302s to an objects.githubusercontent.com URL; HttpClient follows
    // transparently. 10s is generous enough for the chained handshake on
    // a slow link while still capping a hung host.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public async ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct)
    {
        GithubReleaseSource gh = Cast(source);

        // HEAD each asset's redirect URL in parallel so we have a total
        // before any GET starts. Failures (HEAD refused, timeout, 4xx)
        // degrade to Size: 0, and the UI falls back to indeterminate
        // progress display for that file.
        Task<SourceFile>[] probes = new Task<SourceFile>[gh.Files.Count];
        for (int i = 0; i < gh.Files.Count; i++)
        {
            probes[i] = ProbeAsync(gh.Repo, gh.Tag, gh.Files[i], ct);
        }
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    private async Task<SourceFile> ProbeAsync(
        string repo, string tag, string name, CancellationToken ct)
    {
        string url = $"{repo}/releases/download/{tag}/{name}";
        long size = 0;
        using CancellationTokenSource probeCts = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Head, url);
            using HttpResponseMessage resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                size = resp.Content.Headers.ContentLength ?? 0;
                _logger.LogDebug("HEAD {Url} → Size={Size}", url, size);
            }
            else
            {
                _logger.LogDebug(
                    "HEAD {Url} returned {Status}; falling back to Size=0",
                    url, (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("HEAD {Url} timed out after {Timeout}; falling back to Size=0",
                url, ProbeTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "HEAD {Url} failed; falling back to Size=0", url);
        }
        return new SourceFile(Path: name, Size: size, Sha256: null);
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
