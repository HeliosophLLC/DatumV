using Heliosoph.DatumV.Models.Python;

namespace Heliosoph.DatumV.Tests.Models.Python;

/// <summary>
/// Test-only <see cref="IPythonEnvironmentManager"/> that returns whatever
/// <c>python</c> is on PATH (or <c>$DATUMV_PYTHON</c>) and treats every
/// "ensure" call as a no-op. Lets the echo-worker round-trip tests run
/// against the host's system Python without bootstrapping uv, downloading
/// a managed CPython, or provisioning a venv — none of which the echo
/// worker needs (zero PyPI dependencies, just <c>python_worker_host</c>
/// from the same scripts dir).
/// </summary>
/// <remarks>
/// Skips real installer behaviour entirely. Tests that exercise the
/// installer (download progress, venv creation, requirement resolution)
/// should target <see cref="PythonEnvironmentManager"/> directly with a
/// stubbed HTTP handler — not the path these tests take.
/// </remarks>
internal sealed class SystemPythonEnvironmentManager : IPythonEnvironmentManager
{
    private readonly string _pythonPath;

    public SystemPythonEnvironmentManager()
    {
        // DATUMV_PYTHON overrides PATH for hosts that have multiple Pythons
        // and want tests to pin a specific one. Mirrors the convention
        // PythonAvailable() in PythonBackedModelTests uses.
        _pythonPath = Environment.GetEnvironmentVariable("DATUMV_PYTHON") ?? "python";
    }

    public Task<string> EnsureUvAsync(CancellationToken cancellationToken)
        => Task.FromResult("uv");

    public Task<string> EnsurePythonAsync(string version, CancellationToken cancellationToken)
        => Task.FromResult(_pythonPath);

    public Task<string> EnsureVenvAsync(
        string venvName,
        string pythonVersion,
        IReadOnlyList<string> requirements,
        CancellationToken cancellationToken)
        => Task.FromResult(_pythonPath);

    public Task<bool> RemoveVenvAsync(string venvName, CancellationToken cancellationToken)
        => Task.FromResult(false);
}
