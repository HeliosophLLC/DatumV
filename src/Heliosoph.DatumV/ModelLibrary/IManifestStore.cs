// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.ModelLibrary;

// Singleton facade over the on-disk catalog.json + license text files.
// Loaded once at startup; the underlying files are content shipped with
// the app, so re-reading at runtime is unnecessary.
public interface IManifestStore
{
    CatalogManifest Manifest { get; }

    // Absolute path to the directory that holds catalog.json. Paths inside
    // the manifest (installSql, card files, hero images) resolve relative
    // to this directory. License text lives in the central
    // ILicenseRegistry, not under the per-manifest directory.
    string ManifestDirectory { get; }

    // Resolves a variant id to its (entry, variant) pair. The installer,
    // downloader, and residency manager all key on variant ids — but
    // license gating + attribution + card content live at entry level, so
    // every consumer needs both. Returns null when the id is unknown.
    (CatalogEntry Entry, CatalogVariant Variant)? TryResolveVariant(string variantId);

    // Returns the markdown body of the entry card for `entryName`, or
    // null when the entry didn't declare a cardFile. Resolved at load
    // time; this is a plain file read at call time.
    string? GetEntryCardMarkdown(string entryName);

    // Absolute filesystem path to the asset file `relativePath` underneath
    // the entry-card-owner's `cards/<name>/` tree, validated against
    // path-traversal escapes. Null when no card is registered for
    // `entryName`, or when the resolved path falls outside the manifest
    // directory's cards/ subtree (escape attempt). The caller streams the
    // file content with the right content type.
    string? ResolveEntryCardAssetPath(string entryName, string relativePath);

    // Absolute filesystem path to the hero image declared on `entryName`'s
    // entry, or null when the entry didn't set one or the file isn't on
    // disk. The renderer asks for the path; the controller streams it.
    string? ResolveHeroImagePath(string entryName);

    // Reverse identifier→catalog index built once at load time from each
    // version's declared `models[]` arrays. Consumed by `system.models`,
    // `system.tasks`, pre-flight, and the install-time cross-check.
    ICatalogVocabulary Vocabulary { get; }
}
