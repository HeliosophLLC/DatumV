// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.DatasetLibrary;

// Lifecycle events emitted by DatasetDownloadService through
// IDatasetDownloadProgressReporter. Pure data — hosts that surface these
// over the wire (the Web project's SignalR hub) wrap them in their own
// DTO types and convert at the boundary.
//
// Parallel to DatumIngest.ModelLibrary.ModelDownload* but distinct types:
// dataset install has an `Ingesting` stage (running DatumIngest.Ingestion.Ingester
// against the downloaded source) that doesn't exist on the model side,
// and reusing the model records would force the UI to discriminate on a
// kind field for every event. Two parallel streams stay clearer.

public sealed record DatasetDownloadStarted(
    string DatasetId,
    int FileCount,
    long TotalBytes);

public sealed record DatasetDownloadProgress(
    string DatasetId,
    string CurrentFile,        // destFile name from the catalog source
    int FileIndex,             // 1-based for UX ("3 of 7")
    int FileCount,
    long BytesReadInFile,
    long BytesTotalInFile,
    long BytesReadTotal,       // across all files in this dataset
    long BytesTotalAcrossDataset);

// Files-on-disk phase finished successfully. Followed by DatasetIngesting
// + DatasetIngested as the per-ingest-job .datum files get produced.
public sealed record DatasetDownloadComplete(string DatasetId);

// Emitted when one ingest job starts running. CurrentTable is the
// CatalogIngestJob.TableName the job will produce; JobIndex / JobCount
// drive multi-table progress (`Ingesting 2 of 3: annotations`). For v1
// COCO this fires once with `(images, 1, 1)`.
public sealed record DatasetIngesting(
    string DatasetId,
    string CurrentTable,
    int JobIndex,
    int JobCount);

// One ingest job finished. RowsWritten / BytesWritten come from the
// returned IngestionResult so the UI can surface real totals (eg.
// "ingested 40,670 rows / 6.3 GB"). When all jobs complete, the service
// emits DatasetInstalled.
public sealed record DatasetTableIngested(
    string DatasetId,
    string Table,
    long RowsWritten,
    long BytesWritten);

// Terminal success event. All ingest jobs produced their .datum files
// and any installSql (PR 5+) ran successfully.
public sealed record DatasetInstalled(string DatasetId);

public sealed record DatasetDownloadFailed(string DatasetId, string Error);
