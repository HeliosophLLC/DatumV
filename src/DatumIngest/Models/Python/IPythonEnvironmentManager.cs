namespace DatumIngest.Models.Python;

/// <summary>
/// Engine-managed Python toolchain. Bootstraps <c>uv</c>, installs Python
/// interpreters, materialises per-model virtual environments, and hands
/// back the venv-scoped Python executable path that
/// <see cref="PythonBackedModel"/> spawns. Replaces the v1 design where
/// every Python-backed model resolved its own Python interpreter from
/// <c>DATUM_PYTHON</c> / <c>PATH</c> / a hand-managed venv on disk.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this exists.</strong> Users should never have to install
/// Python themselves, never have to manage venvs, never have to know
/// what <c>uv</c> is. The engine takes responsibility for the toolchain
/// the same way it does for downloading ONNX weights — every install
/// action is prompt-and-authorise via the WebUI's existing dialog
/// surface, and every file the engine creates lives under
/// <c>%LOCALAPPDATA%/DatumIngest/</c> so uninstall is a single directory
/// delete.
/// </para>
/// <para>
/// <strong>Ethical-install contract.</strong> Implementations MUST:
/// <list type="bullet">
///   <item><description>Never modify the user's <c>PATH</c>.</description></item>
///   <item><description>Never touch the user's system Python — every interpreter the engine uses lives in its own managed directory.</description></item>
///   <item><description>Surface progress through <see cref="IPythonEnvironmentReporter"/> so the WebUI can render install dialogs + progress bars.</description></item>
///   <item><description>Be idempotent — calling <see cref="EnsurePythonAsync"/> twice with the same version is a no-op the second time; calling <see cref="EnsureVenvAsync"/> twice with the same requirements is a no-op or a quick cache validation.</description></item>
///   <item><description>Fail fast and loud — an offline machine that can't reach the uv release feed should surface a clear "couldn't fetch uv from <c>github.com/astral-sh/uv/releases</c>" rather than silently falling back to system Python.</description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Thread safety.</strong> Implementations MUST be safe for
/// concurrent calls. Two queries that simultaneously trigger model
/// installation should result in one install pass, not two; followers
/// await the in-flight task. Same pattern as
/// <c>ModelResidencyManager.AcquireAsync</c>'s loader gate.
/// </para>
/// </remarks>
public interface IPythonEnvironmentManager
{
    /// <summary>
    /// Ensures the engine-managed <c>uv</c> binary is present at
    /// <c>%LOCALAPPDATA%/DatumIngest/uv/uv.exe</c> (or platform
    /// equivalent). First call downloads from the configured release
    /// channel; subsequent calls verify the cached binary still runs
    /// and return. Idempotent.
    /// </summary>
    /// <param name="cancellationToken">Caller's cancellation. A cancelled download leaves no partial file on disk.</param>
    /// <returns>The absolute path to the resolved <c>uv</c> executable.</returns>
    Task<string> EnsureUvAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Ensures the requested Python interpreter is installed in the
    /// engine's managed directory. Wraps <c>uv python install &lt;version&gt;</c>.
    /// </summary>
    /// <param name="version">CPython version (e.g. "3.11"). Major.minor; uv resolves the patch.</param>
    /// <param name="cancellationToken">Caller's cancellation.</param>
    /// <returns>The absolute path to the installed <c>python</c> executable.</returns>
    Task<string> EnsurePythonAsync(string version, CancellationToken cancellationToken);

    /// <summary>
    /// Ensures a venv named <paramref name="venvName"/> exists with the
    /// supplied <paramref name="requirements"/> installed. Wraps
    /// <c>uv venv</c> + <c>uv pip install</c>. Re-invoking with the same
    /// requirements is a no-op (uv's resolver returns immediately when
    /// the lock matches). Re-invoking with different requirements
    /// re-resolves; the shared wheel cache means most installs are
    /// hardlink-fast.
    /// </summary>
    /// <param name="venvName">Stable identifier — typically the model name.</param>
    /// <param name="pythonVersion">Python version to base the venv on. Must already be installed via <see cref="EnsurePythonAsync"/>.</param>
    /// <param name="requirements">PEP 508 requirement strings (e.g. "torch>=2.0", "diffusers", "transformers"). Resolution is delegated to uv.</param>
    /// <param name="cancellationToken">Caller's cancellation.</param>
    /// <returns>The absolute path to the venv-scoped <c>python</c> executable.</returns>
    Task<string> EnsureVenvAsync(
        string venvName,
        string pythonVersion,
        IReadOnlyList<string> requirements,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes a venv created by <see cref="EnsureVenvAsync"/>. Used by
    /// <c>DROP MODEL</c> / <c>RESET MODEL</c> surfaces so a model's
    /// disk footprint is fully reclaimable. The shared wheel cache is
    /// untouched — other venvs that share dependencies keep working.
    /// </summary>
    /// <param name="venvName">Identifier passed to <see cref="EnsureVenvAsync"/>.</param>
    /// <param name="cancellationToken">Caller's cancellation. Partial removal isn't retained — re-issued calls re-attempt the delete.</param>
    /// <returns><see langword="true"/> if a venv was removed; <see langword="false"/> when none existed.</returns>
    Task<bool> RemoveVenvAsync(string venvName, CancellationToken cancellationToken);
}
