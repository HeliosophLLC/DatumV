// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json.Serialization;

namespace Heliosoph.DatumV.GpuRuntime;

// Hosted at <cuda-cdn-base>/manifest.json. The app fetches at probe time
// to discover which version of the CUDA bundle is current, what URL to
// download from, and the expected SHA-256 + size for verification + UI
// progress bars. Schema is deliberately small and additive — new fields
// don't break older clients (which ignore them via default-init).
public sealed record CudaBundleManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("platforms")] IReadOnlyDictionary<string, CudaBundlePlatformEntry> Platforms);

public sealed record CudaBundlePlatformEntry(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("extracted_size_bytes")] long ExtractedSizeBytes);
