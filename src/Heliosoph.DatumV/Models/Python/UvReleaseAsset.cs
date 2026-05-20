using System.Runtime.InteropServices;

namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// Resolves which GitHub release asset to download for the running
/// host's OS + architecture. Knows the naming convention uv's
/// <c>astral-sh/uv</c> releases use:
/// <c>uv-&lt;arch&gt;-&lt;vendor&gt;-&lt;os&gt;-&lt;abi&gt;.&lt;ext&gt;</c>.
/// </summary>
/// <remarks>
/// Supports the platform set that maps cleanly to .NET 8+ runtimes:
/// Windows x64, Linux x64 (glibc), macOS x64, macOS arm64. Linux arm,
/// Linux musl, Windows arm64 land when there's demand — uv ships
/// archives for all of them; only the host-detection logic here
/// needs extending.
/// </remarks>
internal readonly record struct UvReleaseAsset(
    string ArchiveFileName,
    string ArchiveEntryName)
{
    /// <summary>
    /// Builds the GitHub release download URL for <paramref name="version"/>.
    /// <c>"latest"</c> uses GitHub's redirect pattern; pinned versions
    /// use the tag URL form (<c>releases/download/&lt;tag&gt;/</c>).
    /// </summary>
    public string DownloadUrl(string version)
    {
        return string.Equals(version, "latest", StringComparison.OrdinalIgnoreCase)
            ? $"https://github.com/astral-sh/uv/releases/latest/download/{ArchiveFileName}"
            : $"https://github.com/astral-sh/uv/releases/download/{version}/{ArchiveFileName}";
    }

    /// <summary>
    /// Picks the asset matching the running OS + process architecture.
    /// Throws when neither is recognised — better to fail fast at
    /// "couldn't pick an asset" than to download something
    /// platform-mismatched and have the verification step fail with a
    /// less specific error.
    /// </summary>
    public static UvReleaseAsset ForCurrentPlatform()
    {
        Architecture arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsWindows())
        {
            return arch switch
            {
                Architecture.X64 => new UvReleaseAsset(
                    ArchiveFileName: "uv-x86_64-pc-windows-msvc.zip",
                    ArchiveEntryName: "uv.exe"),
                _ => throw new PlatformNotSupportedException(
                    $"No uv asset configured for Windows on {arch}. Add the mapping "
                    + "in UvReleaseAsset.ForCurrentPlatform; uv ships archives for "
                    + "aarch64-pc-windows-msvc and i686-pc-windows-msvc."),
            };
        }

        if (OperatingSystem.IsLinux())
        {
            // glibc-targeted asset. Alpine/musl users land in the
            // x86_64-unknown-linux-musl asset; not detecting that
            // automatically yet — most test environments are glibc.
            return arch switch
            {
                Architecture.X64 => new UvReleaseAsset(
                    ArchiveFileName: "uv-x86_64-unknown-linux-gnu.tar.gz",
                    ArchiveEntryName: "uv"),
                Architecture.Arm64 => new UvReleaseAsset(
                    ArchiveFileName: "uv-aarch64-unknown-linux-gnu.tar.gz",
                    ArchiveEntryName: "uv"),
                _ => throw new PlatformNotSupportedException(
                    $"No uv asset configured for Linux on {arch}. Add the mapping "
                    + "in UvReleaseAsset.ForCurrentPlatform."),
            };
        }

        if (OperatingSystem.IsMacOS())
        {
            return arch switch
            {
                Architecture.X64 => new UvReleaseAsset(
                    ArchiveFileName: "uv-x86_64-apple-darwin.tar.gz",
                    ArchiveEntryName: "uv"),
                Architecture.Arm64 => new UvReleaseAsset(
                    ArchiveFileName: "uv-aarch64-apple-darwin.tar.gz",
                    ArchiveEntryName: "uv"),
                _ => throw new PlatformNotSupportedException(
                    $"No uv asset configured for macOS on {arch}."),
            };
        }

        throw new PlatformNotSupportedException(
            $"uv installation is not supported on {RuntimeInformation.OSDescription}.");
    }
}
