using Tapper;

namespace DatumIngest.Web.Hubs;

// Wire-format DTOs for dataset-download lifecycle events. Mirror 1:1 of
// the core records in DatumIngest.DatasetLibrary, but carry
// [TranspilationSource] so the TypedSignalR TypeScript codegen emits
// matching interfaces for the React client. Conversion happens at the
// host boundary in SignalRDatasetDownloadProgressReporter — core records
// never touch the wire.

[TranspilationSource]
public sealed record DatasetDownloadStartedDto(
    string DatasetId,
    int FileCount,
    long TotalBytes);

[TranspilationSource]
public sealed record DatasetDownloadProgressDto(
    string DatasetId,
    string CurrentFile,
    int FileIndex,
    int FileCount,
    long BytesReadInFile,
    long BytesTotalInFile,
    long BytesReadTotal,
    long BytesTotalAcrossDataset);

[TranspilationSource]
public sealed record DatasetDownloadCompleteDto(string DatasetId);

[TranspilationSource]
public sealed record DatasetIngestingDto(
    string DatasetId,
    string CurrentTable,
    int JobIndex,
    int JobCount);

[TranspilationSource]
public sealed record DatasetIngestProgressDto(
    string DatasetId,
    string CurrentTable,
    long RowsWrittenSoFar);

[TranspilationSource]
public sealed record DatasetTableIngestedDto(
    string DatasetId,
    string Table,
    long RowsWritten,
    long BytesWritten);

[TranspilationSource]
public sealed record DatasetInstalledDto(string DatasetId);

[TranspilationSource]
public sealed record DatasetDownloadFailedDto(string DatasetId, string Error);
