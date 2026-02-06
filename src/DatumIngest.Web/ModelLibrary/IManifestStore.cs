namespace DatumIngest.Web.ModelLibrary;

// Singleton facade over the on-disk catalog.json + license text files.
// Loaded once at startup; the underlying files are content shipped with
// the app, so re-reading at runtime is unnecessary.
public interface IManifestStore
{
    CatalogManifest Manifest { get; }

    // Returns the raw license text (markdown or plain text, depending on
    // license) for the given license id. Null if the id is unknown or the
    // referenced textFile is missing on disk.
    string? GetLicenseText(string licenseId);
}
