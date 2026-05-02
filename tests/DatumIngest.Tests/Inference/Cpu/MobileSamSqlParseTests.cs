using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

namespace DatumIngest.Tests.Inference.Cpu;

/// <summary>
/// Parse + body-shape smoke for the SQL-defined MobileSAM everything-mode
/// body. Locks in the parser-side surface (nested WHILE, IF/BEGIN/END inside
/// WHILE, struct-shape decoder dispatch, Array&lt;Image&gt; return) without
/// requiring the 60 MB bundle. The real e2e tests against an actual image
/// + decoder run gated by bundle presence.
/// </summary>
public sealed class MobileSamSqlParseTests : ServiceTestBase
{
    private string LoadCanonicalSql()
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogModel model = store.Manifest.Models.First(m => m.Id == "mobile-sam");
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException("Catalog entry 'mobile-sam' has no installSql.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    private static bool BundlePresent()
    {
        string root = Environment.GetEnvironmentVariable("DATUM_MODELS_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DatumIngest", "models");
        return File.Exists(Path.Combine(root, "mobile-sam", "mobile_sam_image_encoder.onnx"));
    }

    [Fact]
    public void MobileSamPoint_CreateModelStatement_ParsesAndRegisters()
    {
        // temp force-validate

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "mobilesam_point"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("Image", descriptor!.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        Assert.Equal(2, descriptor.UsingFiles!.Count);
        // (img Image, x Float64, y Float64).
        Assert.Equal(3, descriptor.Parameters.Count);
    }

    [Fact]
    public void MobileSam_CreateModelStatement_ParsesAndRegisters()
    {
        // temp force-validate

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new DatumIngest.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql());

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "mobilesam"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("Array<Image>", descriptor!.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        Assert.Equal(2, descriptor.UsingFiles!.Count);
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "encoder");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "decoder");
    }
}
