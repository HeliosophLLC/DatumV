using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog.Providers;

/// <summary>
/// Tests for the <c>body_scope</c> column on <c>datum_catalog.functions</c>.
/// The column is annotate-not-hide: body-scoped functions like
/// <c>infer()</c> still appear so users can discover them via
/// <c>WHERE body_scope = 'modelbody'</c>; the plan-time gate (covered by
/// <c>ModelRegistrationTests</c>) is what refuses out-of-context call
/// sites.
/// </summary>
public sealed class DatumCatalogFunctionsProviderTests : ServiceTestBase
{
    [Fact]
    public async Task BodyScopeColumn_DiscriminatesInferAsModelBody()
    {
        TableCatalog catalog = CreateCatalog();

        // SELECT body_scope, function_name FROM datum_catalog.functions
        // WHERE function_name = 'infer'
        IQueryPlan plan = catalog.Plan(
            "SELECT body_scope FROM datum_catalog.functions WHERE function_name = 'infer'");

        List<string> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Single(values);
        Assert.Equal("modelbody", values[0]);
    }

    [Fact]
    public async Task BodyScopeColumn_NoneForOrdinaryScalars()
    {
        TableCatalog catalog = CreateCatalog();

        // upper() is a vanilla scalar — must report 'none' so users filtering
        // for callable-anywhere functions (the default expectation) get it.
        IQueryPlan plan = catalog.Plan(
            "SELECT body_scope FROM datum_catalog.functions WHERE function_name = 'upper'");

        List<string> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0].AsString(batch.Arena));
            }
        }

        Assert.Single(values);
        Assert.Equal("none", values[0]);
    }
}
