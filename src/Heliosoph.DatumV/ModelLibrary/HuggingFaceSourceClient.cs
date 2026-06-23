// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

// IModelSourceClient implementation for HuggingFace Hub repos. Handles both
// model and dataset repos; <see cref="HuggingFaceSource.RepoType"/>
// discriminates between them and chooses the right API namespace:
//
//   RepoType "model"   (default for model-catalog entries):
//     - Tree:     /api/models/{repo}/tree/{revision}?recursive=true
//     - Download: /{repo}/resolve/{revision}/{path}
//
//   RepoType "dataset" (used by dataset-catalog HF mirrors):
//     - Tree:     /api/datasets/{repo}/tree/{revision}?recursive=true
//     - Download: /datasets/{repo}/resolve/{revision}/{path}
//
// Two operations matter:
//
//   1. ListFilesAsync (was: GetTreeAsync)
//        Returns every file under the repo at that revision, with size and
//        (for LFS files) sha256 inside `lfs.oid`. Non-LFS files use the git
//        blob OID — verification falls back to size-only for those.
//        Filtered against HuggingFaceSource.Include via the FileSystemGlobbing
//        matcher.
//
//   2. DownloadFileAsync
//        For LFS files HF responds with a 302 to an S3-presigned URL good
//        for ~10 minutes. HttpClient follows redirects automatically so
//        that's transparent here. Caller pre-creates the file; we stream
//        bytes in and hash incrementally.
//
// v1 limitations (designed to extend without breaking the surface):
//   - No HF token. Gated repos (Llama 3.x, Gemma, FLUX) will 401/403 here.
//     Wire a token via HfHubOptions when the first gated model lands.
//
// Resume semantics (cross-session): on entry to DownloadFileAsync, if a
// `<dest>.part` exists with N>0 bytes, we re-hash those bytes through the
// IncrementalHash to seed it, then issue the GET with `Range: bytes=N-`.
// Branches on the response:
//   - 206 Partial Content → open output in Append mode, continue from N.
//   - 200 OK             → server ignored Range (or doesn't support it);
//                          discard existing bytes, restart from zero.
//   - 416 Range Not Satisfiable → existing >= total; rehash the whole
//                          file. If hash matches the final hash returned
//                          by HF (caller verifies), the move-into-place
//                          succeeds and we're done. If not, restart.
// Because every catalog entry pins a commit SHA, the bytes at a given
// revision are immutable — no sidecar metadata needed to detect drift.
internal sealed class HuggingFaceSourceClient : IModelSourceClient
{
    public string SupportedType => "huggingface";

    private readonly HttpClient _http;
    private readonly ILogger<HuggingFaceSourceClient> _logger;

