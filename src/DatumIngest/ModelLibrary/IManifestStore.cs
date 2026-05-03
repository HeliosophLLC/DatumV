// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

// Singleton facade over the on-disk catalog.json + license text files.
// Loaded once at startup; the underlying files are content shipped with
// the app, so re-reading at runtime is unnecessary.
public interface IManifestStore
{
    CatalogManifest Manifest { get; }

    // Absolute path to the directory that holds catalog.json. Paths inside
    // the manifest (license textFile, installSql, future per-model assets)
    // resolve relative to this directory.
    string ManifestDirectory { get; }

    // Returns the raw license text (markdown or plain text, depending on
    // license) for the given license id. Null if the id is unknown or the
    // referenced textFile is missing on disk.
    string? GetLicenseText(string licenseId);

    // Returns the markdown body of the family card for `modelFamily`, or
    // null when no entry in that family declares a `familyCardFile`.
    // Resolved at load time; this is a plain file read at call time.
    string? GetFamilyCardMarkdown(string modelFamily);

    // Absolute filesystem path to the asset file `relativePath` underneath
    // the family-card-owner's `cards/<family>/` tree, validated against
    // path-traversal escapes. Null when no family card is registered for
    // `modelFamily`, or when the resolved path falls outside the manifest
    // directory (escape attempt). The caller streams the file content
    // with the right content type.
    string? ResolveFamilyCardAssetPath(string modelFamily, string relativePath);

    // Absolute filesystem path to the hero image declared on `modelId`'s
    // entry, or null when the entry didn't set one or the file isn't on
    // disk. The renderer asks for the path; the controller streams it.
    string? ResolveHeroImagePath(string modelId);

    // Reverse identifier→catalog index built once at load time from each
    // version's declared `models[]` arrays. Consumed by `system.models`,
    // `system.tasks`, pre-flight, and the install-time cross-check.
    // See <see cref="ICatalogVocabulary"/>.
    ICatalogVocabulary Vocabulary { get; }
}
