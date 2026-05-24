// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// One channel the model downloader can pull bytes from. One implementation
/// per <see cref="CatalogSource"/> subtype — they're routed by
/// <see cref="SupportedType"/> string-matching the manifest's <c>type</c>
/// discriminator. <see cref="ModelDownloadService"/> walks a model's
/// <see cref="CatalogVariant.Sources"/> list in order; each source's client
/// gets a shot at listing and downloading the file set before falling
/// through to the next.
/// </summary>
public interface IModelSourceClient
{
    /// <summary>
    /// Stable discriminator that matches the <c>type</c> field on the
    /// corresponding <see cref="CatalogSource"/> JSON entry (e.g.
    /// <c>"huggingface"</c>, <c>"github-release"</c>, <c>"https"</c>).
    /// The DI registry uses this to dispatch.
    /// </summary>
    string SupportedType { get; }

    /// <summary>
    /// Resolves the file list this source will provide for
    /// <paramref name="source"/>. For HuggingFace this is a tree-API call
    /// with include-glob filtering; for github-release it's the literal
    /// `Files` array projected onto the release URL; for https it's the
    /// `Urls` array directly. Throws on hard failure (network, 404,
    /// gated repo without credentials) — the caller treats the throw as
    /// "this source can't serve us" and moves on to the next source.
    /// </summary>
    ValueTask<IReadOnlyList<SourceFile>> ListFilesAsync(
        CatalogSource source, CancellationToken ct);

    /// <summary>
    /// Streams <paramref name="file"/> into <paramref name="destPath"/>.
    /// The implementation is responsible for resume semantics (e.g.
    /// honoring an existing <c>.part</c> file via HTTP Range when the
    /// underlying server supports it). Returns the lowercase-hex sha256
    /// of the downloaded bytes — callers can compare against an
    /// expected hash when one is available (HuggingFace LFS), or use the
    /// returned value to seed a checksum cache for future cross-source
    /// verification.
    /// </summary>
    ValueTask<string> DownloadFileAsync(
        CatalogSource source,
        SourceFile file,
        string destPath,
        IProgress<DownloadByteProgress>? progress,
        CancellationToken ct);
}

/// <summary>
/// One file inside a source's manifest. Whichever source serves the model,
/// the file inventory normalises to this shape — relative path within the
/// model directory, size in bytes, optional expected sha256 (present only
/// for HuggingFace LFS entries today; null for everything else).
/// </summary>
/// <param name="Path">Relative path inside the model's local directory.
/// Forward slashes; nested paths are allowed (e.g. <c>"onnx/model.onnx"</c>).</param>
/// <param name="Size">Expected size in bytes. Used by
/// <see cref="ModelDownloadService.ProbeAsync"/> for the on-disk
/// "right size?" check, and for cross-file total-bytes reporting.</param>
/// <param name="Sha256">Lower-hex SHA-256 expected for this file's bytes,
/// or <see langword="null"/> when the source doesn't advertise one.
/// HuggingFace surfaces this for LFS-tracked files via the tree API;
/// GitHub releases and raw HTTPS sources don't.</param>
public sealed record SourceFile(string Path, long Size, string? Sha256);
