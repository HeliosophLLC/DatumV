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

    public ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct)
    {
        HttpsSource https = Cast(source);

        List<SourceFile> files = new(https.Urls.Count);
        foreach (HttpsFile entry in https.Urls)
        {
            // Path = the local destination filename. Url is held by the
            // CatalogSource and resolved at download time by re-matching
            // entries by DestFile. Size + Sha256 unknown until the GET.
            files.Add(new SourceFile(Path: entry.DestFile, Size: 0, Sha256: null));
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
            _http, entry.Url, destPath, progress, _logger, ct).ConfigureAwait(false);
    }

    private static HttpsSource Cast(CatalogSource source) =>
        source as HttpsSource
            ?? throw new InvalidOperationException(
                $"HttpsSourceClient cannot handle {source.GetType().Name}; " +
                "the source registry routed this entry to the wrong client.");
}
