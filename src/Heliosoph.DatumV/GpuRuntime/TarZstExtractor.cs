// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using System.Formats.Tar;
using Microsoft.Extensions.Logging;
using ZstdSharp;

namespace Heliosoph.DatumV.GpuRuntime;

// Streams a .tar.zst archive (zstd-compressed tar) into a destination
// directory, preserving Linux symlinks (the CUDA bundle ships chains like
// libcublas.so.12 -> libcublas.so.12.x.y.z). Decompresses + extracts in
// one pass — no intermediate .tar file on disk.
//
// Why this lives here and not in a shared utility: the CUDA bundle is the
// only consumer of tar.zst in the codebase today. If a second consumer
// arrives, lift this into a Heliosoph.DatumV.IO namespace.
public static class TarZstExtractor
{
    /// <summary>
    /// Decompress <paramref name="archivePath"/> (a .tar.zst file) into
    /// <paramref name="destDir"/>. <paramref name="totalFiles"/> may be 0
    /// when unknown — progress callbacks then carry 0 as the denominator.
    /// </summary>
    public static async Task<TarZstExtractResult> ExtractAsync(
        string archivePath,
        string destDir,
        long totalFiles,
        IProgress<TarZstExtractProgress>? progress,
        ILogger logger,
        CancellationToken ct)
    {
        Directory.CreateDirectory(destDir);

        await using FileStream input = new(
            archivePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 64 * 1024, useAsync: true);
        await using DecompressionStream zstd = new(input);

        long filesExtracted = 0;
        long bytesExtracted = 0;
        DateTime lastEmit = DateTime.UtcNow;

        await using TarReader tar = new(zstd, leaveOpen: false);
        TarEntry? entry;
        while ((entry = await tar.GetNextEntryAsync(copyData: false, ct).ConfigureAwait(false)) is not null)
        {
            string name = NormalizeEntryName(entry.Name);
            if (string.IsNullOrEmpty(name)) continue;

            // Reject path traversal — the bundle is built by our own
            // scripts so this should never fire, but the cost of a
            // surprise tarball write outside destDir is high.
            string fullPath = Path.GetFullPath(Path.Combine(destDir, name));
            if (!fullPath.StartsWith(destDir, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Tar entry escapes destination directory: {entry.Name}");
            }

            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(fullPath);
                    break;

                case TarEntryType.SymbolicLink:
                    WriteSymlink(fullPath, entry.LinkName, logger);
                    filesExtracted++;
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await using (FileStream output = new(
                        fullPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 64 * 1024, useAsync: true))
                    {
                        if (entry.DataStream is not null)
                        {
                            await entry.DataStream.CopyToAsync(output, 64 * 1024, ct)
                                .ConfigureAwait(false);
                            bytesExtracted += output.Length;
                        }
                    }
                    filesExtracted++;
                    break;

                default:
                    logger.LogDebug(
                        "Skipping tar entry with unsupported type {Type}: {Name}",
                        entry.EntryType, entry.Name);
                    break;
            }

            if (progress is not null)
            {
                DateTime now = DateTime.UtcNow;
                if ((now - lastEmit).TotalMilliseconds >= 100)
                {
                    progress.Report(new TarZstExtractProgress(
                        filesExtracted, totalFiles, bytesExtracted));
                    lastEmit = now;
                }
            }
        }

        progress?.Report(new TarZstExtractProgress(
            filesExtracted, totalFiles, bytesExtracted));
        return new TarZstExtractResult(filesExtracted, bytesExtracted);
    }

    private static string NormalizeEntryName(string name)
    {
        // tar entries may carry a leading "./" (our build-cuda-bundle.sh
        // produces these). Strip it so the resolved path stays clean.
        if (name.StartsWith("./", StringComparison.Ordinal)) return name[2..];
        if (name == ".") return string.Empty;
        return name;
    }

    private static void WriteSymlink(string fullPath, string? linkTarget, ILogger logger)
    {
        if (string.IsNullOrEmpty(linkTarget))
        {
            logger.LogWarning("Tar symlink with empty target at {Path}; skipping", fullPath);
            return;
        }
        // Remove any pre-existing entry — re-installs over an existing
        // version dir should be idempotent.
        if (File.Exists(fullPath)) File.Delete(fullPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        // Linux-only; on Windows the bundle is .dll files with no
        // symlinks so this branch is never reached.
        File.CreateSymbolicLink(fullPath, linkTarget);
    }
}

public sealed record TarZstExtractProgress(
    long FilesExtracted,
    long TotalFiles,
    long BytesExtracted);

public sealed record TarZstExtractResult(
    long FilesExtracted,
    long BytesExtracted);
