using System.Runtime.CompilerServices;
using DatumIngest.Indexing;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// In-memory table provider for tests and small fixtures. Yields cloned copies of the
/// supplied rows so each batch owns independent <see cref="DataValue"/> arrays — this
/// matches the production <c>ScanOperator</c>'s pool-rented behaviour, so downstream
/// consumers can safely return batches to <see cref="DatumIngest.Execution.LocalBufferPool"/>
/// without corrupting the test fixture.
/// </summary>
public sealed class InMemoryTableProvider : ITableProvider
{
    private const int DefaultBatchSize = 64;

    private readonly string[] _columns;
    private readonly Row[] _rows;
    private readonly Schema _schema;

    /// <summary>
    /// Creates a provider from a sequence of rows. Column names are derived from the first
    /// row's <see cref="Row.ColumnNames"/>; schema kinds are inferred from the first row's
    /// values. When <paramref name="rows"/> is empty, a single <c>"empty"</c> column is used.
    /// </summary>
    /// <param name="name">The name of the table.</param>
    /// <param name="rows">The rows to serve.</param>
    public InMemoryTableProvider(string name, Row[] rows)
    {
        Name = name;
        _rows = rows;
        _columns = rows.Length == 0
            ? Array.Empty<string>()
            : rows[0].ColumnNames.ToArray();
        _schema = BuildSchema(_columns, _rows);
    }

    /// <summary>
    /// Creates a provider from explicit column names and rows. Use this when the first
    /// row's column names might not represent the full schema.
    /// </summary>
    /// <param name="name">The name of the table.</param>
    /// <param name="columns">Column names for the schema.</param>
    /// <param name="rows">The rows to serve. Each row must have values in <paramref name="columns"/> order.</param>
    public InMemoryTableProvider(string name, string[] columns, Row[] rows)
    {
        this.Name = name;
        _columns = columns;
        _rows = rows;
        _schema = BuildSchema(_columns, _rows);
    }

    /// <inheritdoc/>
    public string Name { get; }

    /// <inheritdoc/>
    public bool Seekable => true;

    /// <inheritdoc/>
    public void Dispose() { }

    /// <inheritdoc/>
    public long GetRowCount() => _rows.Length;

    /// <inheritdoc/>
    public Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public Manifest.QueryResultsManifest? GetManifest() => null;

    /// <inheritdoc/>
    public SourceIndex? GetSourceIndex() => null;

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // filterHint is advisory for zone-map pruning. An in-memory table has no
        // partitions, so the hint is unused — the caller applies the filter downstream.
        await foreach (RowBatch batch in EmitRows(_rows, cancellationToken).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    /// <inheritdoc/>
    public ISeekSession OpenSeekSession(IReadOnlySet<string>? requiredColumns)
        => new InMemorySeekSession(_rows);

    private sealed class InMemorySeekSession : ISeekSession
    {
        private readonly Row[] _rows;

        internal InMemorySeekSession(Row[] rows)
        {
            _rows = rows;
        }

        public async IAsyncEnumerable<RowBatch> SeekAsync(
            long startRow,
            int count,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (startRow >= _rows.Length || count <= 0)
            {
                yield break;
            }

            int start = (int)Math.Min(startRow, int.MaxValue);
            int available = Math.Min(count, _rows.Length - start);
            ArraySegment<Row> slice = new(_rows, start, available);

            await foreach (RowBatch batch in EmitRows(slice, cancellationToken).ConfigureAwait(false))
            {
                yield return batch;
            }
        }

        public void Dispose() { }
    }

    private static async IAsyncEnumerable<RowBatch> EmitRows(
        IEnumerable<Row> rows,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        RowBatch batch = RowBatch.Rent(DefaultBatchSize);

        foreach (Row row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Clone so the batch owns a fresh DataValue[] — when the consumer returns the
            // batch to the pool, the fixture's original arrays are not corrupted.
            batch.Add(row.Clone());

            if (batch.IsFull)
            {
                yield return batch;
                batch = RowBatch.Rent(DefaultBatchSize);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }

        await Task.CompletedTask;
    }

    private static Schema BuildSchema(string[] columns, Row[] rows)
    {
        if (columns.Length == 0)
        {
            return new Schema([new ColumnInfo("empty", DataKind.String, nullable: true)]);
        }

        ColumnInfo[] infos = new ColumnInfo[columns.Length];
        for (int i = 0; i < columns.Length; i++)
        {
            DataKind kind = InferKind(rows, columns[i], i);
            infos[i] = new ColumnInfo(columns[i], kind, nullable: true);
        }

        return new Schema(infos);
    }

    private static DataKind InferKind(Row[] rows, string columnName, int ordinal)
    {
        // Walk rows until a non-null value is found; fall back to String for all-null columns.
        foreach (Row row in rows)
        {
            DataValue value = ordinal < row.FieldCount
                ? row[ordinal]
                : row[columnName];

            if (!value.IsNull)
            {
                return value.Kind;
            }
        }

        return DataKind.String;
    }
}
