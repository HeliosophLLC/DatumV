using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;

namespace DatumIngest.Web.ModelLibrary;

// Thin client over the HuggingFace Hub HTTP API. No huggingface-cli / Python
// dependency. Two operations matter:
//
//   1. GetTreeAsync(repo, revision)
//        GET https://huggingface.co/api/models/{repo}/tree/{revision}?recursive=true
//        Returns every file under the repo at that revision, with size and
//        (for LFS files) sha256 inside `lfs.oid`. Non-LFS files use the git
//        blob OID — verification falls back to size-only for those.
//
//   2. DownloadFileAsync(repo, revision, path, dest, progress)
//        GET https://huggingface.co/{repo}/resolve/{revision}/{path}
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
internal sealed class HfHubClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HfHubClient> _logger;

    public HfHubClient(HttpClient http, ILogger<HfHubClient> logger)
    {
        _http = http;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = new Uri("https://huggingface.co/");
        }
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DatumIngest/0.1 (+https://github.com/Heliosoph)");
    }

    public async Task<IReadOnlyList<HfTreeEntry>> GetTreeAsync(
        string repo, string revision, CancellationToken ct)
    {
        string url = $"api/models/{repo}/tree/{revision}?recursive=true";
        _logger.LogDebug("HF tree: {Url}", url);

        using HttpResponseMessage resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        await using Stream s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        List<HfTreeEntry>? entries = await System.Text.Json.JsonSerializer.DeserializeAsync<List<HfTreeEntry>>(
            s, _jsonOptions, ct).ConfigureAwait(false);
        return entries ?? new List<HfTreeEntry>();
    }

    // Filters tree-API entries against the manifest's include glob patterns.
    // Microsoft.Extensions.FileSystemGlobbing handles `**`, `*`, and literal
    // names correctly. Directory entries are dropped — we only want files.
    public static IReadOnlyList<HfTreeEntry> FilterByIncludes(
        IReadOnlyList<HfTreeEntry> tree, IEnumerable<string> includes)
    {
        Matcher matcher = new();
        foreach (string pattern in includes) matcher.AddInclude(pattern);

        List<HfTreeEntry> matched = new();
        foreach (HfTreeEntry entry in tree)
        {
            if (entry.Type != "file") continue;
            PatternMatchingResult result = matcher.Match(new[] { entry.Path });
            if (result.HasMatches) matched.Add(entry);
        }
        return matched;
    }

    // Streams the file at {repo}/resolve/{revision}/{path} into destPath.
    // Hashes via SHA256 during the stream (free — we touch every byte once).
    // On success, returns the lowercase-hex sha256 of the downloaded bytes.
    // Caller compares against the tree API's lfs.oid where available.
    //
    // Throws on:
    //   - HTTP non-success (callers can map 401/403 to "needs HF token")
    //   - cancellation (.part file is left on disk; future resume can pick it up)
    public async Task<string> DownloadFileAsync(
        string repo, string revision, string path, string destPath,
        IProgress<DownloadByteProgress>? progress, CancellationToken ct)
    {
        string partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        string url = $"{repo}/resolve/{revision}/{path}";

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

    // Stream the existing .part bytes through the hash to seed it. Used
    // before sending a Range request so the final hash matches HF's hash
    // of the full file. Kept private — only DownloadFileAsync calls it.
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
public sealed record HfTreeEntry(
    string Type,
    string Oid,
    long Size,
    string Path,
    HfLfsInfo? Lfs);

public sealed record HfLfsInfo(
    [property: JsonPropertyName("oid")] string Oid,
    [property: JsonPropertyName("size")] long Size)
{
    // HF returns "sha256:<hex>" — extract just the hex for comparison.
    public string Sha256Hex =>
        Oid.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? Oid[7..] : Oid;
}

public readonly record struct DownloadByteProgress(long BytesRead, long? BytesTotal);
