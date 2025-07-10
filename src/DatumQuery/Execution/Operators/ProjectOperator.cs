using DatumQuery.Model;
using DatumQuery.Parsing.Ast;

namespace DatumQuery.Execution.Operators;

/// <summary>
/// Evaluates SELECT column expressions against each incoming row,
/// producing output rows with the projected column set.
/// Expression results are wrapped in <see cref="LazyDataValue"/> thunks
/// so computation chains through nested SELECTs without eagerly materializing.
/// </summary>
public sealed class ProjectOperator : IQueryOperator
{
    private readonly IQueryOperator _source;
    private readonly IReadOnlyList<SelectColumn> _columns;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Creates a project operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="columns">The SELECT columns to project.</param>
    /// <param name="sourceSchema">Optional source schema for star expansion.</param>
    public ProjectOperator(
        IQueryOperator source,
        IReadOnlyList<SelectColumn> columns,
        Schema? sourceSchema = null)
    {
        _source = source;
        _columns = columns;
        _sourceSchema = sourceSchema;
    }

    /// <summary>The child operator producing rows.</summary>
    public IQueryOperator Source => _source;

    /// <summary>The projected SELECT columns.</summary>
    public IReadOnlyList<SelectColumn> Columns => _columns;

    /// <inheritdoc/>
    public async IAsyncEnumerable<Row> ExecuteAsync(ExecutionContext context)
    {
        ExpressionEvaluator evaluator = new(context.FunctionRegistry);
        ProjectionSchema? schema = null;

        await foreach (Row row in _source.ExecuteAsync(context).ConfigureAwait(false))
        {
            schema ??= ProjectionSchema.Build(_columns, row);
            yield return schema.Project(row, evaluator);
        }
    }

    private static string ResolveColumnName(SelectColumn column)
    {
        if (column.Alias is not null)
        {
            return column.Alias;
        }

        return column.Expression switch
        {
            ColumnReference colRef => colRef.ColumnName,
            FunctionCallExpression funcCall => funcCall.FunctionName,
            _ => "expr",
        };
    }

    /// <summary>
    /// Pre-computed projection layout built once from the first source row.
    /// Holds the shared output column names, name-index dictionary, and a plan
    /// describing how to populate each output slot (copy from source ordinal or
    /// evaluate an expression). Subsequent rows allocate only a <see cref="DataValue"/> array.
    /// </summary>
    private sealed class ProjectionSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;
        private readonly ProjectionSlot[] _slots;

        private ProjectionSchema(
            string[] names,
            Dictionary<string, int> nameIndex,
            ProjectionSlot[] slots)
        {
            _names = names;
            _nameIndex = nameIndex;
            _slots = slots;
        }

        /// <summary>
        /// Builds the projection schema from the column list and the first source row.
        /// Star and table-star columns are expanded using the source row's schema;
        /// named expressions record their expression for per-row evaluation.
        /// </summary>
        internal static ProjectionSchema Build(
            IReadOnlyList<SelectColumn> columns, Row firstRow)
        {
            List<string> names = new();
            List<ProjectionSlot> slots = new();

            foreach (SelectColumn column in columns)
            {
                switch (column)
                {
                    case SelectAllColumns:
                        for (int index = 0; index < firstRow.FieldCount; index++)
                        {
                            names.Add(firstRow.ColumnNames[index]);
                            slots.Add(ProjectionSlot.CopyOrdinal(index));
                        }
                        break;

                    case SelectTableColumns tableColumns:
                        string prefix = tableColumns.TableName + ".";
                        for (int index = 0; index < firstRow.FieldCount; index++)
                        {
                            string columnName = firstRow.ColumnNames[index];
                            if (columnName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                || columnName.Equals(
                                    tableColumns.TableName, StringComparison.OrdinalIgnoreCase))
                            {
                                names.Add(columnName);
                                slots.Add(ProjectionSlot.CopyOrdinal(index));
                            }
                        }
                        break;

                    default:
                        names.Add(ResolveColumnName(column));
                        slots.Add(ProjectionSlot.Evaluate(column.Expression));
                        break;
                }
            }

            string[] nameArray = names.ToArray();
            Dictionary<string, int> nameIndex =
                new(nameArray.Length, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < nameArray.Length; index++)
            {
                nameIndex[nameArray[index]] = index;
            }

            return new ProjectionSchema(nameArray, nameIndex, slots.ToArray());
        }

        /// <summary>
        /// Projects a source row using the pre-computed schema. Only a
        /// <see cref="DataValue"/> array is allocated per call.
        /// </summary>
        internal Row Project(Row sourceRow, ExpressionEvaluator evaluator)
        {
            DataValue[] values = new DataValue[_slots.Length];

            for (int index = 0; index < _slots.Length; index++)
            {
                ProjectionSlot slot = _slots[index];
                values[index] = slot.SourceOrdinal >= 0
                    ? sourceRow[slot.SourceOrdinal]
                    : evaluator.Evaluate(slot.Expression!, sourceRow);
            }

            return new Row(_names, values, _nameIndex);
        }
    }

    /// <summary>
    /// Describes how to populate a single output column: either copy from a
    /// source ordinal or evaluate an expression.
    /// </summary>
    private readonly struct ProjectionSlot
    {
        /// <summary>
        /// Source ordinal to copy from, or -1 if <see cref="Expression"/> should be evaluated.
        /// </summary>
        internal int SourceOrdinal { get; private init; }

        /// <summary>
        /// Expression to evaluate, or null when copying by ordinal.
        /// </summary>
        internal Expression? Expression { get; private init; }

        internal static ProjectionSlot CopyOrdinal(int ordinal) =>
            new() { SourceOrdinal = ordinal };

        internal static ProjectionSlot Evaluate(Expression expression) =>
            new() { SourceOrdinal = -1, Expression = expression };
    }
}
