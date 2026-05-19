namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// Observer surface for <see cref="IPythonEnvironmentManager"/> install
/// lifecycle events. Hosts that want to surface install progress to a
/// UI (the WebUI's install dialogs + progress bars, an admin CLI, a
/// TUI) implement this and register it via DI.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Calling convention.</strong> Methods are invoked from the
/// manager's install task. Implementations MUST return quickly and
/// MUST NOT block on I/O or grab engine-side locks — a slow reporter
/// extends every install step's measured duration. The
/// <see cref="NullPythonEnvironmentReporter"/> default is a no-op so
/// hosts that don't wire a reporter pay nothing.
/// </para>
/// <para>
/// <strong>Failure propagation.</strong> A reporter that throws is
/// swallowed by the manager — observability is best-effort, not part
/// of the install correctness path.
/// </para>
/// </remarks>
public interface IPythonEnvironmentReporter
{
    /// <summary>Forwards <see cref="UvDownloadStarted"/> to the UI.</summary>
    ValueTask OnUvDownloadStartedAsync(UvDownloadStarted e, CancellationToken ct);

    /// <summary>Forwards <see cref="UvDownloadProgress"/> to the UI.</summary>
    ValueTask OnUvDownloadProgressAsync(UvDownloadProgress e, CancellationToken ct);

    /// <summary>Forwards <see cref="UvDownloadComplete"/> to the UI.</summary>
    ValueTask OnUvDownloadCompleteAsync(UvDownloadComplete e, CancellationToken ct);

    /// <summary>Forwards <see cref="PythonInstallStarted"/> to the UI.</summary>
    ValueTask OnPythonInstallStartedAsync(PythonInstallStarted e, CancellationToken ct);

    /// <summary>Forwards <see cref="PythonInstallProgress"/> to the UI.</summary>
    ValueTask OnPythonInstallProgressAsync(PythonInstallProgress e, CancellationToken ct);

    /// <summary>Forwards <see cref="PythonInstallComplete"/> to the UI.</summary>
    ValueTask OnPythonInstallCompleteAsync(PythonInstallComplete e, CancellationToken ct);

    /// <summary>Forwards <see cref="VenvInstallStarted"/> to the UI.</summary>
    ValueTask OnVenvInstallStartedAsync(VenvInstallStarted e, CancellationToken ct);

    /// <summary>Forwards <see cref="VenvInstallProgress"/> to the UI.</summary>
    ValueTask OnVenvInstallProgressAsync(VenvInstallProgress e, CancellationToken ct);

    /// <summary>Forwards <see cref="VenvInstallComplete"/> to the UI.</summary>
    ValueTask OnVenvInstallCompleteAsync(VenvInstallComplete e, CancellationToken ct);

    /// <summary>Forwards <see cref="PythonEnvironmentFailed"/> to the UI.</summary>
    ValueTask OnFailedAsync(PythonEnvironmentFailed e, CancellationToken ct);
}

/// <summary>
/// No-op reporter used when no host-level subscriber is wired. Lets
/// the manager unconditionally invoke its reporter without null
/// checks; the wire-up cost when nothing's listening is one virtual
/// dispatch returning a completed <see cref="ValueTask"/>.
/// </summary>
public sealed class NullPythonEnvironmentReporter : IPythonEnvironmentReporter
{
    /// <summary>Process-wide shared instance.</summary>
    public static readonly NullPythonEnvironmentReporter Instance = new();

    private NullPythonEnvironmentReporter() { }

    /// <inheritdoc/>
    public ValueTask OnUvDownloadStartedAsync(UvDownloadStarted e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnUvDownloadProgressAsync(UvDownloadProgress e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnUvDownloadCompleteAsync(UvDownloadComplete e, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask OnPythonInstallStartedAsync(PythonInstallStarted e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnPythonInstallProgressAsync(PythonInstallProgress e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnPythonInstallCompleteAsync(PythonInstallComplete e, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask OnVenvInstallStartedAsync(VenvInstallStarted e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnVenvInstallProgressAsync(VenvInstallProgress e, CancellationToken ct) => ValueTask.CompletedTask;
    /// <inheritdoc/>
    public ValueTask OnVenvInstallCompleteAsync(VenvInstallComplete e, CancellationToken ct) => ValueTask.CompletedTask;

    /// <inheritdoc/>
    public ValueTask OnFailedAsync(PythonEnvironmentFailed e, CancellationToken ct) => ValueTask.CompletedTask;
}
