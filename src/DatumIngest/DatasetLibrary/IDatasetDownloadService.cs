// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.DatasetLibrary;

public interface IDatasetDownloadService
{
    // Returns whether the dataset's expected `.datum` files are present
    // locally under the ingested-datasets root. Filesystem-only check;
    // does not re-validate row counts.
    Task<DatasetInstallState> ProbeAsync(string datasetId, CancellationToken ct = default);

    // Bulk variant. Cheaper for the Datasets view than N individual
    // ProbeAsync calls; same result.
    Task<IReadOnlyDictionary<string, DatasetInstallState>> ProbeAllAsync(CancellationToken ct = default);

    // Kicks off the install: download the source archives into the raw
    // cache, then run DatumIngest.Ingestion.Ingester against each ingest
    // job to produce a `.datum` (+ optional sidecar `.datum-blob`) per
    // table under the ingested root. Returns once queued; progress is
    // pushed asynchronously via IDatasetDownloadProgressReporter.
    // Throws if:
    //   - datasetId is unknown
    //   - one of the dataset's licenses is requiresAcceptance and not yet
    //     accepted
    //   - an install is already running for this dataset
    Task InstallAsync(string datasetId, CancellationToken ct = default);

    // Best-effort: deletes the dataset's ingested folder. The raw cache
    // is left alone — uninstall is about reclaiming catalog space, not
    // about wiping the source archive. The raw cache has its own purge
    // surface.
    Task UninstallAsync(string datasetId, CancellationToken ct = default);

    // Total bytes in any `<file>.part` files inside the dataset's raw-
    // cache folder. Used by the UI to surface a Resume affordance when
    // an interrupted download left bytes on disk.
    Task<long> GetPartialBytesAsync(string datasetId, CancellationToken ct = default);

    // Bulk variant — one entry per dataset with non-zero partial bytes.
    Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(CancellationToken ct = default);

    // Deletes all `*.part` files inside the dataset's raw-cache folder.
    Task DeletePartialsAsync(string datasetId, CancellationToken ct = default);

    // Walks the ingested-datasets root and deletes every `.staging-*`
    // directory left over from a crashed mid-install (process kill, OS
    // panic). Called once at boot from DatasetCatalogInitializationService
    // before the binder probes — staging dirs would otherwise accumulate
    // and waste disk. Returns the number of dirs swept; errors per-dir are
    // logged and the sweep continues.
    Task<int> SweepStagingDirsAsync(CancellationToken ct = default);

    // Fired after every terminal install / uninstall — the set of
    // installed variants may have changed. The dataset schema binder
    // subscribes so the catalog's bound-tables snapshot tracks reality
    // without polling. At-most-one subscriber; the host init service
    // sets this once after construction.
    //
    // Exceptions thrown by the callback are swallowed and logged so a
    // misbehaving binder can't break the install pipeline.
    Func<CancellationToken, Task>? OnVariantsChanged { get; set; }

    // Fired immediately before <see cref="UninstallAsync"/> deletes a
    // variant's ingested folder. The binder subscribes so its mounted
    // <c>DatumFileTableProviderV2</c> for the variant can be torn down
    // (releasing the .datum file handle) BEFORE the recursive delete
    // hits the file system — otherwise the delete throws a sharing
    // violation. At-most-one subscriber; the host init service wires
    // this once. Exceptions are swallowed + logged so a misbehaving
    // subscriber can't block uninstall.
    Action<string>? OnVariantUninstalling { get; set; }
}

// Per-dataset lifecycle state surfaced to the UI. Mirrors
// DatumIngest.ModelLibrary.ModelInstallState but with dataset-specific
// transitions:
//   NotDownloaded -> (download starts) -> Partial (download running, no
//   .datum yet) -> Installed (every ingest job's .datum is on disk).
// Datasets have no installSql step — the .datum files at known paths
// are bound directly into the configured schema by the catalog (see
// DatasetEntry.Schema), so being on disk is the only "ready" condition.
public enum DatasetInstallState
{
    NotDownloaded,
    Partial,     // some .datum tables present, others missing
    Installed,   // every ingest job's .datum is on disk
}