    public HuggingFaceSourceClient(HttpClient http, ILogger<HuggingFaceSourceClient> logger)
    {
        _http = http;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://huggingface.co/");
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Heliosoph.DatumV/0.1 (+https://github.com/Heliosoph.DatumV)");

        // Surface proxy-related env vars on first construction. Electron-
        // hosted runs inherit Chromium's proxy detection through env vars,
        // and an unreachable proxy is a stall that's hard to diagnose
        // from outside. The DI handler already sets UseProxy = false, so
        // this is purely informational — but if you see a value here that
        // doesn't show up in your bare PowerShell session, you've found
        // the difference between the two environments.
        string? httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY")
            ?? Environment.GetEnvironmentVariable("https_proxy");
        string? httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY")
            ?? Environment.GetEnvironmentVariable("http_proxy");
        string? allProxy = Environment.GetEnvironmentVariable("ALL_PROXY")
            ?? Environment.GetEnvironmentVariable("all_proxy");
        string? noProxy = Environment.GetEnvironmentVariable("NO_PROXY")
            ?? Environment.GetEnvironmentVariable("no_proxy");
        if (httpsProxy is not null || httpProxy is not null || allProxy is not null)
        {
            _logger.LogInformation(
                "HF client constructed with inherited proxy env vars: " +
                "HTTPS_PROXY={HttpsProxy}, HTTP_PROXY={HttpProxy}, ALL_PROXY={AllProxy}, NO_PROXY={NoProxy}. " +
                "Handler is configured with UseProxy=false, so these are ignored — but their presence " +
                "indicates this process inherits proxy config from its parent (likely Electron/Chromium).",
                httpsProxy, httpProxy, allProxy, noProxy);
        }
        else
        {
            _logger.LogInformation("HF client constructed; no proxy env vars inherited.");
        }
    }

    public async ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct)
    {
        HuggingFaceSource hf = Cast(source);

        string url = $"api/{RepoSegment(hf)}/{hf.Repo}/tree/{hf.Revision}?recursive=true";

        // Bound the tree call to 30s: every other operation on this client
        // legitimately takes longer (LFS downloads), so a tight per-call
        // budget here is the right surface to fail fast at.
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));

        // Force HTTP/1.1 explicitly. The HF tree endpoint serves both
        // 1.1 and h2, but a SocketsHttpHandler that successfully ALPN-
        // negotiates h2 against a middlebox that breaks h2 framing is the
        // single most common cause of "the request just sits there." 1.1
        // is the conservative, debuggable choice for a JSON metadata call.
        using HttpRequestMessage req = new(HttpMethod.Get, url)
        {
            Version = System.Net.HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
        };

        _logger.LogInformation("HF tree GET {Url}", url);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(
                "HF tree GET {Url} stalled past 30s — check network reachability " +
                "(this is the .NET HttpClient, not curl; HTTP/2, DNS, and IPv6 " +
                "can all cause this even when curl works).", url);
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "HF tree GET {Url} failed at the transport layer: {Message}",
                url, ex.Message);
            throw;
        }

        using (resp)
        {
            _logger.LogInformation(
                "HF tree {Url} → {Status} (HTTP/{Version})",
                url, (int)resp.StatusCode, resp.Version);
            resp.EnsureSuccessStatusCode();

            await using Stream s = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            List<HfTreeEntry>? entries = await System.Text.Json.JsonSerializer.DeserializeAsync<List<HfTreeEntry>>(
                s, _jsonOptions, cts.Token).ConfigureAwait(false);
            IReadOnlyList<HfTreeEntry> tree = entries ?? new List<HfTreeEntry>();
            return BuildSourceFiles(hf, tree);
        }
    }

    private static IReadOnlyList<SourceFile> BuildSourceFiles(HuggingFaceSource hf, IReadOnlyList<HfTreeEntry> tree)
    {

        // Filter against include globs (** / * / literal names handled
        // correctly by FileSystemGlobbing). Drop directory entries.
        Matcher matcher = new();
        foreach (string pattern in hf.Include) matcher.AddInclude(pattern);

        List<SourceFile> result = new();
        foreach (HfTreeEntry entry in tree)
        {
            if (entry.Type != "file") continue;
            if (!matcher.Match(new[] { entry.Path }).HasMatches) continue;
            // Prefer the LFS-reported size when present: the top-level
            // `size` on a tree entry is the git-blob size, which for an
            // LFS pointer is ~135 bytes rather than the real payload.
            long size = entry.Lfs is { Size: > 0 } lfs ? lfs.Size : entry.Size;
            result.Add(new SourceFile(
                Path: entry.Path,
                Size: size,
                Sha256: entry.Lfs?.Sha256Hex));
        }
        return result;
    }

    public async ValueTask<string> DownloadFileAsync(
        CatalogSource source,
        SourceFile file,
        string destPath,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken ct)
    {
        HuggingFaceSource hf = Cast(source);

        string partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        // Resolve path differs between model and dataset namespaces: model
        // repos live at the root (huggingface.co/{repo}/...), dataset repos
        // are under /datasets/ (huggingface.co/datasets/{repo}/...).
        string downloadPrefix = hf.RepoType.Equals("dataset", StringComparison.OrdinalIgnoreCase)
            ? "datasets/"
            : string.Empty;
        string url = $"{downloadPrefix}{hf.Repo}/resolve/{hf.Revision}/{file.Path}";

        // How many bytes from a prior session are already on disk? Zero
        // means a fresh download.
        long existing = 0;
        if (File.Exists(partPath))
        {
            existing = new FileInfo(partPath).Length;
        }

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Seed the hash with existing .part bytes so the final-hash check
        // matches what HF would have produced for the full file. This is
        // the only place we read the partial file from disk; if it fails
        // (file vanished, IO error), fall back to from-scratch.
        if (existing > 0)
        {
            try
            {
                await SeedHashFromPartAsync(partPath, sha, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not seed hash from {Part}; restarting from zero", partPath);
                existing = 0;
            }
        }

        using HttpRequestMessage req = new(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using HttpResponseMessage resp = await _http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 416 = our existing bytes are >= total. Either the move-into-place
        // failed last time (rare) or the file shrank server-side (only
        // possible if catalog revision points at a mutable ref, which we
        // disallow). Re-hash the whole file; caller verifies. If their
        // SHA check fails, the catch in RunDownloadAsync will surface it
        // and the user can Restart to wipe the .part.
        if ((int)resp.StatusCode == 416)
        {
            _logger.LogDebug("HF returned 416 for {Url}; treating existing .part as complete", url);
            byte[] hash416 = sha.GetHashAndReset();
            string hex416 = Convert.ToHexString(hash416).ToLowerInvariant();
            File.Move(partPath, destPath, overwrite: true);
            return hex416;
        }

        resp.EnsureSuccessStatusCode();

        // 200 = server ignored our Range (or no .part existed). Discard
        // any partial bytes and restart from zero so we don't append the
        // full response onto the partial file. 206 = continue from
        // `existing` in Append mode.
        bool isResume = existing > 0 && (int)resp.StatusCode == 206;
        if (existing > 0 && !isResume)
        {
            _logger.LogDebug(
                "HF returned {Status} for ranged request to {Url}; restarting from zero",
                (int)resp.StatusCode, url);
            existing = 0;
            sha.GetHashAndReset(); // reset accumulated state
        }

        long? remoteContentLength = resp.Content.Headers.ContentLength;
        // For 206 the Content-Length is the *remaining* bytes; for 200 it
        // is the full file. The UI wants the full-file total.
        long? totalBytes = isResume
            ? (resp.Content.Headers.ContentRange?.Length ?? (remoteContentLength + existing))
            : remoteContentLength;

        FileMode mode = isResume ? FileMode.Append : FileMode.Create;
        await using (FileStream output = new(
            partPath, mode, FileAccess.Write, FileShare.None,
            bufferSize: 64 * 1024, useAsync: true))
        await using (Stream input = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        {
            byte[] buf = new byte[64 * 1024];
            long bytesRead = existing;
            // Throttle progress events to ~10 Hz so React doesn't render-thrash
            // on a fast disk. The hub is push-only; missing intermediate
            // counts is fine.
            DateTime lastEmit = DateTime.UtcNow;
            int read;
            while ((read = await input.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
                sha.AppendData(buf, 0, read);
                bytesRead += read;

                if (progress is not null)
                {
                    DateTime now = DateTime.UtcNow;
                    if ((now - lastEmit).TotalMilliseconds >= 100)
                    {
                        progress.Report(new DownloadByteProgress(bytesRead, totalBytes));
                        lastEmit = now;
                    }
                }
            }

            progress?.Report(new DownloadByteProgress(bytesRead, totalBytes ?? bytesRead));
        }

        byte[] hash = sha.GetHashAndReset();
        string hex = Convert.ToHexString(hash).ToLowerInvariant();

        File.Move(partPath, destPath, overwrite: true);
        return hex;
    }

    private static HuggingFaceSource Cast(CatalogSource source) =>
        source as HuggingFaceSource
            ?? throw new InvalidOperationException(
                $"HuggingFaceSourceClient cannot handle {source.GetType().Name}; " +
                "the source registry routed this entry to the wrong client.");

    // Selects the API path segment for HF's tree endpoint. The HF Hub has
    // two top-level namespaces — models and datasets — with otherwise
    // identical tree/resolve semantics.
    private static string RepoSegment(HuggingFaceSource hf)
    {
        if (hf.RepoType.Equals("model", StringComparison.OrdinalIgnoreCase)) { return "models"; }
        if (hf.RepoType.Equals("dataset", StringComparison.OrdinalIgnoreCase)) { return "datasets"; }
        throw new InvalidOperationException(
            $"HuggingFaceSource.repoType '{hf.RepoType}' is not recognised. " +
            "Expected 'model' (default) or 'dataset'.");
    }

    // Stream the existing .part bytes through the hash to seed it. Used
    // before sending a Range request so the final hash matches HF's hash
    // of the full file.
    private static async Task SeedHashFromPartAsync(
        string partPath, IncrementalHash sha, CancellationToken ct)
    {
        await using FileStream fs = new(
            partPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        byte[] buf = new byte[64 * 1024];
        int read;
        while ((read = await fs.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buf, 0, read);
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
    };
}

// Mirrors a single entry from the HF tree API. `Lfs.Oid` holds "sha256:<hex>"
// for LFS-tracked files; small files (config.json, tokenizer.txt) ship as
// plain git blobs and have no `lfs` field — `Oid` then holds the git blob
// OID which we don't verify.
internal sealed record HfTreeEntry(
    string Type,
    string Oid,
    long Size,
    string Path,
    HfLfsInfo? Lfs);

internal sealed record HfLfsInfo(
    [property: JsonPropertyName("oid")] string Oid,
    [property: JsonPropertyName("size")] long Size)
{
    // HF returns "sha256:<hex>" — extract just the hex for comparison.
    public string Sha256Hex =>
        Oid.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Oid[7..] : Oid;
}
