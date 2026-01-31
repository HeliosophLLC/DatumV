using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// PR11a tests for plan-time validation of <c>UPDATE</c> statements.
/// PR11c shipped the executor, so successful-validation tests here only
/// assert that no exception is thrown; deeper end-to-end coverage lives
/// in <c>UpdateExecutorTests</c>. <c>UPDATE … FROM</c> still goes through
/// the PR11d-pending rejection path.
/// </summary>
public sealed class UpdateValidationTests : ServiceTestBase
{
    [Fact]
    public void Update_MissingTable_Throws()
    {
        using TableCatalog catalog = CreateCatalog();

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE missing SET x = 1"));
        Assert.Contains("not registered", ex.Message);
    }

    [Fact]
    public void Update_UnknownColumn_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET no_such_col = 1"));
        Assert.Contains("no_such_col", ex.Message);
        Assert.Contains("does not exist", ex.Message);
    }

    [Fact]
    public void Update_DuplicateColumnInSetList_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET name = 'a', name = 'b'"));
        Assert.Contains("name", ex.Message);
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Update_DuplicateColumnInSetList_CaseInsensitive_Throws()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET name = 'a', NAME = 'b'"));
        Assert.Contains("more than once", ex.Message);
    }

    [Fact]
    public void Update_PrimaryKeyColumn_Rejected()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan("UPDATE t SET id = 99"));
        Assert.Contains("PRIMARY KEY", ex.Message);
        Assert.Contains("DELETE and re-INSERT", ex.Message);
    }

    [Fact]
    public void Update_PrimaryKeyColumn_Composite_Rejected()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan(
            "CREATE TEMP TABLE t (a Int32, b Int32, c String, PRIMARY KEY (a, b))");

        // Updating a non-PK column passes validation (and executes —
        // empty table, so it's a no-op, but it must not throw).
        catalog.Plan("UPDATE t SET c = 'x'");

        // Updating either PK component is rejected at plan time.
        Assert.Throws<QueryPlanException>(() => catalog.Plan("UPDATE t SET a = 1"));
        Assert.Throws<QueryPlanException>(() => catalog.Plan("UPDATE t SET b = 2"));
    }

    [Fact]
    public void Update_NonPkColumn_PassesValidation()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE t (id Int32 PRIMARY KEY, name String, score Float32)");

        // No exception — validation passes, executor runs (empty table, no-op).
        catalog.Plan("UPDATE t SET name = 'a', score = 1.0");
    }

    [Fact]
    public void Update_WithFromClause_PassesValidation()
    {
        // PR11d wired UPDATE … FROM end-to-end. Validation passes;
        // executor runs against empty target, no-op.
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float32)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, value Float32)");

        catalog.Plan(
            "UPDATE features SET score = raw.value FROM raw WHERE features.id = raw.id");
    }

    [Fact]
    public void Update_WithJoinInsideFrom_RejectedAsPending()
    {
        using TableCatalog catalog = CreateCatalog();
        catalog.Plan("CREATE TEMP TABLE features (id Int32, score Float32)");
        catalog.Plan("CREATE TEMP TABLE raw (id Int32, model_id Int32, value Float32)");
        catalog.Plan("CREATE TEMP TABLE model (id Int32, weight Float32)");

        QueryPlanException ex = Assert.Throws<QueryPlanException>(
            () => catalog.Plan(
                "UPDATE features SET score = raw.value * model.weight " +
                "FROM raw JOIN model ON raw.model_id = model.id " +
                "WHERE features.id = raw.id"));
        Assert.Contains("JOIN inside the FROM clause", ex.Message);
    }
}
