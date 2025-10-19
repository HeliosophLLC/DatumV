using System.Runtime.CompilerServices;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Simple in-memory table provider for testing. Yields cloned copies of the
/// supplied rows so each batch owns independent <see cref="DataValue"/> arrays,
/// matching the production <c>ScanOperator</c>'s <c>pool.Rent</c> behavior.
/// This ensures <see cref="DatumIngest.Execution.LocalBufferPool.ReturnBatch"/>
/// can safely return the arrays without corrupting test data.
/// </summary>
public sealed class InMemoryTableProvider : ITableProvider
{
    private readonly string[] _columns;
    private readonly Row[] _rows;

    /// <summary>
    /// Initializes a new instance of <see cref="InMemoryTableProvider"/> with the specified rows.
    /// </summary>
    /// <param name="rows">The rows of data in the table.</param>
    public InMemoryTableProvider(Row[] rows)
    {
        _rows = rows;
        _columns = rows.Length == 0
            ? Array.Empty<string>()
            : rows[0].ColumnNames?.ToArray()
                ?? rows[0].RawValues.Select((v, i) => $"Column{i}").ToArray();
    }

    /// <summary>
    /// Initializes a new instance of <see cref="InMemoryTableProvider"/> with the specified columns and rows.
    /// </summary>
    /// <param name="columns">The names of the columns in the table.</param>
    /// <param name="rows">The rows of data in the table.</param>
    public InMemoryTableProvider(string[] columns, Row[] rows)
    {
        _columns = columns;
        _rows = rows;
    }

    /// <inheritdoc/>
    public bool Seekable => throw new NotImplementedException();

    /// <inheritdoc/>
    public void Dispose()
    {
        
    }

    /// <inheritdoc/>
    public long GetRowCount(TableDescriptor descriptor)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public Task<Schema> GetSchemaAsync(TableDescriptor descriptor, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> OpenAsync(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> OpenAsync(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns, Expression filterHint, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<RowBatch> ReadRowRangeAsync(TableDescriptor descriptor, IReadOnlySet<string>? requiredColumns, long startRow, int count, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}