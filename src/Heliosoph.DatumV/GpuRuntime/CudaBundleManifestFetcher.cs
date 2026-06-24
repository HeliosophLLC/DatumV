// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.GpuRuntime;

// Fetches the CUDA bundle manifest from the configured CDN URL. Cached in
// memory for a short window so the Settings UI polling the status endpoint
// doesn't hit the CDN on every request. Cache TTL is small (5 min) so a
// freshly-published manifest is picked up within one settings refresh.
public sealed class CudaBundleManifestFetcher
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly CudaBundleOptions _options;
    private readonly ILogger<CudaBundleManifestFetcher> _logger;

    private CudaBundleManifest? _cached;
    private DateTime _cachedAtUtc;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public CudaBundleManifestFetcher(
        HttpClient http,
        CudaBundleOptions options,
        ILogger<CudaBundleManifestFetcher> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;
    }

    public async Task<CudaBundleManifest?> FetchAsync(CancellationToken ct, bool forceRefresh = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!forceRefresh
                && _cached is not null
                && (DateTime.UtcNow - _cachedAtUtc) < CacheTtl)
            {
                return _cached;
            }

            try
            {
                using HttpResponseMessage resp = await _http.GetAsync(
                    _options.ManifestUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                await using Stream body = await resp.Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                CudaBundleManifest? manifest = await JsonSerializer.DeserializeAsync<CudaBundleManifest>(
                    body, JsonOpts, ct).ConfigureAwait(false);

                if (manifest is { Version.Length: > 0 } && manifest.Platforms.Count > 0)
                {
                    _cached = manifest;
                    _cachedAtUtc = DateTime.UtcNow;
                    return manifest;
                }

                _logger.LogWarning(
                    "CUDA bundle manifest at {Url} parsed but contained no version or platforms",
                    _options.ManifestUrl);
                return null;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                _logger.LogWarning(ex,
                    "Failed to fetch CUDA bundle manifest from {Url}; returning cached value (may be null)",
                    _options.ManifestUrl);
                // Return stale cache on network failure so the UI keeps
                // working offline once a manifest has been seen once.
                return _cached;
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// Configurable knobs for the CUDA bundle subsystem. Bound from
/// configuration so dev/test can override the manifest URL.
/// </summary>
public sealed class CudaBundleOptions
{
    /// <summary>
    /// URL of the manifest.json describing the current bundle version,
    /// per-platform download URLs, and SHA-256 hashes.
    /// </summary>
    public string ManifestUrl { get; set; } = "https://cuda-cdn.heliosoph.com/manifest.json";
}
