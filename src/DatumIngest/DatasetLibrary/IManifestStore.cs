// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.DatasetLibrary;

// Singleton facade over the on-disk datasets/catalog.json + license text
// files. Loaded once at startup; the underlying files are content shipped
// with the app, so re-reading at runtime is unnecessary.
//
// Distinct from DatumIngest.ModelLibrary.IManifestStore — the two
// manifests have different shapes and different on-disk roots. Naming
// collision is by-design: each library exposes its own IManifestStore
// inside its own namespace, mirrored on the parallel-record convention.
public interface IManifestStore
{
    DatasetCatalogManifest Manifest { get; }

    // Absolute path to the directory that holds datasets/catalog.json.
    // Paths inside the manifest (license textFile, cardFile, hero
    // images, ingest source paths) resolve relative to this directory.
    string ManifestDirectory { get; }

    // Returns the raw license text (markdown or plain text, depending on
    // license) for the given license id. Null if the id is unknown or the
    // referenced textFile is missing on disk.
    string? GetLicenseText(string licenseId);

    // Returns the markdown body of the entry's card, or null when the
    // entry didn't declare a CardFile (or the entry doesn't exist).
    // Resolved at load time; the call here is a plain file read.
    string? GetEntryCardMarkdown(string entryName);

    // Absolute filesystem path to the asset file `relativePath`
    // underneath the entry's `cards/<entry-card-basename>/` tree,
    // validated against path-traversal escapes. Null when the entry has
    // no card registered, or when the resolved path falls outside the
    // manifest directory. The caller is responsible for streaming the
    // file content with the right content type.
    string? ResolveEntryAssetPath(string entryName, string relativePath);

    // Absolute filesystem path to the hero image declared on
    // `entryName`'s entry, or null when the entry didn't set one or the
    // file isn't present on disk.
    string? ResolveHeroImagePath(string entryName);

    // Finds a variant by its id and returns (entry, variant). Null when
    // no variant in the manifest carries that id. Used by the install
    // service to resolve the parent entry for license checks while
    // staying keyed on the variant for everything else.
    (DatasetEntry Entry, DatasetVariant Variant)? FindVariant(string variantId);
}
