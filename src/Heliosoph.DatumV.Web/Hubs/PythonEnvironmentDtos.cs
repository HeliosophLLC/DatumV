using Tapper;

namespace Heliosoph.DatumV.Web.Hubs;

// Wire-format DTOs mirroring the Python env event records in
// Heliosoph.DatumV.Models.Python. Same layering rationale as ModelDownloadDtos:
// Tapper attributes live on the Web side so TypedSignalR's TypeScript
// codegen emits matching interfaces for the React client, without
// dragging that concern into the core engine.
//
// Why ints/longs match 1:1: uv/python install events come from a single
// machine-scoped manager and aren't keyed by modelId. The active venv
// install IS keyed (VenvName == catalog id) so the client can correlate
// it back to the model card the user clicked Install on.

/// <summary>Wire DTO for <c>UvDownloadStarted</c>.</summary>
/// <param name="Version">uv version being downloaded.</param>
/// <param name="TotalBytes">Total bytes; 0 when unknown.</param>
[TranspilationSource]
public sealed record UvDownloadStartedDto(string Version, long TotalBytes);

/// <summary>Wire DTO for <c>UvDownloadProgress</c>.</summary>
/// <param name="BytesDownloaded">Bytes received so far.</param>
/// <param name="TotalBytes">Total expected; 0 when unknown.</param>
[TranspilationSource]
public sealed record UvDownloadProgressDto(long BytesDownloaded, long TotalBytes);

/// <summary>Wire DTO for <c>UvDownloadComplete</c>.</summary>
[TranspilationSource]
public sealed record UvDownloadCompleteDto();

/// <summary>Wire DTO for <c>PythonInstallStarted</c>.</summary>
/// <param name="Version">Python version being installed.</param>
[TranspilationSource]
public sealed record PythonInstallStartedDto(string Version);

/// <summary>Wire DTO for <c>PythonInstallProgress</c>.</summary>
/// <param name="Stage">Free-form stage label.</param>
/// <param name="BytesProcessed">Bytes processed within stage.</param>
/// <param name="TotalBytes">Total expected; 0 when unknown.</param>
[TranspilationSource]
public sealed record PythonInstallProgressDto(string Stage, long BytesProcessed, long TotalBytes);

/// <summary>Wire DTO for <c>PythonInstallComplete</c>.</summary>
/// <param name="Version">Installed Python version.</param>
[TranspilationSource]
public sealed record PythonInstallCompleteDto(string Version);

/// <summary>Wire DTO for <c>VenvInstallStarted</c>.</summary>
/// <param name="VenvName">Venv identifier; matches the catalog id.</param>
/// <param name="Requirements">PEP-508 requirement strings being installed.</param>
[TranspilationSource]
public sealed record VenvInstallStartedDto(string VenvName, IReadOnlyList<string> Requirements);

/// <summary>Wire DTO for <c>VenvInstallProgress</c>.</summary>
/// <param name="VenvName">Venv identifier.</param>
/// <param name="Stage">Free-form stage label.</param>
/// <param name="Detail">Per-stage detail (wheel name, cache hits, etc).</param>
[TranspilationSource]
public sealed record VenvInstallProgressDto(string VenvName, string Stage, string Detail);

/// <summary>Wire DTO for <c>VenvInstallComplete</c>.</summary>
/// <param name="VenvName">Venv that completed.</param>
[TranspilationSource]
public sealed record VenvInstallCompleteDto(string VenvName);

/// <summary>Wire DTO for <c>PythonEnvironmentFailed</c>.</summary>
/// <param name="Stage">"uv-download" | "python-install" | "venv-install".</param>
/// <param name="VenvNameOrEmpty">Venv name when applicable; empty otherwise.</param>
/// <param name="Error">Single-line human-readable error.</param>
[TranspilationSource]
public sealed record PythonEnvironmentFailedDto(string Stage, string VenvNameOrEmpty, string Error);
