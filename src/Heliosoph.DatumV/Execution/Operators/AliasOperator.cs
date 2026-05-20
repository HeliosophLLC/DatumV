using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Prefixes all column names in the incoming rows with a table alias,
/// enabling qualified column references (e.g. <c>t.column_name</c>).
/// Retains the original unqualified names as well for unqualified access.
/// </summary>
public sealed class AliasOperator : QueryOperator
{
    private readonly QueryOperator _source;
    private readonly string _alias;

    /// <summary>
    /// Creates an alias operator.
    /// </summary>
    /// <param name="source">The child operator producing rows.</param>
    /// <param name="alias">The table alias to prefix column names with.</param>
    public AliasOperator(QueryOperator source, string alias)
    {
        _source = source;
        _alias = alias;
    }

    /// <summary>The child operator.</summary>
    public QueryOperator Source => _source;

    /// <summary>The table alias.</summary>
    public string Alias => _alias;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Alias")
        {
            Properties = new Dictionary<string, string>
            {
                ["alias"] = _alias,
            },
            Children = [(Source, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        AliasSchema? schema = null;
        ColumnLookup? columnLookup = null;
        RowCopyOutputWriter writer = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _source.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    schema ??= AliasSchema.Build(_alias, inputBatch[i]);
                    columnLookup ??= schema.GetColumnLookup();

                    RowBatch? full = writer.Add(columnLookup, inputBatch, i);
                    if (full is not null) yield return full;
                }

                context.ReturnRowBatch(inputBatch);
            }

            RowBatch? trailing = writer.Flush();
            if (trailing is not null) yield return trailing;
        }
        finally
        {
            RowBatch? leftover = writer.Flush();
            if (leftover is not null) context.ReturnRowBatch(leftover);
        }
    }

    /// <summary>
    /// Pre-computed doubled column schema for alias expansion. Built once from
    /// the first source row and reused for all subsequent rows.
    /// </summary>
    private sealed class AliasSchema
    {
        private readonly string[] _names;
        private readonly Dictionary<string, int> _nameIndex;

        private AliasSchema(
            string[] names,
            Dictionary<string, int> nameIndex)
        {
            _names = names;
            _nameIndex = nameIndex;
        }

        public ColumnLookup GetColumnLookup()
        {
            return new ColumnLookup(_names, _nameIndex);
        }

        /// <summary>
        /// Builds the alias schema from the alias prefix and the first source row.
        /// The physical column names use the qualified form (<c>alias.column</c>).
        /// The unqualified original names are added to the lookup index so that
        /// expressions can resolve either form, without doubling the column count.
        /// </summary>
        internal static AliasSchema Build(string alias, Row firstRow)
        {
            int fieldCount = firstRow.FieldCount;
            string[] names = new string[fieldCount];

            for (int index = 0; index < fieldCount; index++)
            {
                names[index] = $"{alias}.{firstRow.ColumnNames[index]}";
            }

            // Map both qualified and unqualified names to the same slot.
            Dictionary<string, int> nameIndex =
                new(fieldCount * 2, StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < fieldCount; index++)
            {
                nameIndex[names[index]] = index;
                nameIndex[firstRow.ColumnNames[index]] = index;
            }

            return new AliasSchema(names, nameIndex);
        }
    }
}
