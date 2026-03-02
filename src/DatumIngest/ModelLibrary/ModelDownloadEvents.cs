// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

// Lifecycle events emitted by ModelDownloadService through
// IDownloadProgressReporter. Pure data — hosts that need to surface these
// over the wire (the Web project's SignalR hub, for example) wrap them in
// their own DTO types and convert at the boundary.

public sealed record ModelDownloadStarted(
    string ModelId,
    int FileCount,
    long TotalBytes);

public sealed record ModelDownloadProgress(
    string ModelId,
    string CurrentFile,        // path inside the repo (e.g. "unet/model.onnx")
    int FileIndex,             // 1-based for UX ("3 of 7")
    int FileCount,
    long BytesReadInFile,
    long BytesTotalInFile,
    long BytesReadTotal,       // across all files in this model
    long BytesTotalAcrossModel);

public sealed record ModelDownloadComplete(string ModelId);

public sealed record ModelDownloadFailed(string ModelId, string Error);
