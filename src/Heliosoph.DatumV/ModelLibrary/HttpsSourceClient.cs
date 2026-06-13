// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

// IModelSourceClient for ad-hoc HTTPS URLs. The escape hatch for sources
// that don't fit HuggingFace's repo+revision or GitHub's release-asset
// shape — typical real-world cases:
//   - Qualcomm AI Hub assets (per-release S3 URLs)
//   - Custom CDN or vendor-published download links
//   - Direct file mirrors hosted by a research group
//
// No tree call: the manifest's Urls list IS the inventory. Each HttpsFile
// declares both the source URL and the destination filename inside the
// model directory so two upstream URLs can land at different local names
// without collision (uncommon, but supported).
//
// No hash verification beyond HTTPS — same caveat as github-release. Use
// a sibling Heliosoph.DatumV HuggingFace source ahead of this in the Sources list
// when you want strict-sha reproducibility.
internal sealed class HttpsSourceClient : IModelSourceClient
{
    public string SupportedType => "https";

    private readonly HttpClient _http;
    private readonly ILogger<HttpsSourceClient> _logger;

    public HttpsSourceClient(HttpClient http, ILogger<HttpsSourceClient> logger)
    {
        _http = http;
        _logger = logger;

        // No BaseAddress — each URL is absolute. Just stamp the UA so
        // hosts that filter on it can see our requests.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Heliosoph.DatumV/0.1 (+https://github.com/Heliosoph.DatumV)");
    }

    public async ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct)
    {
        HttpsSource https = Cast(source);

        // HEAD each URL in parallel to learn Content-Length up front so
        // downstream progress reporting can render a determinate bar +
        // ETA. Hosts that refuse HEAD, omit Content-Length on chunked
        // responses, or 403 on a HEAD-only request yield Size: 0 and the
        // UI falls back to indeterminate display.
        Task<SourceFile>[] probes = new Task<SourceFile>[https.Urls.Count];
        for (int i = 0; i < https.Urls.Count; i++)
        {
            probes[i] = ProbeAsync(https.Urls[i], https.UserAgent, ct);
        }
        return await Task.WhenAll(probes).ConfigureAwait(false);
    }

    // Per-probe deadline, independent of the install's outer CT. A host
    // that opens a TCP connection but never responds to HEAD would
    // otherwise block the install for the HttpClient default (100s) before
    // any byte moves. Tight enough to feel snappy on the install path,
    // loose enough to ride out a slow TLS handshake on a transcontinental
    // link.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    private async Task<SourceFile> ProbeAsync(
        HttpsFile entry, string? userAgentOverride, CancellationToken ct)
    {
        long size = 0;
        using CancellationTokenSource probeCts = CancellationTokenSource
            .CreateLinkedTokenSource(ct);
        probeCts.CancelAfter(ProbeTimeout);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Head, entry.Url);
            if (!string.IsNullOrEmpty(userAgentOverride))
            {
                req.Headers.UserAgent.Clear();
                req.Headers.UserAgent.ParseAdd(userAgentOverride);
            }
            using HttpResponseMessage resp = await _http
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, probeCts.Token)
                .ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                size = resp.Content.Headers.ContentLength ?? 0;
                _logger.LogDebug("HEAD {Url} → Size={Size}", entry.Url, size);
            }
            else
            {
                _logger.LogDebug(
                    "HEAD {Url} returned {Status}; falling back to Size=0",
                    entry.Url, (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The outer install wasn't cancelled — this was our own per-probe
            // deadline. Treat as "size unknown" and let the GET proceed.
            _logger.LogDebug("HEAD {Url} timed out after {Timeout}; falling back to Size=0",
                entry.Url, ProbeTimeout);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "HEAD {Url} failed; falling back to Size=0", entry.Url);
        }
        return new SourceFile(Path: entry.DestFile, Size: size, Sha256: null);
    }

    public async ValueTask<string> DownloadFileAsync(
        CatalogSource source,
        SourceFile file,
        string destPath,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken ct)
    {
        HttpsSource https = Cast(source);

        // Reverse-lookup the URL by the file's local name. ListFilesAsync
        // populated SourceFile.Path = HttpsFile.DestFile, so this match
        // is exact.
        HttpsFile? entry = null;
        foreach (HttpsFile e in https.Urls)
        {
            if (e.DestFile == file.Path) { entry = e; break; }
        }
        if (entry is null)
        {
            throw new InvalidOperationException(
                $"HttpsSourceClient: no URL entry for destination file '{file.Path}'. " +
                "The source registry passed in a SourceFile that wasn't produced by " +
                "this client's ListFilesAsync — caller bug.");
        }

        _logger.LogDebug("HTTPS fetch: {Url}", entry.Url);

        return await HttpFileDownloader.DownloadAsync(
            _http, entry.Url, destPath, progress, _logger, ct,
            userAgentOverride: https.UserAgent).ConfigureAwait(false);
    }

    private static HttpsSource Cast(CatalogSource source) =>
        source as HttpsSource
            ?? throw new InvalidOperationException(
                $"HttpsSourceClient cannot handle {source.GetType().Name}; " +
                "the source registry routed this entry to the wrong client.");
}
