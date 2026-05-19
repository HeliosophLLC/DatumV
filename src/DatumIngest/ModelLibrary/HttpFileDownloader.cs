// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.ModelLibrary;

// Shared streaming-download core for source clients that don't have their
// own tree/list-API quirks. Used by GithubReleaseSourceClient and
// HttpsSourceClient — both of which:
//   - Fetch a single full URL (no auth header dance)
//   - Have no upstream-advertised sha256 to seed the resume hash against
//   - Respect plain HTTP Range / 206 / 416 semantics
//
// HuggingFaceSourceClient is NOT routed through here because it needs to
// seed the resume hash from existing .part bytes BEFORE issuing the GET
// (so the final-hash compare against the tree API's lfs.sha256 works).
// The GitHub / HTTPS paths skip that step because there's no expected
// hash to compare against — the returned hex is informational only.
internal static class HttpFileDownloader
{
    /// <summary>
    /// GET <paramref name="url"/> using <paramref name="http"/>, stream
    /// into <paramref name="destPath"/>.part with Range-based resume,
    /// hash incrementally, and atomically move into place on success.
    /// Returns the lower-hex SHA-256 of the downloaded bytes.
    /// </summary>
    public static async Task<string> DownloadAsync(
        HttpClient http,
        string url,
        string destPath,
        IProgress<DownloadByteProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        string partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        long existing = 0;
        if (File.Exists(partPath))
        {
            existing = new FileInfo(partPath).Length;
        }

        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        // Re-hash existing .part bytes so the returned hex covers the full
        // file. Failures here drop us back to zero — partial bytes can be
        // re-downloaded; the user's hash result must be authoritative.
        if (existing > 0)
        {
            try
            {
                await SeedHashFromPartAsync(partPath, sha, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not seed hash from {Part}; restarting from zero", partPath);
                existing = 0;
            }
        }

        using HttpRequestMessage req = new(HttpMethod.Get, url);
        if (existing > 0)
        {
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        }

        using HttpResponseMessage resp = await http.SendAsync(
            req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        // 416 = existing >= total. Treat the .part as complete and move
        // into place; the caller can re-download if a later sha check
        // disagrees (only relevant for sources that advertise an expected
        // hash — for github-release / https that's none today).
        if ((int)resp.StatusCode == 416)
        {
            logger.LogDebug("HTTP 416 for {Url}; treating existing .part as complete", url);
            byte[] hash416 = sha.GetHashAndReset();
            string hex416 = Convert.ToHexString(hash416).ToLowerInvariant();
            File.Move(partPath, destPath, overwrite: true);
            return hex416;
        }

        resp.EnsureSuccessStatusCode();

        // 200 = server ignored our Range. Discard existing bytes so we
        // don't append the full response onto the partial file.
        bool isResume = existing > 0 && (int)resp.StatusCode == 206;
        if (existing > 0 && !isResume)
        {
            logger.LogDebug(
                "Server returned {Status} for ranged request to {Url}; restarting from zero",
                (int)resp.StatusCode, url);
            existing = 0;
            sha.GetHashAndReset();
        }

        long? remoteContentLength = resp.Content.Headers.ContentLength;
        // 206's Content-Length is the remaining bytes; 200 is the full size.
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
}
