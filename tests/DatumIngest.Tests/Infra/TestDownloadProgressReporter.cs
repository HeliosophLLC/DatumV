using System.Collections.Concurrent;

using DatumIngest.ModelLibrary;

namespace DatumIngest.Tests.Infra;

/// <summary>
/// Capturing <see cref="IDownloadProgressReporter"/> for tests. Exposes
/// <see cref="WaitForTerminalAsync(string, CancellationToken)"/> so a test
/// can block on a model's lifecycle reaching a terminal state — either
/// success (download complete + optional install) or failure. Without
/// this, <see cref="IModelDownloadService.InstallAsync"/> returns as soon
/// as the download is queued and the test has no way to know when the
/// bytes are actually on disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Terminal events.</strong> For a model without <c>InstallSql</c>,
/// <see cref="OnCompleteAsync"/> is terminal. For a model with
/// <c>InstallSql</c>, the downloader fires <c>OnInstalling</c> →
/// <c>OnInstalled</c> after the download. Either of <see cref="OnCompleteAsync"/>
/// or <see cref="OnInstalledAsync"/> signals "files are on disk and the
/// installer's step (if any) has run" — both fulfil the test's contract
/// equally. <see cref="OnFailedAsync"/> propagates as a faulted task.
/// </para>
/// <para>
/// <strong>Subscription before invocation.</strong> Callers must obtain the
/// completion task BEFORE calling <see cref="IModelDownloadService.InstallAsync"/>.
/// The reporter creates a completion source on first request for a given
/// model id, so a subsequent terminal event resolves it. If the event
/// arrived before subscription (unlikely in tests since InstallAsync is
/// the trigger) the completion source is created in a completed state via
/// the captured <see cref="_terminals"/> map.
/// </para>
/// </remarks>
public sealed class TestDownloadProgressReporter : IDownloadProgressReporter
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _pending = new();
    private readonly ConcurrentDictionary<string, TerminalKind> _terminals = new();

    private enum TerminalKind
    {
        Succeeded,
        Failed,
    }

    /// <summary>
    /// Returns a task that completes when the model reaches a terminal
    /// state. Resolves to success on OnComplete/OnInstalled; faults on
    /// OnFailed.
    /// </summary>
    public Task WaitForTerminalAsync(string modelId, CancellationToken ct)
    {
        TaskCompletionSource tcs = _pending.GetOrAdd(
            modelId,
            _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        // If a terminal event already fired before subscription, replay
        // it now. (Order-of-operations safety net; tests subscribe before
        // calling InstallAsync so this is rarely needed.)
        if (_terminals.TryGetValue(modelId, out TerminalKind kind))
        {
            if (kind == TerminalKind.Succeeded) tcs.TrySetResult();
            else tcs.TrySetException(new InvalidOperationException($"Model '{modelId}' download failed."));
        }

        // Cancellation flips the task into a faulted state — the test's
        // timeout / cancellation token propagates here.
        ct.Register(() => tcs.TrySetCanceled(ct));
        return tcs.Task;
    }

    public ValueTask OnStartedAsync(ModelDownloadStarted started, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnProgressAsync(ModelDownloadProgress progress, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnCompleteAsync(ModelDownloadComplete complete, CancellationToken ct)
    {
        Signal(complete.ModelId, TerminalKind.Succeeded, error: null);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnInstallingAsync(ModelInstalling installing, CancellationToken ct)
        => ValueTask.CompletedTask;

    public ValueTask OnInstalledAsync(ModelInstalled installed, CancellationToken ct)
    {
        Signal(installed.ModelId, TerminalKind.Succeeded, error: null);
        return ValueTask.CompletedTask;
    }

    public ValueTask OnFailedAsync(ModelDownloadFailed failed, CancellationToken ct)
    {
        Signal(failed.ModelId, TerminalKind.Failed, error: failed.Error);
        return ValueTask.CompletedTask;
    }

    private void Signal(string modelId, TerminalKind kind, string? error)
    {
        _terminals[modelId] = kind;
        if (_pending.TryGetValue(modelId, out TaskCompletionSource? tcs))
        {
            if (kind == TerminalKind.Succeeded)
            {
                tcs.TrySetResult();
            }
            else
            {
                tcs.TrySetException(new InvalidOperationException(
                    $"Model '{modelId}' download failed: {error}"));
            }
        }
    }
}
