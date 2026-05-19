using Tapper;

namespace Heliosoph.DatumV.Web.Hubs;

// Wire-format DTOs for model-download lifecycle events. Mirror 1:1 of the
// core records in Heliosoph.DatumV.ModelLibrary, but carry [TranspilationSource]
// so the TypedSignalR TypeScript codegen emits matching interfaces for the
// React client. Conversion happens at the host boundary in
// SignalRDownloadProgressReporter — core records never touch the wire.
//
// Why duplicate the shapes instead of putting Tapper attributes on the core
// records: TypeScript codegen is a Web concern. The core engine has no
// reason to know that TypeScript exists. Keeping the Tapper marker in Web
// preserves clean layering at the cost of ~50 lines of mirror types.

[TranspilationSource]
public sealed record ModelDownloadStartedDto(
    string ModelId,
    int FileCount,
    long TotalBytes);

[TranspilationSource]
public sealed record ModelDownloadProgressDto(
    string ModelId,
    string CurrentFile,
    int FileIndex,
    int FileCount,
    long BytesReadInFile,
    long BytesTotalInFile,
    long BytesReadTotal,
    long BytesTotalAcrossModel);

[TranspilationSource]
public sealed record ModelDownloadCompleteDto(string ModelId);

[TranspilationSource]
public sealed record ModelInstallingDto(string ModelId);

[TranspilationSource]
public sealed record ModelInstalledDto(string ModelId);

[TranspilationSource]
public sealed record ModelDownloadFailedDto(string ModelId, string Error);
