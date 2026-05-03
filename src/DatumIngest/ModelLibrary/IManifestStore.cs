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

    // Reverse identifier→catalog index built once at load time from each
    // version's declared `models[]` arrays. Consumed by `system.models`,
    // `system.tasks`, pre-flight, and the install-time cross-check.
    // See <see cref="ICatalogVocabulary"/>.
    ICatalogVocabulary Vocabulary { get; }
}
