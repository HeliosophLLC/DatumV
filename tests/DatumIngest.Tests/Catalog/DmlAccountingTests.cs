using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// Verifies that the three DML executors (INSERT / UPDATE / DELETE) report
/// their held-state bytes into a shared <see cref="MemoryAccountant"/> when
/// invoked with a non-null <see cref="ExecutionContext"/>. Each test seeds a
/// small table, runs DML, asserts the accountant counter rose at peak and
/// returned to zero at the end (matching NotifyMaterialized/NotifyReleased
/// pairs).
/// </summary>
public sealed class DmlAccountingTests : ServiceTestBase
{
    [Fact]
    public async Task Update_InsideExecutionContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"],
            [2, "bob"],
            [3, "carol"]);
        UpdateStatement update = (UpdateStatement)SqlParser.ParseBatch("UPDATE t SET name = 'updated'")[0];

        using Heliosoph.DatumV.Execution.ExecutionContext context = catalog.CreateExecutionContext();
        long before = context.Accountant.CurrentResidentBytes;

        await UpdateExecutor.ApplyAsync(catalog, update, null, context);

        // All three rows match the empty WHERE, so PeakResidentBytes must
        // have risen above the baseline before NotifyReleased zeroed the
        // counter back out.
        Assert.True(
            context.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {context.Accountant.PeakResidentBytes}");
        Assert.Equal(before, context.Accountant.CurrentResidentBytes);
    }

    [Fact]
    public async Task Insert_InsideExecutionContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"]);
        InsertStatement insert = (InsertStatement)SqlParser.ParseBatch(
            "INSERT INTO t (id, name) VALUES (2, 'bob'), (3, 'carol'), (4, 'dave')")[0];

        using Heliosoph.DatumV.Execution.ExecutionContext context = catalog.CreateExecutionContext();
        long before = context.Accountant.CurrentResidentBytes;

        await InsertExecutor.ApplyAsync(catalog, insert, null, null, context);

        Assert.True(
            context.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {context.Accountant.PeakResidentBytes}");
        Assert.Equal(before, context.Accountant.CurrentResidentBytes);
    }

    [Fact]
    public async Task Delete_InsideExecutionContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"],
            [2, "bob"],
            [3, "carol"]);
        DeleteStatement delete = (DeleteStatement)SqlParser.ParseBatch("DELETE FROM t")[0];

        using Heliosoph.DatumV.Execution.ExecutionContext context = catalog.CreateExecutionContext();
        long before = context.Accountant.CurrentResidentBytes;

        await DeleteExecutor.ApplyAsync(catalog, delete, null, context);

        Assert.True(
            context.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {context.Accountant.PeakResidentBytes}");
        Assert.Equal(before, context.Accountant.CurrentResidentBytes);
    }
}
