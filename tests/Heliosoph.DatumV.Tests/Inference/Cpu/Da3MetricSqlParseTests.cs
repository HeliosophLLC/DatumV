using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Inference.Cpu;

/// <summary>
/// Parse + body-shape smoke for the SQL-defined DA3 Metric Large bodies.
/// Each catalog variant installs three CREATE MODEL statements
/// (visualization, _meters, _full); this catches regressions in the
/// defaulted-fov_deg parameter surface (CHECK / STEP / UNIT / COMMENT),
/// the canonical-depth scale expression, and the Struct return of the
/// _full bundle without needing the ~1.3 GB bundles.
/// </summary>
public sealed class Da3MetricSqlParseTests : ServiceTestBase
{
    private string LoadCanonicalSql(string modelId)
    {
        IManifestStore store = GetService<IManifestStore>();
        CatalogVariant model = store.Manifest.Entries.SelectMany(e => e.Variants).First(v => v.Id == modelId);
        if (string.IsNullOrEmpty(model.InstallSql))
        {
            throw new InvalidOperationException($"Catalog entry '{modelId}' has no installSql.");
        }
        string sqlPath = Path.Combine(store.ManifestDirectory, model.InstallSql);
        return File.ReadAllText(sqlPath);
    }

    [Theory]
    [InlineData("da3metric-large", "da3metric_large")]
    [InlineData("da3metric-large-fp16", "da3metric_large_fp16")]
    public void Da3Metric_InstallSql_ParsesToThreeCreateModelStatements(string catalogId, string baseName)
    {
        // ParseBatchWithText is the same path the catalog installer uses
        // at engine startup; catches parser regressions early.
        string sql = LoadCanonicalSql(catalogId);
        IReadOnlyList<(Statement Statement, string SourceText)> statements =
            SqlParser.ParseBatchWithText(sql);
        Assert.Equal(3, statements.Count);

        CreateModelStatement viz = Assert.IsType<CreateModelStatement>(statements[0].Statement);
        Assert.Equal(baseName, viz.Name);
        Assert.Single(viz.Parameters);

        CreateModelStatement meters = Assert.IsType<CreateModelStatement>(statements[1].Statement);
        Assert.Equal($"{baseName}_meters", meters.Name);
        // img + defaulted fov_deg — the default is what lets the two-arg
        // body still satisfy the [Image] → Array<Float32> metric contract.
        Assert.Equal(2, meters.Parameters.Count);
        Assert.NotNull(meters.Parameters[1].Default);

        CreateModelStatement full = Assert.IsType<CreateModelStatement>(statements[2].Statement);
        Assert.Equal($"{baseName}_full", full.Name);
        Assert.Equal(2, full.Parameters.Count);
        Assert.NotNull(full.Parameters[1].Default);
    }
}
