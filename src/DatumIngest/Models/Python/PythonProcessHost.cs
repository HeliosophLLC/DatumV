using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DatumIngest.Models.Python;

/// <summary>
/// Spawns and talks to a Python subprocess over newline-delimited JSON
/// (NDJSON) on stdin/stdout. The Python side is expected to print
/// <c>{"ready": true}</c> once initialisation finishes, then loop reading
/// one request line and writing one response line per iteration.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Channel discipline.</strong> stdout carries protocol messages
/// only — every line must be a single complete JSON object. stderr is the
/// free-form channel for Python's own progress/warnings (model loading
/// chatter, deprecation notices). The host drains stderr asynchronously
/// into a buffered log so a chatty Python process can't block on a full
/// pipe, but never tries to parse it.
/// </para>
/// <para>
/// <strong>Concurrency.</strong> A single Python process serialises calls
/// through one <see cref="SemaphoreSlim"/>. Most ML libraries hold the GIL
/// or are otherwise not concurrent-safe, and per-model latency is large
/// enough (seconds) that one-call-at-a-time per process is fine. If a
/// model needs throughput, run multiple <see cref="PythonProcessHost"/>
/// instances over the same script.
/// </para>
/// <para>
/// <strong>Lifecycle.</strong> Construct via <see cref="StartAsync"/>,
/// which blocks until the ready handshake completes (or the timeout
/// elapses). <see cref="Dispose"/> attempts a graceful shutdown by
/// closing stdin, then kills the process if it doesn't exit within
/// <see cref="ShutdownGraceMs"/>.
/// </para>
/// </remarks>
internal sealed class PythonProcessHost : IDisposable
{
    /// <summary>How long to wait for the process to exit after closing stdin before forcing a kill.</summary>
    private const int ShutdownGraceMs = 2_000;

    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly StreamReader _stdout;
    private readonly StringBuilder _stderrLog;
    private readonly Task _stderrPump;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private int _nextId;
    private bool _disposed;

    private PythonProcessHost(Process process, StringBuilder stderrLog, Task stderrPump)
    {
        _process = process;
        _stdin = process.StandardInput;
        _stdout = process.StandardOutput;
        _stderrLog = stderrLog;
        _stderrPump = stderrPump;
    }

    /// <summary>Captured stderr output from the Python process. Useful in test diagnostics or after a failure.</summary>
    public string StderrLog
    {
        get
        {
            lock (_stderrLog) return _stderrLog.ToString();
        }
    }

    /// <summary>Process ID. Non-functional outside diagnostics — included for log-correlation.</summary>
    public int ProcessId => _process.Id;

    /// <summary>
    /// Spawns <paramref name="pythonExecutable"/> with <paramref name="scriptPath"/>
    /// and any extra <paramref name="scriptArgs"/>, then waits up to
    /// <paramref name="readyTimeout"/> for the worker to print
    /// <c>{"ready": true}</c> on stdout.
    /// </summary>
    /// <exception cref="PythonProcessException">Thrown if the process fails to spawn, exits before signalling ready, or times out.</exception>
    public static async Task<PythonProcessHost> StartAsync(
        string pythonExecutable,
        string scriptPath,
        IReadOnlyList<string>? scriptArgs,
        TimeSpan readyTimeout,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? extraPythonPath = null)
    {
        ProcessStartInfo psi = new()
        {
            FileName = pythonExecutable,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            // -u disables Python's stdout/stderr buffering — without it we'd
            // wait indefinitely for the ready line because Python wouldn't
            // flush until the buffer fills.
            ArgumentList = { "-u", scriptPath },
        };
        if (scriptArgs is not null)
        {
            foreach (string arg in scriptArgs) psi.ArgumentList.Add(arg);
        }

        // Augment PYTHONPATH for the child so user-written worker scripts
        // living outside the engine's bundled python/ directory can still
        // `from python_worker_host import run`. We prepend caller-provided
        // paths (typically the engine's bundled scripts dir) to whatever
        // PYTHONPATH the parent process inherits — first match wins, so
        // engine scripts shadow any system-installed namesakes.
        if (extraPythonPath is { Count: > 0 })
        {
            string existing = Environment.GetEnvironmentVariable("PYTHONPATH") ?? string.Empty;
            List<string> parts = new(extraPythonPath.Count + 1);
            foreach (string path in extraPythonPath)
            {
                if (!string.IsNullOrEmpty(path)) parts.Add(path);
            }
            if (!string.IsNullOrEmpty(existing)) parts.Add(existing);
            psi.Environment["PYTHONPATH"] = string.Join(Path.PathSeparator, parts);
        }

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new PythonProcessException(
                    $"Process.Start returned null for '{pythonExecutable} {scriptPath}'. "
                    + "Confirm Python is installed and on PATH, or set DATUM_PYTHON to its absolute path.");
        }
        catch (Exception ex) when (ex is not PythonProcessException)
        {
            throw new PythonProcessException(
                $"Failed to spawn '{pythonExecutable} {scriptPath}': {ex.Message}. "
                + "Confirm Python is installed and on PATH, or set DATUM_PYTHON to its absolute path.",
                ex);
        }

