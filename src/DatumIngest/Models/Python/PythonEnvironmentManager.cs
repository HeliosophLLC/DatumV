using System.Diagnostics;
using System.IO.Compression;

namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// Concrete <see cref="IPythonEnvironmentManager"/> backed by
/// <c>uv</c>. Stores managed Python interpreters under
/// <c>%LOCALAPPDATA%/Heliosoph.DatumV/python/</c> and venvs under
/// <c>%LOCALAPPDATA%/Heliosoph.DatumV/venvs/&lt;name&gt;/</c>; uv's shared
/// wheel cache lives at uv's default location (<c>%LOCALAPPDATA%/uv/cache/</c>
/// on Windows, <c>~/.cache/uv/</c> on Linux/macOS) so disparate venvs
/// share their torch / transformers installs via hardlinks.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Status.</strong> Skeleton only — none of the install methods
/// have an implementation yet. They throw
/// <see cref="NotImplementedException"/>. The interface contract, DI
/// wiring, reporter shape, and managed-directory layout are all
/// settled here so subsequent PRs can plug real install logic in
/// without churning the surface every consumer touches.
/// </para>
/// <para>
/// <strong>Why a class instead of static helpers.</strong>
/// Testability: the
/// <see cref="IPythonEnvironmentManager"/> seam lets tests substitute
/// a fake that "installs" without spawning real subprocesses, and
/// lets the existing
/// <see cref="Heliosoph.DatumV.Models.Python.PythonBackedModel"/> in PR 6
/// depend on the abstraction rather than concrete file paths.
/// </para>
/// </remarks>
public sealed class PythonEnvironmentManager : IPythonEnvironmentManager
{
    /// <summary>
    /// Root directory for engine-managed Python state. All managed
    /// interpreters, venvs, and uv binary live under here. A single
    /// directory delete (or "Reset Python environments" button in
    /// Settings) reclaims everything.
    /// </summary>
    public static string DefaultRootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Heliosoph.DatumV");

    /// <summary>
    /// uv release identifier to fetch when the cached binary is
    /// missing. <c>"latest"</c> uses GitHub's <c>releases/latest/download/</c>
    /// redirect; pin a specific tag (e.g. <c>"0.5.7"</c>) for
    /// reproducibility. Constant for now; a Settings-driven override
    /// lands when there's a real reason to want one (e.g. users
    /// reporting compatibility issues with a new release).
    /// </summary>
    public const string DefaultUvVersion = "latest";

    private readonly string _rootDirectory;
    private readonly IPythonEnvironmentReporter _reporter;
    private readonly HttpClient _http;
    private readonly string _uvVersion;

    // One-installer-at-a-time gate — concurrent EnsureUvAsync callers
    // share the in-flight install task rather than racing two
    // simultaneous downloads. Same pattern as
    // ModelResidencyManager._loadGate.
    private readonly SemaphoreSlim _uvInstallGate = new(1, 1);

    /// <summary>
    /// Creates a manager rooted at <paramref name="rootDirectory"/>.
    /// <see langword="null"/> uses <see cref="DefaultRootDirectory"/>.
    /// Reporter defaults to <see cref="NullPythonEnvironmentReporter.Instance"/>;
    /// hosts that want UI progress wire a SignalR-backed reporter via
    /// DI. <paramref name="http"/> is injectable for testability —
    /// production wiring uses a singleton <see cref="HttpClient"/>,
    /// tests pass one backed by a fake handler.
    /// </summary>
    public PythonEnvironmentManager(
        string? rootDirectory = null,
        IPythonEnvironmentReporter? reporter = null,
        HttpClient? http = null,
        string? uvVersion = null)
    {
        _rootDirectory = rootDirectory ?? DefaultRootDirectory;
        _reporter = reporter ?? NullPythonEnvironmentReporter.Instance;
        _http = http ?? new HttpClient();
        _uvVersion = uvVersion ?? DefaultUvVersion;
    }

    /// <summary>Absolute path under which uv, Python, and venvs live.</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>The reporter receiving install lifecycle events.</summary>
    internal IPythonEnvironmentReporter Reporter => _reporter;

