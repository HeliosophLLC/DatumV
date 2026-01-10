using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR11a tests for plan-time validation of <c>UPDATE</c> statements.
/// The executor itself is a stub that throws
/// <see cref="NotSupportedException"/> after validation passes (the
/// rewrite path lands in PR11b/c). These tests pin the validation
/// surface so the executor can be slotted in without API churn.
/// </summary>
public sealed class UpdateValidationTests
{
    private static TableCatalog NewCatalog() => new(new Pool(new PoolBacking()));

    [Fact]
    public void Update_MissingTable_Throws()
    {
        using TableCatalog catalog = NewCatalog();

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE missing SET x = 1"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Update_UnknownColumn_Throws()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET no_such_col = 1"));
        Assert.Contains("no_such_col", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Update_DuplicateColumnInSetList_Throws()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET name = 'a', name = 'b'"));
        Assert.Contains("name", ex.Message);
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Update_DuplicateColumnInSetList_CaseInsensitive_Throws()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET name = 'a', NAME = 'b'"));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Update_PrimaryKeyColumn_Rejected()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET id = 99"));
        Assert.Contains("PRIMARY KEY", ex.Message);
        Assert.Contains("DELETE and re-INSERT", ex.Message);
    }

    [Fact]
    public void Update_PrimaryKeyColumn_Composite_Rejected()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (a Int32, b Int32, c String, PRIMARY KEY (a, b))");

        // Updating a non-PK column is fine (validation passes; stub throws NotSupportedException).
        Assert.Throws<NotSupportedException>(() => catalog.Plan("UPDATE t SET c = 'x'"));

        // Updating either PK component is rejected.
        Assert.Throws<QueryPlanException>(() => catalog.Plan("UPDATE t SET a = 1"));
        Assert.Throws<QueryPlanException>(() => catalog.Plan("UPDATE t SET b = 2"));
    }

    [Fact]
    public void Update_NonPkColumn_PassesValidation()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String, score Float32)");

        // Validation passes; executor stub throws NotSupportedException.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => catalog.Plan("UPDATE t SET name = 'a', score = 1.0"));
        Assert.Contains("PR11b", ex.Message);
    }

    [Fact]
    public void Update_AfterValidation_StubThrowsWithPr11bHint()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => catalog.Plan("UPDATE t SET name = 'a' WHERE id = 1"));
        Assert.Contains("PR11b", ex.Message);
    }

    [Fact]
    public void Update_WithFromClause_ValidationPasses()
    {
        using TableCatalog catalog = NewCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float32)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float32)");

        // Validation should pass through to the executor stub.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(
            () => catalog.Plan(
                "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id"));
        Assert.Contains("PR11b", ex.Message);
    }
}
