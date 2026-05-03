#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace DatumIngest.DatasetLibrary;

/// <summary>
/// Single source of truth for "where do this dataset's files live on disk?"
/// Datasets split across two roots:
/// <list type="bullet">
/// <item><b>Raw cache</b> — the downloaded archives and their extracted
/// trees. Sized to be expendable; the user's <c>keepRawDownloads</c>
/// setting decides whether it survives ingest. Lives under
/// <c>$DATUM_DATASETS</c> (or the per-user default).</item>
/// <item><b>Ingested catalog</b> — the produced <c>.datum</c> +
/// <c>.datum-blob</c> pairs that the table catalog reads. Lives under
/// <c>&lt;CatalogRootPath&gt;/datasets/</c> alongside the rest of the
/// table catalog's persistent state.</item>
/// </list>
/// </summary>
public interface IDatasetPathResolver
{
    /// <summary>
    /// Absolute path to the top-level raw archive cache directory
    /// (<c>$DATUM_DATASETS</c> or the per-user default). Stable for the
    /// lifetime of the resolver.
    /// </summary>
    string DatasetsCacheRoot { get; }

    /// <summary>
    /// Absolute path to the top-level ingested-datasets directory under
    /// the catalog root. Stable for the lifetime of the resolver.
    /// </summary>
    string IngestedDatasetsRoot { get; }

    /// <summary>
    /// Absolute path to the per-dataset folder under the raw cache. Per-
    /// version layout: <c>&lt;cacheRoot&gt;/&lt;id&gt;/&lt;versionPin&gt;</c>.
    /// Falls back to the version-less folder when no version is supplied.
    /// </summary>
    string GetRawCacheRoot(string datasetId, string? versionPin = null);

    /// <summary>
    /// Absolute path to the per-dataset folder under the ingested-datasets
    /// root. Per-version layout: <c>&lt;ingestedRoot&gt;/&lt;id&gt;/&lt;versionPin&gt;</c>.
    /// Falls back to the version-less folder when no version is supplied.
    /// </summary>
    string GetIngestedRoot(string datasetId, string? versionPin = null);

    /// <summary>
    /// True when <c>&lt;ingestedRoot&gt;/&lt;id&gt;/&lt;version&gt;/</c> exists
    /// on disk. Used to decide whether a `datasets.X` reference is
    /// already installed.
    /// </summary>
    bool IsVersionOnDisk(string datasetId, string version);
}

/// <summary>
/// Production resolver: paths look like
/// <c>&lt;root&gt;/&lt;id&gt;/&lt;version&gt;/&lt;rest&gt;</c> under each of the
/// two roots. No catalog lookup yet — callers supply the versionPin
/// explicitly (typically from the catalog entry's <c>Versions[0].Version</c>).
/// A future PR will add an async-local install context and a catalog
/// lookup so paths can resolve against an active-version pointer the
/// same way models do.
/// </summary>
internal sealed class VersionedDatasetPathResolver : IDatasetPathResolver
{
    public string DatasetsCacheRoot { get; }
    public string IngestedDatasetsRoot { get; }

    public VersionedDatasetPathResolver(string datasetsCacheRoot, string ingestedDatasetsRoot)
    {
        ArgumentNullException.ThrowIfNull(datasetsCacheRoot);
        ArgumentNullException.ThrowIfNull(ingestedDatasetsRoot);
        DatasetsCacheRoot = datasetsCacheRoot;
        IngestedDatasetsRoot = ingestedDatasetsRoot;
    }

    public VersionedDatasetPathResolver(DatasetLibraryOptions options)
        : this(options.DatasetsCacheDirectory, Path.Combine(options.CatalogRootPath, "datasets"))
    {
    }

    public string GetRawCacheRoot(string datasetId, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(datasetId);
        string idFolder = Path.Combine(DatasetsCacheRoot, datasetId);
        return versionPin is null ? idFolder : Path.Combine(idFolder, versionPin);
    }

    public string GetIngestedRoot(string datasetId, string? versionPin = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(datasetId);
        string idFolder = Path.Combine(IngestedDatasetsRoot, datasetId);
        return versionPin is null ? idFolder : Path.Combine(idFolder, versionPin);
    }

    public bool IsVersionOnDisk(string datasetId, string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(datasetId);
        ArgumentException.ThrowIfNullOrEmpty(version);
        return Directory.Exists(Path.Combine(IngestedDatasetsRoot, datasetId, version));
    }
}
