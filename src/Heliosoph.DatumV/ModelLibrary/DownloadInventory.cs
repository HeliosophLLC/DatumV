// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json.Serialization;

namespace Heliosoph.DatumV.ModelLibrary;

// On-disk completion marker written at the end of a successful download
// phase. Lets the probe / resume paths answer "did we already finish
// downloading?" without re-asking the upstream source — that's how the
// Models view stays filesystem-only on open even when 20+ entries have
// local directories, and how dataset retries skip a redundant HF tree
// call when the download finished but a downstream phase (ingest) failed.
internal sealed record InventoryDoc(
    [property: JsonPropertyName("files")] List<InventoryEntry> Files);

internal sealed record InventoryEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size);
