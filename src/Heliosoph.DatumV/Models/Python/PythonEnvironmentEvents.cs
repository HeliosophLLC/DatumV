namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// Lifecycle events fired by <see cref="IPythonEnvironmentManager"/>
/// during uv / Python / venv installation. The WebUI subscribes via
/// <see cref="IPythonEnvironmentReporter"/> to render progress dialogs
/// and bars; tests subscribe to assert ordering without spinning up
/// real subprocesses.
/// </summary>
/// <remarks>
/// Mirrors the shape of the existing
/// <see cref="Heliosoph.DatumV.ModelLibrary.IDownloadProgressReporter"/>
/// surface so the front-end's download-progress conventions extend
/// naturally. Distinct events (not a single discriminated record)
/// because the manager's stages have different payloads and the
/// receiver method names are the cleanest discriminator at the
/// SignalR / hub boundary.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Documentation", "CS1591",
    Justification = "Event records are self-explanatory; type-level summary documents the group.")]
file static class EventsDoc { }

/// <summary>Fires when the engine begins downloading the uv binary.</summary>
/// <param name="Version">uv version string (e.g. "0.5.7") being downloaded.</param>
/// <param name="TotalBytes">Total bytes the download will fetch, when known. 0 when the server doesn't supply a Content-Length.</param>
public readonly record struct UvDownloadStarted(string Version, long TotalBytes);

/// <summary>Fires periodically while the uv binary downloads.</summary>
/// <param name="BytesDownloaded">Bytes received so far.</param>
/// <param name="TotalBytes">Total bytes expected. 0 when unknown — UIs render an indeterminate bar.</param>
public readonly record struct UvDownloadProgress(long BytesDownloaded, long TotalBytes);

/// <summary>Fires once the uv binary is on disk and verified executable.</summary>
public readonly record struct UvDownloadComplete;

/// <summary>Fires when <c>uv python install &lt;version&gt;</c> begins.</summary>
/// <param name="Version">Python major.minor version being installed.</param>
public readonly record struct PythonInstallStarted(string Version);

/// <summary>Fires periodically during Python installation.</summary>
/// <param name="Stage">Free-form stage label ("downloading", "extracting", "verifying"). Surfaced to the UI as the prompt subtitle.</param>
/// <param name="BytesProcessed">Bytes processed within the current stage.</param>
/// <param name="TotalBytes">Total bytes the stage expects. 0 when uv doesn't report a total.</param>
public readonly record struct PythonInstallProgress(string Stage, long BytesProcessed, long TotalBytes);

/// <summary>Fires once Python is installed and the executable validates.</summary>
/// <param name="Version">The installed Python major.minor version.</param>
public readonly record struct PythonInstallComplete(string Version);

/// <summary>Fires when venv creation + dependency installation begins.</summary>
/// <param name="VenvName">Stable identifier the venv was created under — typically the model name.</param>
/// <param name="Requirements">Verbatim requirement strings (PEP 508) being installed.</param>
public readonly record struct VenvInstallStarted(string VenvName, IReadOnlyList<string> Requirements);

/// <summary>Fires periodically during venv installation.</summary>
/// <param name="VenvName">The venv being installed.</param>
/// <param name="Stage">Free-form stage label ("resolving", "downloading wheel", "linking from cache").</param>
/// <param name="Detail">Per-stage detail — the wheel name being downloaded, the cache hit count, etc.</param>
public readonly record struct VenvInstallProgress(string VenvName, string Stage, string Detail);

/// <summary>Fires once the venv is created and all requirements are installed.</summary>
/// <param name="VenvName">The venv that completed.</param>
public readonly record struct VenvInstallComplete(string VenvName);

/// <summary>Fires when any stage of install fails terminally.</summary>
/// <param name="Stage">Which stage failed — "uv-download", "python-install", "venv-install".</param>
/// <param name="VenvNameOrEmpty">The venv name when applicable; empty string otherwise.</param>
/// <param name="Error">Single-line human-readable error description. Full stack / log lives in the engine trace.</param>
public readonly record struct PythonEnvironmentFailed(string Stage, string VenvNameOrEmpty, string Error);
