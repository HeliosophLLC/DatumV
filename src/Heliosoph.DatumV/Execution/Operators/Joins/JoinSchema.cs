using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Execution.Operators.Joins;

/// <summary>
/// Pre-computed schema for combined rows in a join. Holds the shared column
/// name array and name-index dictionary so that each combined row allocates
/// only a <see cref="DataValue"/> array instead of rebuilding the full schema.
/// </summary>
/// <remarks>
/// Shared across <c>JoinOperator</c>, <c>MergeJoinOperator</c>,
/// <c>LateralJoinOperator</c>, <c>GraceHashJoinExecutor</c>, and
/// <c>IndexNestedLoopJoinExecutor</c> — every join path builds output rows
/// through this type.
/// </remarks>
internal sealed class JoinSchema
{
    private readonly string[] _names;
    private readonly Dictionary<string, int> _nameIndex;
    private readonly ColumnLookup _columnLookup;
    private readonly int _leftFieldCount;

    private JoinSchema(
        string[] names, Dictionary<string, int> nameIndex, int leftFieldCount)
    {
        _names = names;
        _nameIndex = nameIndex;
        _columnLookup = new ColumnLookup(names, nameIndex);
        _leftFieldCount = leftFieldCount;
    }

    /// <summary>
    /// The combined column lookup vended to <see cref="ExecutionContext.RentRowBatch(ColumnLookup)"/>
    /// when the join sets up its output batch. Wraps <see cref="_names"/> + <see cref="_nameIndex"/>
    /// so every output row constructed via <see cref="Combine"/> / <see cref="CombinePooled"/>
    /// shares the same schema reference.
    /// </summary>
    internal ColumnLookup ColumnLookup => _columnLookup;

    /// <summary>The total combined column count.</summary>
    internal int FieldCount => _names.Length;

    /// <summary>
    /// Builds a schema from the first left and right rows encountered in a join.
    /// </summary>
    internal static JoinSchema Build(Row left, Row right)
    {
        int totalFields = left.FieldCount + right.FieldCount;
        string[] names = new string[totalFields];

        for (int index = 0; index < left.FieldCount; index++)
        {
            names[index] = left.ColumnNames[index];
        }

        for (int index = 0; index < right.FieldCount; index++)
        {
            names[left.FieldCount + index] = right.ColumnNames[index];
        }

        Dictionary<string, int> nameIndex = new(totalFields, StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < totalFields; index++)
        {
            nameIndex[names[index]] = index;
        }

        // Add unqualified shortcuts for aliased columns so that expressions
        // like image_to_tensor_chw(image) can resolve unqualified names after
        // a JOIN.  Skip ambiguous names that appear on both sides.
        HashSet<string> ambiguous = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> unqualified = new(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < totalFields; index++)
        {
            int dotPosition = names[index].LastIndexOf('.');
            if (dotPosition < 0)
            {
                continue;
            }

            string shortName = names[index][(dotPosition + 1)..];
            if (ambiguous.Contains(shortName))
            {
                continue;
            }

            if (!unqualified.TryAdd(shortName, index))
            {
                // Same unqualified name on both sides — remove and mark ambiguous.
                unqualified.Remove(shortName);
                ambiguous.Add(shortName);
            }
        }

        foreach (KeyValuePair<string, int> entry in unqualified)
        {
            nameIndex.TryAdd(entry.Key, entry.Value);
        }

        return new JoinSchema(names, nameIndex, left.FieldCount);
    }

    /// <summary>
    /// Combines two rows using the shared schema. Only a <see cref="DataValue"/> array
    /// is allocated per call.
    /// </summary>
    internal Row Combine(Row left, Row right)
    {
        DataValue[] values = new DataValue[_names.Length];

        for (int index = 0; index < _leftFieldCount; index++)
        {
            values[index] = left[index];
        }

        for (int index = 0; index < _names.Length - _leftFieldCount; index++)
        {
            values[_leftFieldCount + index] = right[index];
        }

        return new Row(_columnLookup, values);
    }

    /// <summary>
    /// Fills the target array with combined values from left and right rows.
    /// No heap allocation occurs; the caller provides the buffer.
    /// </summary>
    internal void CombineInto(Row left, Row right, DataValue[] target)
    {
        for (int index = 0; index < _leftFieldCount; index++)
        {
            target[index] = left[index];
        }

        for (int index = 0; index < _names.Length - _leftFieldCount; index++)
        {
            target[_leftFieldCount + index] = right[index];
        }
    }

    /// <summary>
    /// Combines two rows, renting the backing <see cref="DataValue"/> array from
    /// <paramref name="pool"/> to avoid per-row heap allocation. The downstream
    /// consumer returns the array via <see cref="Pool.ReturnDataValues"/> when it
    /// is no longer needed.
    /// </summary>
    internal Row CombinePooled(Row left, Row right, Pool pool)
        => new(_columnLookup, CombinePooledValues(left, right, pool));

    /// <summary>
    /// Same as <see cref="CombinePooled"/> but returns the underlying
    /// <see cref="DataValue"/>[] directly so the caller can hand it to
    /// <see cref="RowBatch.Add(DataValue[])"/> without paying for a Row
    /// struct that the batch would discard anyway. The returned array's
    /// lifecycle matches the one CombinePooled uses — the downstream
    /// consumer (typically the batch itself) returns it to the pool.
    /// </summary>
    internal DataValue[] CombinePooledValues(Row left, Row right, Pool pool)
    {
        DataValue[] values = pool.RentDataValues(_names.Length);

        for (int index = 0; index < _leftFieldCount; index++)
        {
            values[index] = left[index];
        }

        for (int index = 0; index < _names.Length - _leftFieldCount; index++)
        {
            values[_leftFieldCount + index] = right[index];
        }

        return values;
    }

    /// <summary>
    /// Creates a reusable row-plus-buffer pair for scenarios where the same
    /// row is filled repeatedly (e.g. residual filter evaluation). The caller
    /// keeps the buffer reference and calls <see cref="CombineInto"/> to
    /// overwrite its contents before each use.
    /// </summary>
    internal (Row Row, DataValue[] Buffer) CreateReusableRow()
    {
        DataValue[] buffer = new DataValue[_names.Length];
        Row row = new(_columnLookup, buffer);
        return (row, buffer);
    }
}
