using DatumIngest.Catalog;
using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Verifies that the three DML executors (INSERT / UPDATE / DELETE) report
/// their held-state bytes into a shared <see cref="MemoryAccountant"/> when
/// invoked with a non-null <see cref="BatchContext"/>. Each test seeds a
/// small table, runs DML, asserts the accountant counter rose at peak and
/// returned to zero at the end (matching NotifyMaterialized/NotifyReleased
/// pairs).
/// </summary>
public sealed class DmlAccountingTests : ServiceTestBase
{
    [Fact]
    public async Task Update_InsideBatchContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"],
            [2, "bob"],
            [3, "carol"]);
        UpdateStatement update = (UpdateStatement)SqlParser.ParseBatch("UPDATE t SET name = 'updated'")[0];

        using BatchContext batch = new();
        long before = batch.Accountant.CurrentResidentBytes;

        await UpdateExecutor.ExecuteAsync(catalog, update, batch);

        // All three rows match the empty WHERE, so PeakResidentBytes must
        // have risen above the baseline before NotifyReleased zeroed the
        // counter back out.
        Assert.True(
            batch.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {batch.Accountant.PeakResidentBytes}");
        Assert.Equal(before, batch.Accountant.CurrentResidentBytes);
    }

    [Fact]
    public async Task Update_WithoutBatchContext_IsBackwardCompatible()
    {
        // No BatchContext: executor must run unchanged, no accountant calls.
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"]);
        UpdateStatement update = (UpdateStatement)SqlParser.ParseBatch("UPDATE t SET name = 'changed'")[0];

        await UpdateExecutor.ExecuteAsync(catalog, update);

        // No assertion beyond "didn't throw" — the path with batchContext=null
        // must keep working for the shell / web / migrations callers.
    }

    [Fact]
    public async Task Insert_InsideBatchContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"]);
        InsertStatement insert = (InsertStatement)SqlParser.ParseBatch(
            "INSERT INTO t (id, name) VALUES (2, 'bob'), (3, 'carol'), (4, 'dave')")[0];

        using BatchContext batch = new();
        long before = batch.Accountant.CurrentResidentBytes;

        await InsertExecutor.ExecuteAsync(catalog, insert, batch);

        Assert.True(
            batch.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {batch.Accountant.PeakResidentBytes}");
        Assert.Equal(before, batch.Accountant.CurrentResidentBytes);
    }

    [Fact]
    public async Task Delete_InsideBatchContext_NotifiesAccountant_AndReleases()
    {
        TableCatalog catalog = CreateCatalog("t",
            columns: ["id", "name"],
            [1, "alice"],
            [2, "bob"],
            [3, "carol"]);
        DeleteStatement delete = (DeleteStatement)SqlParser.ParseBatch("DELETE FROM t")[0];

        using BatchContext batch = new();
        long before = batch.Accountant.CurrentResidentBytes;

        await DeleteExecutor.ExecuteAsync(catalog, delete, batch);

        Assert.True(
            batch.Accountant.PeakResidentBytes > before,
            $"PeakResidentBytes should have risen above {before}; got {batch.Accountant.PeakResidentBytes}");
        Assert.Equal(before, batch.Accountant.CurrentResidentBytes);
    }
}
