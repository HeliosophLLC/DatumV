namespace Axon.QueryEngine.Model;

/// <summary>
/// A columnar batch of rows sharing a common <see cref="Model.Schema"/>.
/// Data is stored column-major: each inner array holds all values for one column.
/// </summary>
public sealed class RowBatch
{
    private readonly DataValue[][] _columns;

    /// <summary>
    /// Creates a batch from a schema and column-major data arrays.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when the number of column arrays does not match the schema,
    /// or when column arrays have different lengths (jagged).
    /// </exception>
    public RowBatch(Schema schema, DataValue[][] columns)
    {
        if (columns.Length != schema.Columns.Count)
        {
            throw new ArgumentException(
                $"Expected {schema.Columns.Count} columns but received {columns.Length}.");
        }

        // All column arrays must have the same length.
        if (columns.Length > 0)
        {
            int expectedRowCount = columns[0].Length;
            for (int columnIndex = 1; columnIndex < columns.Length; columnIndex++)
            {
                if (columns[columnIndex].Length != expectedRowCount)
                {
                    throw new ArgumentException(
                        $"Column {columnIndex} has {columns[columnIndex].Length} rows "
                        + $"but column 0 has {expectedRowCount} rows.");
                }
            }
        }

        Schema = schema;
        _columns = columns;
    }

    /// <summary>The schema describing every column in this batch.</summary>
    public Schema Schema { get; }

    /// <summary>The number of rows across all columns.</summary>
    public int RowCount => _columns.Length == 0 ? 0 : _columns[0].Length;

    /// <summary>The number of columns.</summary>
    public int ColumnCount => _columns.Length;

    /// <summary>
    /// Materialises a single row by gathering values from each column at the given index.
    /// </summary>
    public Row GetRow(int rowIndex)
    {
        string[] names = new string[_columns.Length];
        DataValue[] values = new DataValue[_columns.Length];

        for (int columnIndex = 0; columnIndex < _columns.Length; columnIndex++)
        {
            names[columnIndex] = Schema.Columns[columnIndex].Name;
            values[columnIndex] = _columns[columnIndex][rowIndex];
        }

        return new Row(names, values);
    }

    /// <summary>
    /// Returns a read-only span over all values in the column identified by name.
    /// </summary>
    public ReadOnlySpan<DataValue> GetColumn(string name)
    {
        for (int columnIndex = 0; columnIndex < Schema.Columns.Count; columnIndex++)
        {
            if (string.Equals(Schema.Columns[columnIndex].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return _columns[columnIndex];
            }
        }

        throw new KeyNotFoundException($"Column '{name}' not found.");
    }

    /// <summary>
    /// Returns a read-only span over all values in the column at the given ordinal.
    /// </summary>
    public ReadOnlySpan<DataValue> GetColumn(int ordinal)
    {
        return _columns[ordinal];
    }

    /// <summary>
    /// Returns a new batch containing a contiguous sub-range of rows.
    /// </summary>
    public RowBatch Slice(int offset, int count)
    {
        DataValue[][] slicedColumns = new DataValue[_columns.Length][];

        for (int columnIndex = 0; columnIndex < _columns.Length; columnIndex++)
        {
            slicedColumns[columnIndex] = _columns[columnIndex]
                .AsSpan(offset, count)
                .ToArray();
        }

        return new RowBatch(Schema, slicedColumns);
    }
}
