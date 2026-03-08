// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

/// <summary>
/// Per-file byte-progress sample passed to <c>IProgress&lt;T&gt;</c>
/// callbacks during a download. <see cref="BytesTotal"/> is null when the
/// underlying source doesn't expose Content-Length (rare — most HTTP
/// servers do, but some chunked-encoding paths drop it).
/// </summary>
public readonly record struct DownloadByteProgress(long BytesRead, long? BytesTotal);
