using DatumIngest.Catalog;

namespace DatumIngest.Output.Checkpoint;

/// <summary>
/// Collects file-level identity snapshots from registered data sources,
/// used to detect whether sources have changed between a failed run and a resume.
/// </summary>
public static class SourceFingerprintCollector
{
    /// <summary>
    /// Collects fingerprints (file size and last-modified time) for all registered tables.
    /// </summary>
    /// <param name="catalog">The table catalog containing registered sources.</param>
    /// <returns>A list of source fingerprints, one per registered table.</returns>
    public static IReadOnlyList<SourceFingerprint> Collect(TableCatalog catalog)
    {
        List<SourceFingerprint> fingerprints = new();

        foreach (string tableName in catalog.TableNames)
        {
            TableDescriptor descriptor = catalog.Resolve(tableName);
            FileInfo fileInfo = new(descriptor.FilePath);

            if (fileInfo.Exists)
            {
                fingerprints.Add(new SourceFingerprint(
                    descriptor.Name,
                    descriptor.Provider,
                    descriptor.FilePath,
                    fileInfo.Length,
                    fileInfo.LastWriteTimeUtc));
            }
            else
            {
                // Record zero-size fingerprint for sources that are not local files
                // (e.g. virtual providers or missing files detected at query time).
                fingerprints.Add(new SourceFingerprint(
                    descriptor.Name,
                    descriptor.Provider,
                    descriptor.FilePath,
                    SizeBytes: 0,
                    LastModifiedUtc: DateTime.MinValue));
            }
        }

        return fingerprints;
    }

    /// <summary>
    /// Validates that current source fingerprints match the expected fingerprints
    /// from a previous checkpoint. Returns a mismatch description if sources have
    /// changed, or <c>null</c> if all fingerprints match.
    /// </summary>
    /// <param name="expected">Fingerprints from the checkpoint markers.</param>
    /// <param name="current">Freshly collected fingerprints.</param>
    /// <returns>A human-readable mismatch description, or <c>null</c> if valid.</returns>
    public static string? Validate(
        IReadOnlyList<SourceFingerprint> expected,
        IReadOnlyList<SourceFingerprint> current)
    {
        Dictionary<string, SourceFingerprint> currentByName = new(StringComparer.OrdinalIgnoreCase);
        foreach (SourceFingerprint fingerprint in current)
        {
            currentByName[fingerprint.Name] = fingerprint;
        }

        foreach (SourceFingerprint expectedFingerprint in expected)
        {
            if (!currentByName.TryGetValue(expectedFingerprint.Name, out SourceFingerprint? currentFingerprint))
            {
                return $"Source '{expectedFingerprint.Name}' is missing from the current catalog.";
            }

            if (expectedFingerprint.SizeBytes != currentFingerprint.SizeBytes)
            {
                return $"Source '{expectedFingerprint.Name}' size changed: " +
                       $"expected {expectedFingerprint.SizeBytes} bytes, " +
                       $"got {currentFingerprint.SizeBytes} bytes.";
            }

            if (expectedFingerprint.LastModifiedUtc != currentFingerprint.LastModifiedUtc)
            {
                return $"Source '{expectedFingerprint.Name}' modification time changed: " +
                       $"expected {expectedFingerprint.LastModifiedUtc:O}, " +
                       $"got {currentFingerprint.LastModifiedUtc:O}.";
            }
        }

        return null;
    }
}
