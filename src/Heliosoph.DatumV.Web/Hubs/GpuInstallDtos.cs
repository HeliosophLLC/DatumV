using Tapper;

namespace Heliosoph.DatumV.Web.Hubs;

// Wire-format DTOs for CUDA-bundle install lifecycle events. Mirror 1:1
// of the core records in Heliosoph.DatumV.GpuRuntime, but carry
// [TranspilationSource] so the TypedSignalR TypeScript codegen emits
// matching interfaces for the React client. Conversion happens at the
// host boundary in SignalRGpuInstallProgressReporter — core records
// never touch the wire.

[TranspilationSource]
public sealed record CudaBundleInstallStartedDto(
    string Version,
    long TotalBytes);

[TranspilationSource]
public sealed record CudaBundleDownloadProgressDto(
    string Version,
    long BytesDownloaded,
    long TotalBytes);

[TranspilationSource]
public sealed record CudaBundleExtractStartedDto(string Version);

[TranspilationSource]
public sealed record CudaBundleExtractProgressDto(
    string Version,
    long FilesExtracted,
    long TotalFiles,
    long BytesExtracted);

[TranspilationSource]
public sealed record CudaBundleInstalledDto(
    string Version,
    string InstalledPath);

[TranspilationSource]
public sealed record CudaBundleInstallFailedDto(
    string Version,
    string Error);
