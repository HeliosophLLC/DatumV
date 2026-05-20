using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Execution;

/// <summary>
/// End-to-end SQL tests pinning the Type Registry contract at the query boundary.
/// Earlier tests cover registry primitives (TypeRegistryTests), the function
/// (TypeofFunctionTests), and the lift path (StructValueRefLiftTests). These
/// run real queries to confirm the contract survives plan/execute without
/// regressing.
/// </summary>
public sealed class TypeRegistryIntegrationTests : ServiceTestBase
{
    private TableCatalog OneRow() =>
        CreateCatalog("data", ["id"], [1]);

    // ─── SELECT [] — empty array literal ────────────────────────────────────

    [Fact]
    public async Task EmptyArrayLiteral_ProducesEmptyArrayValue()
    {
        // Pins the runtime contract: `SELECT []` returns an array value, not a
        // null or scalar. The element kind defaults to String per
        // ArrayConstructorFunction (zero args ⇒ String). The TypeRegistry isn't
        // involved for primitive-element arrays — TypeId stays 0.
        TableCatalog catalog = OneRow();
        List<Row> rows = await ExecuteQueryAsync("SELECT [] AS arr FROM data", catalog);

        Assert.Single(rows);
        DataValue arr = rows[0]["arr"];
        Assert.True(arr.IsArray);
        Assert.False(arr.IsNull);
        Assert.Equal(DataKind.String, arr.Kind);
        Assert.Equal((ushort)0, arr.TypeId);
    }

    // ─── typeof(NULL) — null Type value ─────────────────────────────────────

    [Fact]
    public async Task TypeofNull_ReturnsNullTypeValue()
    {
        // `typeof(NULL)` is a null Type value — the design says null has no
        // inhabitable type identity, so downstream rendering shows "NULL"
        // rather than a kind name. Unit-tested in CastFunctionTests; the
        // integration test confirms the planner/evaluator wiring carries
        // the null through.
        TableCatalog catalog = OneRow();
        List<Row> rows = await ExecuteQueryAsync(
            "SELECT typeof(NULL) AS t FROM data",
            catalog);

        Assert.Single(rows);
        DataValue t = rows[0]["t"];
        Assert.Equal(DataKind.Type, t.Kind);
        Assert.True(t.IsNull);
    }

    [Fact]
    public async Task TypeofNullableColumn_PropagatesNullPerRow()
    {
        // Heterogeneous null/non-null inputs across rows. Row with non-null
        // string returns Type(String); row with null returns null Type.
        TableCatalog catalog = CreateCatalog("data", ["s"],
            ["hello"],
            [DataValue.Null(DataKind.String)]);

        List<Row> rows = await ExecuteQueryAsync(
            "SELECT typeof(s) AS t FROM data",
            catalog);

        Assert.Equal(2, rows.Count);
        Assert.False(rows[0]["t"].IsNull);
        Assert.Equal(DataKind.String, rows[0]["t"].AsType());
        Assert.True(rows[1]["t"].IsNull);
        Assert.Equal(DataKind.Type, rows[1]["t"].Kind);
    }

    // ─── typeof({…}) — struct literal flows through registry ────────────────

    [Fact]
    public async Task TypeofStructLiteral_CarriesRegisteredTypeId()
    {
        // The struct literal flows through ExpressionEvaluator → registry →
        // typeof. The output Type value's TypeId must be non-zero (registered),
        // and the registry must round-trip the field names.
        TableCatalog catalog = OneRow();

        // Build a context so we can inspect its registry afterward. Copy the
        // ExecuteQueryAsync flow to retain access to the context.
        var query = Heliosoph.DatumV.Parsing.SqlParser.Parse("SELECT typeof({a: 'test'}) AS t FROM data");
        var planner = new Heliosoph.DatumV.Execution.QueryPlanner(catalog, Heliosoph.DatumV.Functions.FunctionRegistry.CreateDefault());
        var context = CreateExecutionContext(catalog: catalog);
        var plan = await planner.PlanWithSubqueriesAsync(query, context, CancellationToken.None);
        List<Row> rows = await plan.CollectRowsAsync(context);

        Assert.Single(rows);
        DataValue t = rows[0]["t"];
        Assert.Equal(DataKind.Type, t.Kind);
        Assert.Equal(DataKind.Struct, t.AsType());
        Assert.NotEqual((ushort)0, t.TypeId);

        TypeDescriptor? desc = context.Types.GetDescriptor(t.TypeId);
        Assert.NotNull(desc);
        Assert.Equal(DataKind.Struct, desc!.Kind);
        Assert.NotNull(desc.Fields);
        Assert.Single(desc.Fields!);
        Assert.Equal("a", desc.Fields![0].Name);
    }
}
