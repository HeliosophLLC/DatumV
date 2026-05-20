using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// Exercises the <c>inference.onnx_inspect(path)</c> TVF end-to-end:
/// resolves the path through the same convention as <c>CREATE MODEL USING</c>,
/// loads the model via <see cref="OnnxRuntimeBackend"/> on CPU, and emits one
/// row per input + output tensor.
/// </summary>
/// <remarks>
/// Uses the existing <c>Fixtures/softmax.onnx</c> fixture (single Float32 input
/// <c>x</c>, single Float32 output <c>y</c>, both 1-d with one dynamic dim) so
/// the assertions can pin the exact (kind, name, dtype, shape, is_dynamic)
/// tuples the user is supposed to see.
/// </remarks>
public sealed class OnnxInspectFunctionTests : ServiceTestBase
{
    private static string FixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "softmax.onnx");

    private TableCatalog CreateCatalogWithRealDispatcher(string? modelDirectory = null)
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: modelDirectory ?? Path.GetTempPath());
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        return catalog;
    }

    [Fact]
    public async Task OnnxInspect_AbsolutePath_ReturnsInputAndOutputRows()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath),
            $"Fixture missing at {fixturePath}. Check tests/Heliosoph.DatumV.Tests/Fixtures/softmax.onnx is present and copied to bin output.");

        TableCatalog catalog = CreateCatalogWithRealDispatcher();

        StatementPlan plan = catalog.Plan(
            $"SELECT kind, name, dtype, shape, is_dynamic FROM inference.onnx_inspect('file://{fixturePath}')");

        List<(string Kind, string Name, string Dtype, int[] Shape, bool IsDynamic)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                rows.Add((
                    row[0].AsString(batch.Arena),
                    row[1].AsString(batch.Arena),
                    row[2].AsString(batch.Arena),
                    row[3].AsArraySpan<int>(batch.Arena).ToArray(),
                    row[4].AsBoolean()));
            }
        }

        Assert.Equal(2, rows.Count);

        var input = Assert.Single(rows, r => r.Kind == "input");
        Assert.Equal("x", input.Name);
        Assert.Equal("Float32", input.Dtype);
        Assert.Equal([-1], input.Shape);
        Assert.True(input.IsDynamic);

        var output = Assert.Single(rows, r => r.Kind == "output");
        Assert.Equal("y", output.Name);
        Assert.Equal("Float32", output.Dtype);
        Assert.Equal([-1], output.Shape);
        Assert.True(output.IsDynamic);
    }

    [Fact]
    public async Task OnnxInspect_RelativePath_ResolvesAgainstModelDirectory()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath));

        string tempDir = Path.Combine(Path.GetTempPath(), $"onnx-inspect-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string targetPath = Path.Combine(tempDir, "softmax.onnx");
            File.Copy(fixturePath, targetPath);

            TableCatalog catalog = CreateCatalogWithRealDispatcher(modelDirectory: tempDir);

            StatementPlan plan = catalog.Plan(
                "SELECT kind FROM inference.onnx_inspect('softmax.onnx')");

            int rowCount = 0;
            await foreach (RowBatch batch in ExecutePlanAsync(plan))
            {
                rowCount += batch.Count;
            }

            Assert.Equal(2, rowCount);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public async Task OnnxInspect_NonexistentFile_ThrowsFileNotFound()
    {
        TableCatalog catalog = CreateCatalogWithRealDispatcher();

        string missing = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.onnx");
        StatementPlan plan = catalog.Plan(
            $"SELECT kind FROM inference.onnx_inspect('file://{missing}')");

        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });

        Assert.Contains(missing, ex.Message);
    }
}
