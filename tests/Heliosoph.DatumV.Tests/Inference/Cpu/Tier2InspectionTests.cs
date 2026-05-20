using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// Tier 2 of the inference toolkit: <c>onnx_inspect_meta</c>,
/// <c>infer_compatibility</c>, and <c>model_skeleton</c>. All three exercise
/// the new ONNX protobuf header reader against the existing softmax.onnx
/// fixture.
/// </summary>
public sealed class Tier2InspectionTests : ServiceTestBase
{
    private static string FixturePath() => Path.Combine(
        AppContext.BaseDirectory, "Fixtures", "softmax.onnx");

    private TableCatalog CreateCatalogWithDispatcher()
    {
        TableCatalog catalog = CreateCatalog();
        catalog.Models = new ModelCatalog(modelDirectory: Path.GetTempPath());
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);
        return catalog;
    }

    // ——— inference.onnx_inspect_meta ——————————————————————————————————————————

    [Fact]
    public async Task OnnxInspectMeta_FixtureSoftmax_ReturnsSingleRowWithExpectedShape()
    {
        string fixturePath = FixturePath();
        Assert.True(File.Exists(fixturePath));

        TableCatalog catalog = CreateCatalogWithDispatcher();
        StatementPlan plan = catalog.Plan(
            $"SELECT producer_name, producer_version, opset, ir_version, required_ops, file_size_bytes " +
            $"FROM inference.onnx_inspect_meta('file://{fixturePath}')");

        int rowCount = 0;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                rowCount++;
                Row row = batch[i];
                // producer_name / producer_version: tolerant — the fixture might
                // be re-exported and these are non-empty for normal HF exports
                // but we don't pin a specific value.
                string producerName = row[0].AsString(batch.Arena);
                Assert.NotNull(producerName);

                int opset = row[2].AsInt32();
                Assert.True(opset > 0, $"Expected positive opset, got {opset}.");

                long irVersion = row[3].AsInt64();
                Assert.True(irVersion > 0, $"Expected positive ir_version, got {irVersion}.");

                // required_ops must contain at least Softmax (the fixture's
                // only operation).
                string[] ops = row[4].AsStringArray(batch.Arena);
                Assert.Contains("Softmax", ops);

                long fileSize = row[5].AsInt64();
                Assert.Equal(new FileInfo(fixturePath).Length, fileSize);
            }
        }

        Assert.Equal(1, rowCount);
    }

    [Fact]
    public async Task OnnxInspectMeta_MissingFile_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher();
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.onnx");
        StatementPlan plan = catalog.Plan(
            $"SELECT producer_name FROM inference.onnx_inspect_meta('file://{missing}')");

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
        {
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });
    }

    // ——— inference.infer_compatibility ———————————————————————————————————————

    [Fact]
    public async Task InferCompatibility_FixtureSoftmax_OrtBackendSupports()
    {
        string fixturePath = FixturePath();
        TableCatalog catalog = CreateCatalogWithDispatcher();
        StatementPlan plan = catalog.Plan(
            $"SELECT backend, supported, opset_required, opset_supported, notes " +
            $"FROM inference.infer_compatibility('file://{fixturePath}')");

        List<(string Backend, bool Supported, int Required, int Supported2, string Notes)> rows = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row r = batch[i];
                rows.Add((
                    r[0].AsString(batch.Arena),
                    r[1].AsBoolean(),
                    r[2].AsInt32(),
                    r[3].AsInt32(),
                    r[4].AsString(batch.Arena)));
            }
        }

        var ort = Assert.Single(rows, r => r.Backend == "OnnxRuntime");
        Assert.True(ort.Supported,
            $"ORT should support softmax.onnx, got supported=false. opset required={ort.Required}, opset supported={ort.Supported2}, notes='{ort.Notes}'.");
        Assert.True(ort.Required > 0);
        Assert.Equal(22, ort.Supported2);
        Assert.Equal("", ort.Notes);
    }

    // ——— inference.model_skeleton ————————————————————————————————————————————

    [Fact]
    public async Task ModelSkeleton_FixtureSoftmax_GeneratesPlausibleTemplate()
    {
        string fixturePath = FixturePath();
        TableCatalog catalog = CreateCatalogWithDispatcher();
        StatementPlan plan = catalog.Plan(
            $"SELECT inference.model_skeleton('file://{fixturePath}')");

        string? skeleton = null;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                skeleton = batch[i][0].AsString(batch.Arena);
            }
        }

        Assert.NotNull(skeleton);
        // Must contain the structural pieces a user would expect.
        Assert.Contains("CREATE MODEL your_model_name(", skeleton);
        Assert.Contains($"USING 'file://{fixturePath}'", skeleton);
        Assert.Contains("RETURNS", skeleton);
        Assert.Contains("AS BEGIN", skeleton);
        Assert.Contains("END", skeleton);
        // Single-IO fixture → body should call infer() on the input parameter.
        Assert.Contains("RETURN infer(@", skeleton);
        // The softmax fixture's input is 'x' and output is 'y' — both Float32[].
        Assert.Contains("Float32[]", skeleton);
    }

    [Fact]
    public async Task ModelSkeleton_RelativePath_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalogWithDispatcher();
        StatementPlan plan = catalog.Plan(
            "SELECT inference.model_skeleton('model.onnx')");

        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });
        Exception root = thrown;
        while (root.InnerException is not null) root = root.InnerException;
        Assert.Contains("relative path", root.Message, StringComparison.OrdinalIgnoreCase);
    }
}
