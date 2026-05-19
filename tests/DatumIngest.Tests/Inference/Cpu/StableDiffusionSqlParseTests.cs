using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// Parse + IMPLEMENTS-contract smoke for the SQL-defined Stable Diffusion
/// model bodies. The bodies share their pipeline shape across catalog
/// entries — registering the canonical SD-1.5 Hyper body and the SDXL body
/// here locks the parser-side surface (CLIP framing helper, sd_turbo_schedule,
/// array math, multi-input infer with rank-0 timestep) without requiring the
/// 4-8 GB ONNX bundles. The matching <c>*E2ETests</c> tests run real
/// generation and self-skip when the bundle isn't downloaded.
/// </summary>
public sealed class StableDiffusionSqlParseTests : ServiceTestBase
{
    private string LoadCanonicalSql(string modelId)
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogModel model = store.Manifest.Models.First(m => m.Id == modelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException(
                $"Catalog entry '{modelId}' has no installSql.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    /// <summary>
    /// Self-skip when the catalog bundle isn't present. The registrar's
    /// file-existence check is the last step after parse + IMPLEMENTS
    /// contract validation + RETURN shape check, so reaching it means the
    /// body itself is structurally valid; the test still adds value in dev
    /// environments where the bundle is downloaded.
    /// </summary>
    private static bool BundlePresent(string folder, string firstFile)
    {
        string root = Environment.GetEnvironmentVariable("DATUM_MODELS_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heliosoph.DatumV", "models");
        return File.Exists(Path.Combine(root, folder, firstFile));
    }

    [Fact]
    public void RealisticVisionHyper_CreateModelStatement_ParsesAndRegisters()
    {
        if (!BundlePresent("realistic-vision-hyper-onnx", "text_encoder/model.onnx")) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql("realistic-vision-hyper"));

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "realistic_vision_hyper"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("TextToImage", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        Assert.NotEmpty(descriptor.StatementBody);
        Assert.NotNull(descriptor.UsingFiles);
        Assert.Equal(3, descriptor.UsingFiles!.Count);
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "text_encoder");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "unet");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "vae_decoder");
    }

    [Fact]
    public void SdTurbo_CreateModelStatement_ParsesAndRegisters()
    {
        if (!BundlePresent("sd-turbo-onnx", "text_encoder/model.onnx")) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql("sd-turbo"));

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "sd_turbo"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("TextToImage", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        Assert.Equal(3, descriptor.UsingFiles!.Count);
    }

    [Fact]
    public void SdxlTurbo_CreateModelStatement_ParsesAndRegisters()
    {
        if (!BundlePresent("sdxl-turbo-onnx", "text_encoder/model.onnx")) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql("sdxl-turbo"));

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "sdxl_turbo"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("TextToImage", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        // Dual text encoders + UNet + VAE decoder = 4 sessions.
        Assert.Equal(4, descriptor.UsingFiles!.Count);
    }

    [Fact]
    public void JuggernautXlLightning_CreateModelStatement_ParsesAndRegisters()
    {
        if (!BundlePresent("juggernaut-xl-lightning-onnx", "text_encoder/model.onnx")) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql("juggernaut-xl-lightning"));

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "juggernaut_xl_lightning"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("TextToImage", descriptor!.ImplementsTaskName);
        Assert.Equal("Image", descriptor.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        // Dual text encoders + UNet + VAE decoder = 4 sessions.
        Assert.Equal(4, descriptor.UsingFiles!.Count);
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "text_encoder_1");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "text_encoder_2");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "unet");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "vae_decoder");
    }
}
