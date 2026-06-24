// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace Heliosoph.DatumV.GpuRuntime;

/// <summary>
/// Resolves the per-machine cache directory for the CUDA runtime bundle.
/// Lives outside the app install dir so it survives app upgrades; one
/// shared cache per host means multiple catalogs share a single download.
/// </summary>
public interface ICudaBundleCacheLayout
{
    /// <summary>Root of all CUDA bundle versions, e.g. &lt;global&gt;/cuda-runtime/.</summary>
    string CacheRoot { get; }

    /// <summary>The per-version subdirectory, e.g. &lt;global&gt;/cuda-runtime/v1.0.0/.</summary>
    string VersionDir(string version);

    /// <summary>Where the in-flight download writes its .tar.zst (and matching .part).</summary>
    string DownloadStagingPath(string version);

    /// <summary>Atomically-renamed extract staging dir (then moved to VersionDir).</summary>
    string ExtractStagingDir(string version);
}

/// <summary>Default layout under &lt;DATUMV_GLOBAL_PATH&gt;/cuda-runtime/.</summary>
public sealed class GlobalDataCudaBundleCacheLayout : ICudaBundleCacheLayout
{
    public GlobalDataCudaBundleCacheLayout(string globalDataPath)
    {
        CacheRoot = Path.Combine(globalDataPath, "cuda-runtime");
    }

    public string CacheRoot { get; }

    public string VersionDir(string version)
        => Path.Combine(CacheRoot, SanitizeVersion(version));

    public string DownloadStagingPath(string version)
        => Path.Combine(CacheRoot, $".download-{SanitizeVersion(version)}.tar.zst");

    public string ExtractStagingDir(string version)
        => Path.Combine(CacheRoot, $".staging-{SanitizeVersion(version)}");

    // Defensive: manifest versions should only ever be SemVer-like
    // (digits + dots + hyphens for pre-release tags) but we never want
    // a malicious or malformed version string to escape the cache dir.
    private static string SanitizeVersion(string version)
    {
        foreach (char c in version)
        {
            if (!(char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_'))
                throw new ArgumentException(
                    $"CUDA bundle version contains illegal character '{c}': {version}",
                    nameof(version));
        }
        return version;
    }
}
