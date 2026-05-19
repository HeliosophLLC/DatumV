namespace Heliosoph.DatumV.Tests.Models.Python;

using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models.Python;

/// <summary>
/// Round-trip integration test for the Python-backed model bridge. Runs
/// the bundled echo worker through a real Python subprocess and verifies
/// inputs flow over and back unchanged. Self-skips when Python isn't on
/// PATH so CI without Python doesn't fail the suite — the framework is
/// experimentation infrastructure, not a load-bearing engine feature.
/// </summary>
public sealed class PythonBackedModelTests : ServiceTestBase
{
    private static readonly string ScriptsDirectory =
        Path.Combine(AppContext.BaseDirectory, "python");

    /// <summary>
    /// Resolves the path to <c>echo_worker.py</c> in the test output's
    /// python/ folder. Returns null if the scripts haven't been copied
    /// (e.g. running from a stale build) so individual tests can self-skip.
    /// </summary>
    private static string? FindEchoWorker()
    {
        string path = Path.Combine(ScriptsDirectory, "echo_worker.py");
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// True when "python" is invokable on this machine. Used by every test
    /// here as a guard — without Python, none of these tests can run, and
    /// failing them on a CPU-only / no-python CI box would be noise.
    /// </summary>
    private static bool PythonAvailable()
    {
        try
        {
            using System.Diagnostics.Process? p = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("DATUM_PYTHON") ?? "python",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends three string rows through the echo worker and asserts the
    /// outputs match. Exercises: process spawn, ready handshake, request/
    /// response round-trip, output decoding, and clean shutdown via
    /// Dispose.
    /// </summary>
    [Fact]
    public async Task EchoWorker_RoundTripsStrings()
    {
        if (!PythonAvailable()) return;
        string? scriptPath = FindEchoWorker();
        if (scriptPath is null) return;

        using PythonBackedModel model = new(
            name: "echo_test",
            inputKinds: [DataKind.String],
            outputKind: DataKind.String,
            isDeterministic: true,
            environments: new SystemPythonEnvironmentManager(),
            venvName: "echo_test",
            pythonVersion: "3.11",
            requirements: [],
            scriptPath: scriptPath);

        IReadOnlyList<IReadOnlyList<ValueRef>> inputs =
        [
            [ValueRef.FromString("hello")],
            [ValueRef.FromString("world")],
            [ValueRef.FromString("from python")],
        ];
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides = [[], [], []];

        IReadOnlyList<ValueRef> outputs = await model
            .InferBatchAsync(inputs, overrides, CancellationToken.None);

        Assert.Equal(3, outputs.Count);
        Assert.Equal("hello", outputs[0].AsString());
        Assert.Equal("world", outputs[1].AsString());
        Assert.Equal("from python", outputs[2].AsString());
    }

    /// <summary>
    /// A second batch reuses the already-running subprocess (the host
    /// is started lazily on the first call). Without process reuse, every
    /// inference would pay the model-load cost — for a real model that's
    /// 5-30 seconds. This test pins the laziness invariant.
    /// </summary>
    [Fact]
    public async Task EchoWorker_ReusesProcessAcrossBatches()
    {
        if (!PythonAvailable()) return;
        string? scriptPath = FindEchoWorker();
        if (scriptPath is null) return;

        using PythonBackedModel model = new(
            name: "echo_test",
            inputKinds: [DataKind.String],
            outputKind: DataKind.String,
            isDeterministic: true,
            environments: new SystemPythonEnvironmentManager(),
            venvName: "echo_test",
            pythonVersion: "3.11",
            requirements: [],
            scriptPath: scriptPath);

        for (int i = 0; i < 3; i++)
        {
            IReadOnlyList<IReadOnlyList<ValueRef>> inputs =
                [[ValueRef.FromString($"batch-{i}")]];
            IReadOnlyList<IReadOnlyList<ValueRef>> overrides = [[]];

            IReadOnlyList<ValueRef> outputs = await model
                .InferBatchAsync(inputs, overrides, CancellationToken.None);

            Assert.Single(outputs);
            Assert.Equal($"batch-{i}", outputs[0].AsString());
        }
    }

    /// <summary>
    /// Bytes round-trip via base64-in-JSON. Important for the framework's
    /// audio/image use cases — Bark output, MusicGen output, image inputs
    /// to BLIP-2 / XTTS-v2 reference-clip cloning, etc.
    /// </summary>
    [Fact]
    public async Task EchoWorker_RoundTripsBytes()
    {
        if (!PythonAvailable()) return;
        string? scriptPath = FindEchoWorker();
        if (scriptPath is null) return;

        // 256 distinct byte values — catches any 8-bit-clean encoding bugs.
        byte[] payload = new byte[256];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)i;

        using PythonBackedModel model = new(
            name: "echo_test",
            inputKinds: [DataKind.Image],
            outputKind: DataKind.Image,
            isDeterministic: true,
            environments: new SystemPythonEnvironmentManager(),
            venvName: "echo_test",
            pythonVersion: "3.11",
            requirements: [],
            scriptPath: scriptPath);

        IReadOnlyList<IReadOnlyList<ValueRef>> inputs =
            [[ValueRef.FromBytes(DataKind.Image, payload)]];
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides = [[]];

        IReadOnlyList<ValueRef> outputs = await model
            .InferBatchAsync(inputs, overrides, CancellationToken.None);

        Assert.Single(outputs);
        Assert.Equal(payload, outputs[0].AsBytes());
    }

    /// <summary>
    /// A worker script that doesn't exist surfaces as a clear
    /// PythonProcessException, not an obscure spawn error.
    /// </summary>
    [Fact]
    public async Task NonexistentScript_ThrowsPythonProcessException()
    {
        if (!PythonAvailable()) return;

        using PythonBackedModel model = new(
            name: "missing_test",
            inputKinds: [DataKind.String],
            outputKind: DataKind.String,
            isDeterministic: true,
            environments: new SystemPythonEnvironmentManager(),
            venvName: "missing_test",
            pythonVersion: "3.11",
            requirements: [],
            scriptPath: Path.Combine(ScriptsDirectory, "this_script_does_not_exist.py"),
            readyTimeout: TimeSpan.FromSeconds(5));

        IReadOnlyList<IReadOnlyList<ValueRef>> inputs = [[ValueRef.FromString("x")]];
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides = [[]];

        await Assert.ThrowsAsync<PythonProcessException>(() =>
            model.InferBatchAsync(inputs, overrides, CancellationToken.None));
    }
}
