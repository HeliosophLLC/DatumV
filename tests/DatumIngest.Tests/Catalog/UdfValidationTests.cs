using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// End-to-end tests for the runtime-validation extensions on UDFs:
/// <c>IS NOT NULL</c> on parameters, <c>RETURNS T</c> as an implicit
/// CAST, and <c>RETURNS T IS NOT NULL</c> as a layered cast + null
/// assertion. These run a real query through <c>TableCatalog.Plan</c>
/// and execute it so the assertions exercise the full inliner + planner
/// + evaluator path.
/// </summary>
public class UdfValidationTests : ServiceTestBase
{
    private static async Task<List<DataValue>> CollectFirstColumnAsync(IQueryPlan plan)
    {
        List<DataValue> values = new();
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                values.Add(batch[i][0]);
            }
        }
        return values;
    }

    [Fact]
    public async Task NotNullParam_NullArg_Throws()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { null });

        catalog.Plan("CREATE FUNCTION shout(s STRING IS NOT NULL) AS upper(s)");
        IQueryPlan plan = catalog.Plan("SELECT shout(v) FROM data");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));

        // The evaluator wraps the assertion in ExpressionEvaluationException,
        // but the user-visible message (on either layer) names the parameter
        // so the user can locate the offending arg.
        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("s", fullMessage);
        Assert.Contains("must not be null", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NotNullParam_NonNullArg_PassesThrough()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { "hello" });

        catalog.Plan("CREATE FUNCTION shout(s STRING IS NOT NULL) AS upper(s)");
        IQueryPlan plan = catalog.Plan("SELECT shout(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Single(values);
        Assert.Equal("HELLO", values[0].AsString());
    }

    [Fact]
    public async Task NullableParam_NullArg_PropagatesThroughBody()
    {
        // Without IS NOT NULL, NULL flows through and the body's normal
        // three-valued logic decides the output (upper(NULL) → NULL).
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { null });

        catalog.Plan("CREATE FUNCTION shout(s STRING) AS upper(s)");
        IQueryPlan plan = catalog.Plan("SELECT shout(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Single(values);
        Assert.True(values[0].IsNull);
    }

    [Fact]
    public async Task ReturnsType_CoercesBodyToDeclaredKind()
    {
        // Body produces Float64; RETURNS INT32 wraps with CAST so the
        // call site sees Int32 regardless of the body's natural kind.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { 3.7 });

        catalog.Plan("CREATE FUNCTION truncated(x FLOAT64) RETURNS INT32 AS x");
        IQueryPlan plan = catalog.Plan("SELECT truncated(v) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(DataKind.Int32, values[0].Kind);
        Assert.Equal(3, values[0].AsInt32());
    }

    [Fact]
    public async Task ReturnsTypeIsNotNull_NullBody_Throws()
    {
        // try_cast returns NULL when conversion fails. With RETURNS IS NOT NULL,
        // a NULL body throws at the call site instead of propagating.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["s"],
            new object?[] { "not a number" });

        catalog.Plan(
            "CREATE FUNCTION parsed(s STRING) RETURNS INT32 IS NOT NULL " +
            "AS try_cast(s, INT32)");
        IQueryPlan plan = catalog.Plan("SELECT parsed(s) FROM data");

        Exception ex = await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));

        string fullMessage = ex.Message + (ex.InnerException?.Message ?? "");
        Assert.Contains("parsed", fullMessage);
        Assert.Contains("must not be null", fullMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReturnsTypeIsNotNull_NonNullBody_ReturnsValue()
    {
        TableCatalog catalog = CreateCatalog("data",
            columns: ["s"],
            new object?[] { "42" });

        catalog.Plan(
            "CREATE FUNCTION parsed(s STRING) RETURNS INT32 IS NOT NULL " +
            "AS try_cast(s, INT32)");
        IQueryPlan plan = catalog.Plan("SELECT parsed(s) FROM data");

        List<DataValue> values = await CollectFirstColumnAsync(plan);
        Assert.Equal(DataKind.Int32, values[0].Kind);
        Assert.Equal(42, values[0].AsInt32());
    }

    [Fact]
    public async Task NotNullParam_AppliesPerRow_NullMidStreamThrows()
    {
        // Each row's argument is checked independently. A mid-stream NULL
        // throws during that row's evaluation; whether the rows preceding
        // it surface depends on batching.
        TableCatalog catalog = CreateCatalog("data",
            columns: ["v"],
            new object?[] { "first" },
            new object?[] { null },
            new object?[] { "third" });

        catalog.Plan("CREATE FUNCTION shout(s STRING IS NOT NULL) AS upper(s)");
        IQueryPlan plan = catalog.Plan("SELECT shout(v) FROM data");

        await Assert.ThrowsAnyAsync<Exception>(
            () => CollectFirstColumnAsync(plan));
    }
}
