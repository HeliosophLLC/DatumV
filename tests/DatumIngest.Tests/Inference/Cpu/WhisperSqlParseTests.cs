using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Inference.OnnxRuntime;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// Parse + body-shape smoke for the SQL-defined Whisper bodies. Each
/// catalog entry installs a single CREATE MODEL statement; this catches
/// regressions in the audio_to_log_mel + decode_seq2seq + tokenizer
/// pipeline without needing the multi-hundred-MB bundles. Real e2e
/// transcription runs gated by bundle presence.
/// </summary>
public sealed class WhisperSqlParseTests : ServiceTestBase
{
    private string LoadCanonicalSql(string modelId)
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogModel model = store.Manifest.Models.First(m => m.Id == modelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException($"Catalog entry '{modelId}' has no installSql.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    private static bool BundlePresent(string folder)
    {
        string root = Environment.GetEnvironmentVariable("DATUM_MODELS_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heliosoph.DatumV", "models");
        return File.Exists(Path.Combine(root, folder, "onnx", "encoder_model.onnx"));
    }

    [Theory]
    [InlineData("whisper-tiny",            "whisper_tiny")]
    [InlineData("whisper-base",            "whisper_base")]
    [InlineData("whisper-small",           "whisper_small")]
    [InlineData("whisper-large-v3-turbo",  "whisper_large_v3_turbo")]
    public void Whisper_InstallSql_ParsesToOneCreateModelStatement(string catalogId, string modelName)
    {
        // Body-shape regression: parse standalone without the bundle.
        // ParseBatchWithText is the same path the catalog installer uses
        // at engine startup; catches parser regressions early.
        string sql = LoadCanonicalSql(catalogId);
        IReadOnlyList<(Statement Statement, string SourceText)> statements =
            SqlParser.ParseBatchWithText(sql);
        Assert.Single(statements);
        CreateModelStatement create = Assert.IsType<CreateModelStatement>(statements[0].Statement);
        Assert.Equal(modelName, create.Name);
    }

    [Fact]
    public void WhisperBase_InstallSql_RegistersModel()
    {
        if (!BundlePresent("whisper-base")) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        catalog.Plan(LoadCanonicalSql("whisper-base"));

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "whisper_base"),
                out ModelDescriptor? descriptor));
        Assert.NotNull(descriptor);
        Assert.Equal("AudioToText", descriptor!.ImplementsTaskName);
        Assert.Equal("String", descriptor.ReturnTypeName);
        Assert.NotNull(descriptor.UsingFiles);
        Assert.Equal(2, descriptor.UsingFiles!.Count);
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "encoder");
        Assert.Contains(descriptor.UsingFiles, f => f.Alias == "decoder");
    }
}
