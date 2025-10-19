using System.Runtime.CompilerServices;
using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests;

/// <summary>
/// Simple in-memory table provider for testing. Yields cloned copies of the
/// supplied rows so each batch owns independent <see cref="DataValue"/> arrays,
/// matching the production <c>ScanOperator</c>'s <c>pool.Rent</c> behavior.
/// This ensures <see cref="DatumIngest.Execution.LocalBufferPool.ReturnBatch"/>
/// can safely return the arrays without corrupting test data.
/// </summary>
internal sealed class InMemoryTableProvider : ITableProvider
{
    private readonly Row[] _rows;

    public InMemoryTableProvider(Row[] rows)
    {
        _rows = rows;
    }

    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        if (_rows.Length == 0)
        {
            return Task.FromResult(new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]));
        }

        List<ColumnInfo> columns = [];
        foreach (string name in _rows[0].ColumnNames)
        {
            columns.Add(new ColumnInfo(name, _rows[0][name].Kind, nullable: true));
        }

        return Task.FromResult(new Schema(columns));
    }

    public long GetRowCount(TableDescriptor descriptor)
    {
        return _rows.Length;
    }

    public async IAsyncEnumerable<RowBatch> OpenAsync(
        TableDescriptor descriptor,
        IReadOnlySet<string>? requiredColumns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RowBatch batch = RowBatch.Rent(64);

        foreach (Row row in _rows)
        {
            batch.Add(row.Clone());

            if (batch.IsFull)
            {
                yield return batch;
                batch = RowBatch.Rent(64);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }
}