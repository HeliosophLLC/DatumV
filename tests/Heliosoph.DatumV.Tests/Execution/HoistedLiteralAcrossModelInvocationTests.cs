namespace Heliosoph.DatumV.Tests.Execution;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;

/// <summary>
/// Regression for the user's <c>SELECT models.llama31_8b(concat('long literal', "Description"))</c>
/// failure — it threw <c>"Arena[#3] has not been allocated... GetString at offset=0 length=64"</c>
/// because the hoisted long-string literal lived in <c>context.Store</c> (the plan-scoped
/// hoist arena) but <see cref="ModelInvocationOperator"/> built the EvaluationFrame
/// from <c>sourceBatch.Arena</c>. With providers renting fresh per-batch arenas at the
/// time, <c>sourceBatch.Arena</c> wasn't <c>context.Store</c>, so the literal lookup
/// hit an empty unrelated arena.
/// </summary>
/// <remarks>
/// Pinned by the one-arena-per-query Path B work — once providers bind their batches
/// to <c>context.Store</c> via the new <c>targetArena</c> parameter on
/// <see cref="ITableProvider.ScanAsync"/>, <c>sourceBatch.Arena == context.Store</c>
/// and the hoisted-literal lookup succeeds.
/// </remarks>
public sealed class HoistedLiteralAcrossModelInvocationTests : ServiceTestBase
{
    private const string LongLiteral =
        "Please interpret this description from the chicago crimes dataset: ";

    private static ModelCatalog BuildEchoCatalog()
    {
        ModelCatalog models = new(modelDirectory: System.IO.Path.GetTempPath());
        models.Register(new ModelCatalogEntry(
            Name: "echo",
            Backend: "echo",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: _ => EchoModel.Instance,
            OptionalArgKinds: null));
        return models;
    }

    /// <summary>
    /// The original failure shape: a long string literal (>16 bytes, forcing arena
    /// storage) concatenated with an arena-backed column value, dispatched through
    /// a model. The plan tree is <c>Project &gt; ModelInvocation &gt; Scan &gt; InMemoryProvider</c>
    /// — same shape as the user's <c>FROM "Crimes" LIMIT 10</c> query (LIMIT just
    /// trims the upstream batches, doesn't change the arena story).
    /// </summary>
    [Fact]
    public async Task LongHoistedLiteral_PlusArenaBackedColumn_ConcatThroughModel_Succeeds()
    {
        // Long enough to force arena-backed storage in the column too.
        const string description1 =
            "Battery in domestic situation reported at the corner of state and main";
        const string description2 =
            "Theft from motor vehicle reported at parking garage on lower wacker";

        TableCatalog catalog = CreateCatalog(
            tableName: "crimes",
            columns: ["description"],
            new object?[] { description1 },
            new object?[] { description2 });
        catalog.Models = BuildEchoCatalog();

        Assert.True(LongLiteral.Length > 16, "Test premise: literal must be long enough to force arena-backed storage.");

        // The original failure threw DURING execution at ToValueRef → AsString
        // when concat tried to read the hoisted literal against the wrong arena.
        // Reaching this assertion at all means the arena routing is intact —
        // we don't read row content here because the per-query arena that holds
        // arena-backed strings goes out of scope inside ExecuteQueryAsync, so a
        // post-execution AsString against an unrelated scratch arena would
        // legitimately fail to resolve. Row count is the meaningful signal.
        List<Row> rows = await ExecuteQueryAsync(
            $"SELECT models.echo(concat('{LongLiteral}', description)) FROM crimes",
            catalog);

        Assert.Equal(2, rows.Count);
        // Each output row carries one column (the model's response).
        Assert.Equal(1, rows[0].FieldCount);
        Assert.Equal(1, rows[1].FieldCount);
        // And the values are non-null (a model invocation that ran but failed
        // mid-arg-evaluation would surface as a typed null or an exception).
        Assert.False(rows[0][0].IsNull);
        Assert.False(rows[1][0].IsNull);
    }

    /// <summary>
    /// Same shape but with <c>LIMIT</c> in the way — the user's actual query had
    /// <c>LIMIT 10</c>. Confirms LIMIT doesn't perturb the arena-routing story.
    /// </summary>
    [Fact]
    public async Task LongHoistedLiteral_WithLimitClause_Succeeds()
    {
        TableCatalog catalog = CreateCatalog(
            tableName: "crimes",
            columns: ["description"],
            new object?[] { "long enough description for arena-backed storage" },
            new object?[] { "another long description for arena-backed storage" },
            new object?[] { "third long description should not appear due to LIMIT" });
        catalog.Models = BuildEchoCatalog();

        List<Row> rows = await ExecuteQueryAsync(
            $"SELECT models.echo(concat('{LongLiteral}', description)) FROM crimes LIMIT 2",
            catalog);

        // LIMIT 2 of 3 inputs → exactly 2 rows reach the model.
        Assert.Equal(2, rows.Count);
        Assert.False(rows[0][0].IsNull);
        Assert.False(rows[1][0].IsNull);
    }
}