        StringBuilder stderrLog = new();
        Task stderrPump = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
                {
                    lock (stderrLog) stderrLog.AppendLine(line);
                }
            }
            catch
            {
                // Process exit closes the pipe; that's normal during disposal.
            }
        });

        PythonProcessHost host = new(process, stderrLog, stderrPump);

        try
        {
            await host.WaitForReadyAsync(readyTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            host.Dispose();
            throw;
        }
        return host;
    }

    private async Task WaitForReadyAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        Task<string?> readTask = _stdout.ReadLineAsync();
        Task completed = await Task.WhenAny(
            readTask,
            Task.Delay(Timeout.Infinite, cts.Token)).ConfigureAwait(false);

        if (completed != readTask)
        {
            throw new PythonProcessException(
                $"Python worker did not signal ready within {timeout.TotalSeconds:F0}s. " +
                $"stderr so far:\n{StderrLog}");
        }

        string? line = await readTask.ConfigureAwait(false);
        if (line is null)
        {
            // EOF before ready — process exited.
            await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw new PythonProcessException(
                $"Python worker exited (code {_process.ExitCode}) before signalling ready. " +
                $"stderr:\n{StderrLog}");
        }

        JsonNode? handshake = JsonNode.Parse(line);
        if (handshake?["ready"]?.GetValue<bool>() != true)
        {
            throw new PythonProcessException(
                $"Expected ready handshake, got: {line}. stderr:\n{StderrLog}");
        }
    }

    /// <summary>
    /// Sends one request and awaits one response. <paramref name="requestBody"/>
    /// must be a JSON object — the host injects the <c>id</c> field and
    /// validates the response carries the matching id.
    /// </summary>
    /// <returns>The response object with the <c>id</c> field stripped.</returns>
    /// <exception cref="PythonProcessException">Thrown if the worker reports an error or the response is malformed.</exception>
    public async Task<JsonObject> CallAsync(JsonObject requestBody, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _callLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int id = Interlocked.Increment(ref _nextId);
            requestBody["id"] = id;

            string requestLine = requestBody.ToJsonString();
            await _stdin.WriteLineAsync(requestLine.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

            string? responseLine;
            try
            {
                responseLine = await _stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                throw new PythonProcessException(
                    $"Failed reading response from Python worker: {ex.Message}. stderr:\n{StderrLog}", ex);
            }

            if (responseLine is null)
            {
                await _process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                throw new PythonProcessException(
                    $"Python worker exited (code {_process.ExitCode}) without responding. stderr:\n{StderrLog}");
            }

            JsonNode? parsed;
            try
            {
                parsed = JsonNode.Parse(responseLine);
            }
            catch (JsonException ex)
            {
                throw new PythonProcessException(
                    $"Python worker response was not valid JSON: {responseLine}. stderr:\n{StderrLog}", ex);
            }

            if (parsed is not JsonObject response)
            {
                throw new PythonProcessException(
                    $"Python worker response was not a JSON object: {responseLine}");
            }

            int? responseId = response["id"]?.GetValue<int>();
            if (responseId != id)
            {
                throw new PythonProcessException(
                    $"Python worker response id mismatch: sent {id}, got {responseId?.ToString() ?? "<null>"}. " +
                    $"Body: {responseLine}");
            }

            if (response["error"] is JsonNode errorNode)
            {
                string errorMessage = errorNode.GetValue<string>();
                string? traceback = response["traceback"]?.GetValue<string>();
                // Include the worker's accumulated stderr too — diagnostic
                // prints from the worker (e.g. shape/dtype dumps before a
                // failed ONNX call) end up there and would otherwise be
                // silently dropped from the user-facing exception message.
                string stderr = StderrLog;
                throw new PythonProcessException(
                    $"Python worker raised: {errorMessage}" +
                    (traceback is null ? "" : $"\n{traceback}") +
                    (string.IsNullOrEmpty(stderr) ? "" : $"\nstderr:\n{stderr}"));
            }

            response.Remove("id");
            return response;
        }
        finally
        {
            _callLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            // Close stdin to signal "no more requests" — well-behaved workers
            // exit their read loop and shut down cleanly.
            _stdin.Close();
        }
        catch { /* may already be closed */ }

        try
        {
            if (!_process.WaitForExit(ShutdownGraceMs))
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch { /* process may already be dead */ }

        try { _stdout.Dispose(); } catch { }
        try { _process.Dispose(); } catch { }

        // Best-effort: drain stderr pump.
        try { _stderrPump.Wait(500); } catch { }

        _callLock.Dispose();
    }
}

/// <summary>
/// Thrown when a Python worker fails to start, crashes, returns malformed
/// data, or reports an error from inside its <c>infer</c> function.
/// </summary>
public sealed class PythonProcessException : Exception
{
    /// <summary>Creates an exception with the given diagnostic message.</summary>
    public PythonProcessException(string message) : base(message) { }

    /// <summary>Creates an exception wrapping an inner cause (e.g. a process spawn failure).</summary>
    public PythonProcessException(string message, Exception inner) : base(message, inner) { }
}
