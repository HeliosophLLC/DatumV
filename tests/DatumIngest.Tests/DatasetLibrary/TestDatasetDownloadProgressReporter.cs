using System.Collections.Concurrent;

using DatumIngest.DatasetLibrary;

namespace DatumIngest.Tests.DatasetLibrary;

/// <summary>
/// Capturing <see cref="IDatasetDownloadProgressReporter"/> for tests.
/// Mirrors <see cref="DatumIngest.Tests.Infra.TestDownloadProgressReporter"/>:
/// exposes <see cref="WaitForTerminalAsync"/> so a test can block on a
/// dataset install reaching a terminal state (success via
/// <see cref="OnInstalledAsync"/>, failure via
/// <see cref="OnFailedAsync"/>).
/// </summary>
public sealed class TestDatasetDownloadProgressReporter : IDatasetDownloadProgressReporter
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private readonly ConcurrentDictionary<string, TerminalKind> _terminals = new();
    private readonly ConcurrentDictionary<string, string?> _errors = new();

    public ConcurrentBag<DatasetDownloadStarted> Started { get; } = [];
    public ConcurrentBag<DatasetDownloadProgress> Progresses { get; } = [];
    public ConcurrentBag<DatasetDownloadComplete> Completes { get; } = [];
    public ConcurrentBag<DatasetIngesting> Ingestings { get; } = [];
    public ConcurrentBag<DatasetTableIngested> TableIngesteds { get; } = [];
    public ConcurrentBag<DatasetInstalled> Installeds { get; } = [];
    public ConcurrentBag<DatasetDownloadFailed> Faileds { get; } = [];

    private enum TerminalKind { Succeeded, Failed }

    public Task WaitForTerminalAsync(string datasetId, CancellationToken ct)
    {
        TaskCompletionSource tcs = _pending.GetOrAdd(
            datasetId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        if (_terminals.TryGetValue(datasetId, out TerminalKind kind))
        {
            if (kind == TerminalKind.Succeeded) tcs.TrySetResult();
            else tcs.TrySetException(new InvalidOperationException(
                $"Dataset '{datasetId}' install failed: {_errors.GetValueOrDefault(datasetId)}"));
        }

        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public ValueTask OnStartedAsync(DatasetDownloadStarted e, CancellationToken ct)
    {
        Started.Add(e);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnProgressAsync(DatasetDownloadProgress e, CancellationToken ct)
    {
        Progresses.Add(e);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnCompleteAsync(DatasetDownloadComplete e, CancellationToken ct)
    {
        Completes.Add(e);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnIngestingAsync(DatasetIngesting e, CancellationToken ct)
    {
        Ingestings.Add(e);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnTableIngestedAsync(DatasetTableIngested e, CancellationToken ct)
    {
        TableIngesteds.Add(e);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnInstalledAsync(DatasetInstalled e, CancellationToken ct)
    {
        Installeds.Add(e);
        Signal(e.DatasetId, TerminalKind.Succeeded, error: null);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFailedAsync(DatasetDownloadFailed e, CancellationToken ct)
    {
        Faileds.Add(e);
        Signal(e.DatasetId, TerminalKind.Failed, error: e.Error);
        return ValueTask.CompletedTask;
    }

    private void Signal(string datasetId, TerminalKind kind, string? error)
    {
        _terminals[datasetId] = kind;
        _errors[datasetId] = error;
        if (_pending.TryGetValue(datasetId, out TaskCompletionSource? tcs))
        {
            if (kind == TerminalKind.Succeeded) tcs.TrySetResult();
            else tcs.TrySetException(new InvalidOperationException(
                $"Dataset '{datasetId}' install failed: {error}"));
        }
    }
}
