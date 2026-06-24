// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace Heliosoph.DatumV.GpuRuntime;

// Lifecycle events emitted by CudaBundleInstaller through
// ICudaBundleInstallProgressReporter. Pure data — the Web project's
// SignalR adapter wraps these in wire DTOs at the boundary.
//
// Lifecycle:
//   Started -> N x DownloadProgress -> ExtractStarted ->
//     N x ExtractProgress -> Installed
//   Failed replaces any later event on failure.

public sealed record CudaBundleInstallStarted(
    string Version,
    long TotalBytes);

// Download phase. BytesDownloaded climbs to TotalBytes, then ExtractStarted
// fires. Emitted at most ~10 times/second to match HttpFileDownloader's
// 100 ms throttle.
public sealed record CudaBundleDownloadProgress(
    string Version,
    long BytesDownloaded,
    long TotalBytes);

// SHA-256 verification + tar.zst extract begins. Same Version as the
// started event; clients use this transition to switch the UI from
// download to "preparing" / "installing" phase.
public sealed record CudaBundleExtractStarted(string Version);

// Per-file progress through the tar stream. FilesExtracted and BytesExtracted
// grow monotonically; TotalFiles is the count from the tar header walk and
// stays constant for a given bundle version.
public sealed record CudaBundleExtractProgress(
    string Version,
    long FilesExtracted,
    long TotalFiles,
    long BytesExtracted);

// Terminal success. InstalledPath points at the final cache directory
// (e.g. <global>/cuda-runtime/v1.0.0/). Electron reads this path on
// subsequent backend spawns to set LD_LIBRARY_PATH.
public sealed record CudaBundleInstalled(
    string Version,
    string InstalledPath);

public sealed record CudaBundleInstallFailed(
    string Version,
    string Error);
