using DatumIngest.Catalog;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// Exercises <c>inference.devices()</c>: walks
/// <see cref="IInferenceDispatcher.Backends"/> and emits one row per
/// <c>(backend, device)</c> the backend recognises (including ones it
/// cannot reach on this machine, with a <c>reason</c>).
/// </summary>
public sealed class DevicesFunctionTests : ServiceTestBase
{
    private TableCatalog CreateCatalogWithDispatcher()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        return catalog;
    }

    private record DeviceRow(string Backend, string Device, bool Available, string Reason, int EstimatedVramMb);

    private static async Task<List<DeviceRow>> CollectRowsAsync(IQueryPlan plan)
    {
        List<DeviceRow> rows = new();
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add(new DeviceRow(
                    row[0].AsString(batch.Arena),
                    row[1].AsString(batch.Arena),
                    row[2].AsBoolean(),
                    row[3].AsString(batch.Arena),
                    row[4].AsInt32()));
            }
        }
        return rows;
    }

    [Fact]
    public async Task Devices_OnnxRuntime_AlwaysIncludesCpuAvailable()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher();
        IQueryPlan plan = catalog.Plan(
            "SELECT backend, device, available, reason, estimated_vram_mb FROM inference.devices()");

        List<DeviceRow> rows = await CollectRowsAsync(plan);

        DeviceRow cpu = Assert.Single(rows,
            r => r.Backend == "OnnxRuntime" && r.Device == "Cpu");
        Assert.True(cpu.Available);
        Assert.Equal(string.Empty, cpu.Reason);
    }

    [Fact]
    public async Task Devices_EmitsRowsForEveryRecognisedDevice()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher();
        IQueryPlan plan = catalog.Plan(
            "SELECT backend, device, available, reason, estimated_vram_mb FROM inference.devices()");

        List<DeviceRow> rows = await CollectRowsAsync(plan);

        // OnnxRuntime knows 4 candidate devices regardless of host platform.
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, r => r.Backend == "OnnxRuntime" && r.Device == "Cpu");
        Assert.Contains(rows, r => r.Backend == "OnnxRuntime" && r.Device == "Cuda");
        Assert.Contains(rows, r => r.Backend == "OnnxRuntime" && r.Device == "DirectMl");
        Assert.Contains(rows, r => r.Backend == "OnnxRuntime" && r.Device == "CoreMl");
    }

    [Fact]
    public async Task Devices_UnreachableDevices_CarryReason()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher();
        IQueryPlan plan = catalog.Plan(
            "SELECT backend, device, available, reason, estimated_vram_mb FROM inference.devices() WHERE available = false");

        List<DeviceRow> rows = await CollectRowsAsync(plan);

        // On every supported host platform at least one device is unreachable
        // (CoreML on non-Mac, DirectML on non-Windows, CUDA on machines
        // without an NVIDIA stack). Every such row must carry a non-empty
        // reason so the user can tell why they can't target it.
        foreach (DeviceRow r in rows)
        {
            Assert.False(r.Available);
            Assert.False(string.IsNullOrEmpty(r.Reason),
                $"Backend={r.Backend} Device={r.Device} reported unavailable with no reason.");
        }

        // Platform-specific spot checks.
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(rows, r =>
                r.Device == "CoreMl" && r.Reason == "CoreML is macOS-only.");
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.Contains(rows, r =>
                r.Device == "DirectMl" && r.Reason == "DirectML is Windows-only.");
        }
    }
}
