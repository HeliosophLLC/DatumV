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
/// Parse + body-shape smoke for the SQL-defined MobileSAM bodies. The
/// install SQL contains two CREATE MODEL statements (everything-mode +
/// prompted-point); both must parse and register. The actual e2e against
/// real images runs gated by bundle presence.
/// </summary>
public sealed class MobileSamSqlParseTests : ServiceTestBase
{
    private string LoadCanonicalSql()
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogVariant model = store.Manifest.Entries.SelectMany(e => e.Variants).First(v => v.Id == "mobile-sam");
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException("Catalog entry 'mobile-sam' has no installSql.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    private static bool BundlePresent()
    {
        string root = Environment.GetEnvironmentVariable("DATUMV_MODELS_DIRECTORY")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Heliosoph.DatumV", "models");
        return File.Exists(Path.Combine(root, "mobile-sam", "mobile_sam_image_encoder.onnx"));
    }

    /// <summary>
    /// Plans every CREATE MODEL statement in the install SQL via the same
    /// batch-aware parser the catalog installer uses. Self-skips when the
    /// bundle is missing so CI without downloads stays green.
    /// </summary>
    private async Task RunInstallAsync(TableCatalog catalog)
    {
        string sql = LoadCanonicalSql();
        IReadOnlyList<(Statement Statement, string SourceText)> statements =
            SqlParser.ParseBatchWithText(sql);
        Assert.Equal(2, statements.Count);
        foreach ((Statement statement, string sourceText) in statements)
        {
            StatementPlan plan = await catalog.PlanAsync(statement, sourceText);
            await catalog.ExecuteAsync(plan).DrainAsync();
        }
    }

    [Fact]
    public void MobileSam_InstallSql_ParsesToTwoCreateModelStatements()
    {
        // Body-shape regression: both models must parse standalone even
        // without the bundle. The catalog installer's batch path
        // (ParseBatchWithText) is what runs at engine startup; mirroring it
        // here catches parser-level regressions (reserved-word DECLARE
        // names, mismatched BEGIN/END, etc.) without needing the 60MB
        // bundle.
        string sql = LoadCanonicalSql();
        IReadOnlyList<(Statement Statement, string SourceText)> statements =
            SqlParser.ParseBatchWithText(sql);
        Assert.Equal(2, statements.Count);
        Assert.All(statements, s => Assert.IsType<CreateModelStatement>(s.Statement));
        CreateModelStatement everything = (CreateModelStatement)statements[0].Statement;
        CreateModelStatement prompted   = (CreateModelStatement)statements[1].Statement;
        Assert.Equal("mobilesam",       everything.Name);
        Assert.Equal("mobilesam_point", prompted.Name);
    }

    [Fact]
    public async Task MobileSam_InstallSql_RegistersBothModels()
    {
        if (!BundlePresent()) return;

        TableCatalog catalog = CreateCatalog();
        catalog.Models = new Heliosoph.DatumV.Models.ModelCatalog(modelDirectory: ModelsDirectory);
        catalog.InferenceDispatcher = new InferenceDispatcher(
            [new OnnxRuntimeBackend()],
            NullLogger<InferenceDispatcher>.Instance);

        await RunInstallAsync(catalog);

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "mobilesam"),
                out ModelDescriptor? everythingMode));
        Assert.NotNull(everythingMode);
        Assert.Equal("Array<Image>", everythingMode!.ReturnTypeName);

        Assert.True(
            catalog.DeclaredModels.TryGet(
                new QualifiedName("models", "mobilesam_point"),
                out ModelDescriptor? promptedMode));
        Assert.NotNull(promptedMode);
        Assert.Equal("Image", promptedMode!.ReturnTypeName);
        // (img Image, x Float64, y Float64).
        Assert.Equal(3, promptedMode.Parameters.Count);
    }
}
