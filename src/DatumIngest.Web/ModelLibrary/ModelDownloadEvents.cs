using Tapper;

namespace DatumIngest.Web.ModelLibrary;

// Push payloads from the server over SignalR for model-download status.
// Mirrored 1:1 by the TypedSignalR codegen so the React side gets typed
// callbacks. [TranspilationSource] tells `dotnet-tsrts` to emit each record
// as a TypeScript interface; without it the transpiler refuses to handle
// the type and the post-build codegen fails.

[TranspilationSource]
public sealed record ModelDownloadStarted(
    string ModelId,
    int FileCount,
    long TotalBytes);

[TranspilationSource]
public sealed record ModelDownloadProgress(
    string ModelId,
    string CurrentFile,        // path inside the repo (e.g. "unet/model.onnx")
    int FileIndex,             // 1-based for UX ("3 of 7")
    int FileCount,
    long BytesReadInFile,
    long BytesTotalInFile,
    long BytesReadTotal,       // across all files in this model
    long BytesTotalAcrossModel);

[TranspilationSource]
public sealed record ModelDownloadComplete(string ModelId);

[TranspilationSource]
public sealed record ModelDownloadFailed(string ModelId, string Error);