    /// <summary>Expected path of the cached uv binary.</summary>
    public string UvBinaryPath => Path.Combine(_rootDirectory, "uv", UvBinaryName);

    /// <summary>Directory that holds managed Python interpreters by version (<c>python/3.11/</c>).</summary>
    public string PythonInstallDirectory => Path.Combine(_rootDirectory, "python");

    /// <summary>Directory that holds per-model venvs (<c>venvs/&lt;name&gt;/</c>).</summary>
    public string VenvsDirectory => Path.Combine(_rootDirectory, "venvs");

    /// <inheritdoc/>
    public async Task<string> EnsureUvAsync(CancellationToken cancellationToken)
    {
        // Fast path: cached binary exists and runs. We verify by
        // executing `uv --version` rather than just checking file
        // existence — a half-downloaded binary or one orphaned by a
        // failed extract would pass an existence check and fail on
        // first real use.
        if (File.Exists(UvBinaryPath) && await VerifyUvAsync(UvBinaryPath, cancellationToken).ConfigureAwait(false))
        {
            return UvBinaryPath;
        }

        // Serialise concurrent installs. Two callers entering here
        // simultaneously would otherwise race the download + extract
        // + replace. The lock is contested only on first-ever-install;
        // steady-state callers fast-path above without entering.
        await _uvInstallGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check the fast path under the lock — another caller
            // may have just completed the install while we were
            // waiting.
            if (File.Exists(UvBinaryPath) && await VerifyUvAsync(UvBinaryPath, cancellationToken).ConfigureAwait(false))
            {
                return UvBinaryPath;
            }

            try
            {
                await DownloadAndInstallUvAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await _reporter.OnFailedAsync(
                    new PythonEnvironmentFailed(
                        Stage: "uv-download",
                        VenvNameOrEmpty: "",
                        Error: $"{ex.GetType().Name}: {ex.Message}"),
                    cancellationToken).ConfigureAwait(false);
                throw;
            }

            return UvBinaryPath;
        }
        finally
        {
            _uvInstallGate.Release();
        }
    }

    /// <summary>
    /// Spawns the candidate <c>uv</c> binary with <c>--version</c> and
    /// returns true when it exits 0. Acts as the freshness gate: a
    /// half-extracted or wrong-arch binary fails here and the caller
    /// re-downloads.
    /// </summary>
    private static async Task<bool> VerifyUvAsync(string uvPath, CancellationToken cancellationToken)
    {
        try
        {
            using Process proc = new();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = uvPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            if (!proc.Start()) return false;
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return proc.ExitCode == 0;
        }
        catch
        {
            // Any failure (missing dep, wrong arch, permission denied,
            // file-not-found race) means "this binary isn't usable" —
            // which is the same answer as "doesn't exist." The caller
            // re-downloads either way.
            return false;
        }
    }

    /// <summary>
    /// Downloads the platform-appropriate uv archive from GitHub,
    /// extracts the <c>uv</c> binary, and atomically swaps it into
    /// <see cref="UvBinaryPath"/>. Reports progress via the bound
    /// <see cref="IPythonEnvironmentReporter"/>.
    /// </summary>
    private async Task DownloadAndInstallUvAsync(CancellationToken cancellationToken)
    {
        UvReleaseAsset asset = UvReleaseAsset.ForCurrentPlatform();
        string downloadUrl = asset.DownloadUrl(_uvVersion);

        string uvDir = Path.GetDirectoryName(UvBinaryPath)!;
        Directory.CreateDirectory(uvDir);

        // Download to a temp file alongside the target so a crash
        // mid-stream leaves a leftover we can spot rather than a
        // half-written uv.exe that future runs would try to execute.
        // Preserve the asset's real extension (.zip on Windows,
        // .tar.gz elsewhere) — ExtractUvBinary dispatches off it.
        string tempArchivePath = Path.Combine(uvDir, asset.ArchiveFileName + ".download.tmp");
        try
        {
            using (HttpResponseMessage response = await _http
                .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                long totalBytes = response.Content.Headers.ContentLength ?? 0;

                await _reporter.OnUvDownloadStartedAsync(
                    new UvDownloadStarted(_uvVersion, totalBytes),
                    cancellationToken).ConfigureAwait(false);

                using FileStream output = File.Create(tempArchivePath);
                using Stream input = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                byte[] buffer = new byte[81920];
                long bytesRead = 0;
                int n;
                while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                    bytesRead += n;
                    await _reporter.OnUvDownloadProgressAsync(
                        new UvDownloadProgress(bytesRead, totalBytes),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // Extract the uv binary out of the archive to a temp path,
            // then atomic-rename onto UvBinaryPath. Two-step so a
            // partial extract doesn't leave a usable-looking binary at
            // the cache path; the rename is atomic on every platform
            // we target.
            string tempBinaryPath = UvBinaryPath + ".new";
            try
            {
                ExtractUvBinary(tempArchivePath, asset.ArchiveFileName, asset.ArchiveEntryName, tempBinaryPath);

                // Linux/macOS need the execute bit. ZIP archives
                // (Windows) carry no Unix permission bits, but
                // tar.gz does — ExtractUvBinary handles tar perms;
                // we set 0o755 here as a belt-and-braces for the
                // zip path.
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(tempBinaryPath,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }

                // Replace any existing binary atomically. File.Move
                // with overwrite=true is rename(2) on Linux/macOS and
                // ReplaceFile on Windows — both atomic on the same
                // filesystem.
                File.Move(tempBinaryPath, UvBinaryPath, overwrite: true);

                // On macOS, strip the Gatekeeper quarantine xattr
                // that gets attached to anything downloaded via HTTP.
                // Astral notarises uv so the attribute usually
                // doesn't gate execution, but it sometimes surfaces
                // a one-time "uv cannot be opened" prompt on first
                // run — removing the xattr is the standard defensive
                // step that the official `uv` installer script also
                // takes. No-op on non-macOS hosts.
                await TryRemoveMacOSQuarantineAsync(UvBinaryPath, cancellationToken)
                    .ConfigureAwait(false);
            }
            finally
            {
                if (File.Exists(tempBinaryPath))
                {
                    try { File.Delete(tempBinaryPath); } catch { /* best-effort cleanup */ }
                }
            }

            await _reporter.OnUvDownloadCompleteAsync(default, cancellationToken).ConfigureAwait(false);

            // Smoke-test the freshly-installed binary. If verification
            // fails here we've already committed it to disk — the next
            // EnsureUvAsync call will see the cached path, fail
            // verification, and re-download. Loud throw rather than
            // silently leaving a bad binary cached.
            if (!await VerifyUvAsync(UvBinaryPath, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException(
                    $"Downloaded uv binary at '{UvBinaryPath}' failed to run --version. "
                    + "Archive may be corrupt or built for a different architecture.");
            }
        }
        finally
        {
            if (File.Exists(tempArchivePath))
            {
                try { File.Delete(tempArchivePath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    /// <summary>
    /// On macOS, strips the <c>com.apple.quarantine</c> extended
    /// attribute from a freshly-downloaded binary so Gatekeeper
    /// doesn't surface a one-time "developer cannot be verified"
    /// prompt on first execution. No-op on non-macOS hosts. Failures
    /// (xattr binary missing, attribute already absent, permission
    /// denied) are swallowed — the worst case is the user sees
    /// Gatekeeper's prompt and clicks through once.
    /// </summary>
    private static async Task TryRemoveMacOSQuarantineAsync(string path, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsMacOS()) return;

        try
        {
            using Process proc = new();
            proc.StartInfo = new ProcessStartInfo
            {
                FileName = "xattr",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            proc.StartInfo.ArgumentList.Add("-d");
            proc.StartInfo.ArgumentList.Add("com.apple.quarantine");
            proc.StartInfo.ArgumentList.Add(path);
            if (!proc.Start()) return;
            await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            // Non-zero exit is the common case where the attribute
            // wasn't set at all (notarised binary, or extracted from
            // a tar that didn't carry the flag). Either way the
            // binary is fine to execute.
        }
        catch
        {
            // xattr CLI missing on this machine, permission denied,
            // or some other rare failure — the binary either works
            // anyway (Gatekeeper-allowed) or surfaces a clear
            // "developer cannot be verified" prompt on next launch.
            // Swallowing here keeps install non-fatal.
        }
    }

    /// <summary>
    /// Pulls the named entry out of <paramref name="archivePath"/> and
    /// writes it to <paramref name="targetPath"/>. Dispatches off
    /// <paramref name="archiveFileName"/> (the asset's canonical name)
    /// rather than the path's extension — the on-disk file is a temp
    /// name (<c>foo.download.tmp</c>) that loses the type signal.
    /// Windows ships uv as a zip, Linux/macOS as tar.gz.
    /// </summary>
    private static void ExtractUvBinary(string archivePath, string archiveFileName, string entryName, string targetPath)
    {
        if (archiveFileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using ZipArchive zip = ZipFile.OpenRead(archivePath);
            ZipArchiveEntry? entry = zip.GetEntry(entryName)
                ?? zip.Entries.FirstOrDefault(e =>
                    string.Equals(Path.GetFileName(e.FullName), entryName, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidOperationException(
                    $"uv zip archive at '{archivePath}' has no entry named '{entryName}'. "
                    + $"Archive contents: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");
            }
            using FileStream output = File.Create(targetPath);
            using Stream input = entry.Open();
            input.CopyTo(output);
        }
        else if (archiveFileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            // System.Formats.Tar handles the inner tar; GZipStream
            // handles the outer gzip. Stream-once — no temp tar file.
            using FileStream gz = File.OpenRead(archivePath);
            using System.IO.Compression.GZipStream decompressed = new(gz, CompressionMode.Decompress);
            using System.Formats.Tar.TarReader reader = new(decompressed);
            while (reader.GetNextEntry() is System.Formats.Tar.TarEntry entry)
            {
                if (string.Equals(Path.GetFileName(entry.Name), entryName, StringComparison.OrdinalIgnoreCase))
                {
                    using FileStream output = File.Create(targetPath);
                    entry.DataStream?.CopyTo(output);
                    return;
                }
            }
            throw new InvalidOperationException(
                $"uv tar.gz archive at '{archivePath}' has no entry named '{entryName}'.");
        }
        else
        {
            throw new InvalidOperationException(
                $"Unknown uv archive format '{archiveFileName}' at '{archivePath}'. Expected .zip or .tar.gz.");
        }
    }

    /// <inheritdoc/>
    public async Task<string> EnsurePythonAsync(string version, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        string uv = await EnsureUvAsync(cancellationToken).ConfigureAwait(false);

        await _reporter.OnPythonInstallStartedAsync(
            new PythonInstallStarted(version), cancellationToken).ConfigureAwait(false);

        try
        {
            // `uv python install <version>` is idempotent — re-invoking
            // when the version is already installed exits 0 quickly
            // with a "Python <ver> already installed" line. Forcing
            // installs under the engine's managed root via
            // UV_PYTHON_INSTALL_DIR keeps the interpreters out of
            // uv's default location and under the same directory tree
            // we already promised to clean up on uninstall.
            //
            // Progress shape: uv's install output is human-formatted
            // stderr text, not structured. Each stderr line streams
            // through as an opaque "Stage" string. UI gets
            // "Downloading cpython-..." / "Extracting..." /
            // "Installed Python 3.11.x" — not pixel-precise bars,
            // but informative.
            int exitCode = await RunUvAsync(
                uv,
                arguments: ["python", "install", version],
                onStderrLine: line => OnPythonInstallStderrLineAsync(version, line, cancellationToken),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (exitCode != 0)
            {
                throw new InvalidOperationException(
                    $"`uv python install {version}` exited with code {exitCode}.");
            }

            // Find the freshly-installed interpreter. uv exposes both
            // a managed lookup (--managed-python) that scopes to its
            // own installs, and a plain `uv python find` that walks
            // PATH + managed locations. We use --managed-python here
            // so a user with a system Python doesn't shadow our
            // managed install.
            string? pythonPath = await FindManagedPythonAsync(uv, version, cancellationToken)
                .ConfigureAwait(false);
            if (pythonPath is null)
            {
                throw new InvalidOperationException(
                    $"`uv python install {version}` reported success but `uv python find` "
                    + "could not locate the interpreter. Try clearing the install directory "
                    + $"at '{PythonInstallDirectory}' and re-running.");
            }

            await _reporter.OnPythonInstallCompleteAsync(
                new PythonInstallComplete(version), cancellationToken).ConfigureAwait(false);

            return pythonPath;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new PythonEnvironmentFailed(
                    Stage: "python-install",
                    VenvNameOrEmpty: "",
                    Error: $"{ex.GetType().Name}: {ex.Message}"),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Resolves the absolute path of an engine-managed Python
    /// interpreter via <c>uv python find --managed-python &lt;version&gt;</c>.
    /// Returns <see langword="null"/> when uv reports no match —
    /// distinct from "uv exits non-zero" (which raises in the caller).
    /// </summary>
    private async Task<string?> FindManagedPythonAsync(string uvPath, string version, CancellationToken cancellationToken)
    {
        using Process proc = new();
        proc.StartInfo = BuildUvStartInfo(uvPath, ["python", "find", "--managed-python", version]);
        proc.StartInfo.RedirectStandardOutput = true;
        if (!proc.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start '{uvPath} python find {version}'.");
        }
        string stdout = await proc.StandardOutput
            .ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (proc.ExitCode != 0) return null;

        string trimmed = stdout.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private async ValueTask OnPythonInstallStderrLineAsync(string version, string line, CancellationToken ct)
    {
        await _reporter.OnPythonInstallProgressAsync(
            new PythonInstallProgress(Stage: line, BytesProcessed: 0, TotalBytes: 0),
            ct).ConfigureAwait(false);
        _ = version; // kept on the signature so future protocol changes can tag events with it
    }

    /// <summary>
    /// Spawns <c>uv</c> with the given arguments, streams stderr
    /// line-by-line to <paramref name="onStderrLine"/>, and returns
    /// the process's exit code. Standard helper for every uv
    /// subcommand the manager invokes.
    /// </summary>
    private async Task<int> RunUvAsync(
        string uvPath,
        IReadOnlyList<string> arguments,
        Func<string, ValueTask> onStderrLine,
        CancellationToken cancellationToken)
    {
        using Process proc = new();
        proc.StartInfo = BuildUvStartInfo(uvPath, arguments);
        proc.StartInfo.RedirectStandardOutput = true;
        proc.StartInfo.RedirectStandardError = true;

        if (!proc.Start())
        {
            throw new InvalidOperationException(
                $"Failed to start '{uvPath} {string.Join(' ', arguments)}'.");
        }

        // Drain both streams concurrently to avoid deadlock — a child
        // that fills its stderr pipe while we're only reading stdout
        // would otherwise block forever waiting for us to drain.
        // Stdout is buffered to a string (most uv output that matters
        // goes to stderr); stderr is line-streamed to the callback.
        Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        Task stderrTask = DrainStderrAsync(proc.StandardError, onStderrLine, cancellationToken);

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        return proc.ExitCode;
    }

    private static async Task DrainStderrAsync(
        StreamReader stderr,
        Func<string, ValueTask> onLine,
        CancellationToken cancellationToken)
    {
        while (await stderr.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            await onLine(line).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Builds a <see cref="ProcessStartInfo"/> wired to invoke
    /// <paramref name="uvPath"/> with the engine's managed directories
    /// in scope. Sets <c>UV_PYTHON_INSTALL_DIR</c> so installed
    /// interpreters land under <see cref="PythonInstallDirectory"/>
    /// rather than uv's default location; sets <c>UV_NO_PROGRESS</c>
    /// so uv emits stable line-per-event output instead of redrawing
    /// progress bars (which would smash our line-streaming).
    /// </summary>
    private ProcessStartInfo BuildUvStartInfo(string uvPath, IReadOnlyList<string> arguments)
    {
        ProcessStartInfo psi = new()
        {
            FileName = uvPath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string arg in arguments) psi.ArgumentList.Add(arg);
        psi.Environment["UV_PYTHON_INSTALL_DIR"] = PythonInstallDirectory;
        psi.Environment["UV_NO_PROGRESS"] = "1";
        return psi;
    }

    /// <inheritdoc/>
    public async Task<string> EnsureVenvAsync(
        string venvName,
        string pythonVersion,
        IReadOnlyList<string> requirements,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(venvName);
        ArgumentException.ThrowIfNullOrEmpty(pythonVersion);
        ArgumentNullException.ThrowIfNull(requirements);

        // Ensure the prerequisites are on disk before we touch the
        // venv. Both calls are idempotent fast-path-after-first-use.
        string uv = await EnsureUvAsync(cancellationToken).ConfigureAwait(false);
        string pythonPath = await EnsurePythonAsync(pythonVersion, cancellationToken).ConfigureAwait(false);

        string venvDir = GetVenvDirectory(venvName);
        string venvPython = GetVenvPythonPath(venvName);
        string requirementsManifestPath = Path.Combine(venvDir, "datum-requirements.txt");

        // Fast path: venv already exists, its python runs, and its
        // saved requirements manifest matches what we'd install now.
        // The manifest is a sorted-line representation of the input
        // list — exact equality is the cheap "nothing changed" gate.
        // Different requirements re-run the install, which uv handles
        // efficiently because most wheels hit its global cache and
        // resolve as hardlinks.
        string normalisedManifest = NormaliseRequirements(requirements);
        if (File.Exists(venvPython)
            && File.Exists(requirementsManifestPath)
            && string.Equals(
                await File.ReadAllTextAsync(requirementsManifestPath, cancellationToken).ConfigureAwait(false),
                normalisedManifest,
                StringComparison.Ordinal)
            && await VerifyUvAsync(venvPython, cancellationToken).ConfigureAwait(false))
        {
            return venvPython;
        }

        await _reporter.OnVenvInstallStartedAsync(
            new VenvInstallStarted(venvName, requirements), cancellationToken).ConfigureAwait(false);

        try
        {
            Directory.CreateDirectory(VenvsDirectory);

            // 1) Create (or recreate) the venv. `uv venv --python <path>
            //    <target>` is idempotent in the "venv with same python
            //    already exists" case but rebuilds when it doesn't —
            //    safer than a custom "exists?" check that might miss
            //    half-built venvs from a crashed earlier run.
            int venvExit = await RunUvAsync(
                uv,
                arguments: ["venv", "--python", pythonPath, venvDir],
                onStderrLine: line => OnVenvProgressAsync(venvName, "creating venv", line, cancellationToken),
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (venvExit != 0)
            {
                throw new InvalidOperationException(
                    $"`uv venv` exited with code {venvExit} when creating '{venvName}'.");
            }

            // 2) Install requirements via the venv-scoped python. uv
            //    pip install reads --python and resolves into that
            //    venv's site-packages; hardlinks from the global cache
            //    keep disk cost manageable across venvs.
            if (requirements.Count > 0)
            {
                List<string> pipArgs = new() { "pip", "install", "--python", venvPython };
                foreach (string r in requirements) pipArgs.Add(r);
                int pipExit = await RunUvAsync(
                    uv,
                    arguments: pipArgs,
                    onStderrLine: line => OnVenvProgressAsync(venvName, "installing deps", line, cancellationToken),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                if (pipExit != 0)
                {
                    throw new InvalidOperationException(
                        $"`uv pip install` exited with code {pipExit} when installing requirements for '{venvName}'.");
                }
            }

            // 3) Stash the normalised requirements alongside the venv
            //    so future EnsureVenvAsync calls can fast-path, and
            //    so `system.python_environments` (PR 5) can show what
            //    deps each venv was built with. Persisted as plain
            //    text — same format `uv pip freeze` emits — so
            //    operators can read it directly.
            await File.WriteAllTextAsync(requirementsManifestPath, normalisedManifest, cancellationToken)
                .ConfigureAwait(false);

            await _reporter.OnVenvInstallCompleteAsync(
                new VenvInstallComplete(venvName), cancellationToken).ConfigureAwait(false);

            return venvPython;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _reporter.OnFailedAsync(
                new PythonEnvironmentFailed(
                    Stage: "venv-install",
                    VenvNameOrEmpty: venvName,
                    Error: $"{ex.GetType().Name}: {ex.Message}"),
                cancellationToken).ConfigureAwait(false);
            throw;
        }
    }

    /// <inheritdoc/>
    public Task<bool> RemoveVenvAsync(string venvName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(venvName);
        string venvDir = GetVenvDirectory(venvName);
        if (!Directory.Exists(venvDir)) return Task.FromResult(false);
        // Directory.Delete is synchronous; wrapping it in
        // Task.FromResult avoids spinning up a thread for what's
        // typically a few-MB delete (the bulk of the venv's contents
        // are hardlinks to the shared cache — deleting the link
        // doesn't touch the cached wheel).
        Directory.Delete(venvDir, recursive: true);
        return Task.FromResult(true);
    }

    /// <summary>
    /// Absolute path of the directory holding the named venv.
    /// Public so the <c>system.python_environments</c> provider
    /// can resolve paths by name.
    /// </summary>
    public string GetVenvDirectory(string venvName)
        => Path.Combine(VenvsDirectory, venvName);

    /// <summary>
    /// One row's worth of information about a venv on disk, as
    /// surfaced by <c>system.python_environments</c>. Read by walking
    /// the venv directory; no engine state required.
    /// </summary>
    /// <param name="Name">Venv identifier (directory name).</param>
    /// <param name="PythonVersion">Version string from <c>pyvenv.cfg</c>'s <c>version</c> / <c>version_info</c> line, or "unknown" if the cfg is unreadable.</param>
    /// <param name="Path">Absolute path of the venv directory.</param>
    /// <param name="SizeBytes">Sum of file sizes inside the venv directory. Logical size — overstates actual disk because hardlinks to uv's wheel cache count multiple times.</param>
    /// <param name="Requirements">Newline-joined requirement strings from the <c>datum-requirements.txt</c> sidecar; empty when absent.</param>
    /// <param name="CreatedAt">UTC creation time of the venv directory.</param>
    public sealed record VenvInfo(
        string Name,
        string PythonVersion,
        string Path,
        long SizeBytes,
        string Requirements,
        DateTimeOffset CreatedAt);

    /// <summary>
    /// Aggregate paths + disk usage summary for the
    /// <c>system.python_paths</c> provider.
    /// </summary>
    /// <param name="DataRoot">Engine data root (parent of uv / python / venvs subtrees).</param>
    /// <param name="UvBinaryPath">Cached uv executable path.</param>
    /// <param name="UvInstalled">True when the uv binary exists on disk; false when the engine hasn't downloaded it yet.</param>
    /// <param name="PythonInstallDir">Directory holding managed Python interpreters.</param>
    /// <param name="VenvsDir">Directory holding per-model venvs.</param>
    /// <param name="TotalBytes">Sum of file sizes across uv + python + venvs subtrees. Logical size.</param>
    public sealed record PythonPathsSummary(
        string DataRoot,
        string UvBinaryPath,
        bool UvInstalled,
        string PythonInstallDir,
        string VenvsDir,
        long TotalBytes);

    /// <summary>
    /// Enumerates every venv currently on disk under
    /// <see cref="VenvsDirectory"/>. Returns an empty list when the
    /// directory doesn't exist yet (first-run case). Disk walks are
    /// synchronous and may be slow on large venvs — providers cache
    /// per-scan rather than per-row.
    /// </summary>
    public IReadOnlyList<VenvInfo> EnumerateVenvs()
    {
        if (!Directory.Exists(VenvsDirectory)) return [];

        List<VenvInfo> result = [];
        foreach (string dir in Directory.EnumerateDirectories(VenvsDirectory))
        {
            string name = Path.GetFileName(dir);
            string pythonVersion = TryReadVenvPythonVersion(dir) ?? "unknown";
            string requirementsPath = Path.Combine(dir, "datum-requirements.txt");
            string requirements = File.Exists(requirementsPath)
                ? File.ReadAllText(requirementsPath)
                : "";
            long sizeBytes = ComputeDirectorySize(dir);
            DateTimeOffset createdAt = new(Directory.GetCreationTimeUtc(dir), TimeSpan.Zero);
            result.Add(new VenvInfo(name, pythonVersion, dir, sizeBytes, requirements, createdAt));
        }
        return result;
    }

    /// <summary>
    /// Reads the path + disk-usage summary for the
    /// <c>system.python_paths</c> provider. Cheap — no per-venv walk
    /// here; just an aggregate scan of the three managed subtrees.
    /// </summary>
    public PythonPathsSummary GetPathsSummary()
    {
        long uvSize = File.Exists(UvBinaryPath) ? new FileInfo(UvBinaryPath).Length : 0;
        long pythonSize = Directory.Exists(PythonInstallDirectory)
            ? ComputeDirectorySize(PythonInstallDirectory) : 0;
        long venvsSize = Directory.Exists(VenvsDirectory)
            ? ComputeDirectorySize(VenvsDirectory) : 0;

        return new PythonPathsSummary(
            DataRoot: _rootDirectory,
            UvBinaryPath: UvBinaryPath,
            UvInstalled: File.Exists(UvBinaryPath),
            PythonInstallDir: PythonInstallDirectory,
            VenvsDir: VenvsDirectory,
            TotalBytes: uvSize + pythonSize + venvsSize);
    }

    /// <summary>
    /// Parses the venv's <c>pyvenv.cfg</c> for the Python version.
    /// The file is a flat <c>key = value</c> format; we accept either
    /// <c>version</c> or <c>version_info</c> as the source (both exist
    /// in practice across CPython releases).
    /// </summary>
    private static string? TryReadVenvPythonVersion(string venvDir)
    {
        string cfgPath = Path.Combine(venvDir, "pyvenv.cfg");
        if (!File.Exists(cfgPath)) return null;
        try
        {
            foreach (string line in File.ReadAllLines(cfgPath))
            {
                int idx = line.IndexOf('=');
                if (idx < 0) continue;
                string key = line[..idx].Trim();
                string value = line[(idx + 1)..].Trim();
                if (string.Equals(key, "version", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(key, "version_info", StringComparison.OrdinalIgnoreCase))
                {
                    return value;
                }
            }
        }
        catch
        {
            // Unreadable cfg — fall through to null. The provider
            // surfaces "unknown" rather than dropping the row.
        }
        return null;
    }

    /// <summary>
    /// Recursive directory size walk. Returns the sum of file lengths;
    /// silently skips unreadable entries (permission denied, broken
    /// symlinks, files deleted during the walk). On venvs backed by
    /// uv's hardlink cache, this OVERSTATES actual disk usage —
    /// hardlink targets count once per link, not once total. Honest
    /// dedupe would need inode tracking which doesn't translate to
    /// Windows; not worth the complexity for a status surface.
    /// </summary>
    private static long ComputeDirectorySize(string dir)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; }
                catch { /* file vanished or unreadable — skip */ }
            }
        }
        catch
        {
            // Top-level enumeration failed (perm denied?) — return
            // whatever partial we accumulated.
        }
        return total;
    }

    /// <summary>
    /// Absolute path of the python interpreter inside the named
    /// venv. Different layout per OS — Windows uses
    /// <c>Scripts/python.exe</c>, Unix uses <c>bin/python</c>.
    /// </summary>
    public string GetVenvPythonPath(string venvName)
        => OperatingSystem.IsWindows()
            ? Path.Combine(GetVenvDirectory(venvName), "Scripts", "python.exe")
            : Path.Combine(GetVenvDirectory(venvName), "bin", "python");

    private async ValueTask OnVenvProgressAsync(string venvName, string stage, string line, CancellationToken ct)
    {
        await _reporter.OnVenvInstallProgressAsync(
            new VenvInstallProgress(venvName, stage, line), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sorted, trimmed, newline-joined representation of a
    /// requirement list. Used as the manifest sidecar and as the
    /// fast-path equality key. Two callers passing the same set of
    /// requirements in different orders or with stray whitespace
    /// produce the same manifest and skip the re-install.
    /// </summary>
    private static string NormaliseRequirements(IReadOnlyList<string> requirements)
    {
        string[] sorted = requirements
            .Select(r => r.Trim())
            .Where(r => r.Length > 0)
            .OrderBy(r => r, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join('\n', sorted) + (sorted.Length > 0 ? "\n" : "");
    }

    /// <summary>
    /// Platform-specific binary name for uv. The release archives use
    /// <c>uv.exe</c> on Windows and <c>uv</c> elsewhere. Computed once
    /// rather than at every property access since it doesn't change
    /// over a process's lifetime.
    /// </summary>
    private static readonly string UvBinaryName =
        OperatingSystem.IsWindows() ? "uv.exe" : "uv";
}
