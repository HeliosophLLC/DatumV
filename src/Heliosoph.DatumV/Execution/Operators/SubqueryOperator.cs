using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution.Operators;

/// <summary>
/// Wraps an inner <see cref="SelectStatement"/>'s operator tree, applying
/// the subquery alias to produce a derived table result.
/// </summary>
public sealed class SubqueryOperator : QueryOperator
{
    private readonly QueryOperator _innerOperator;
    private readonly string _alias;

    /// <summary>
    /// Creates a subquery operator that wraps the inner operator tree.
    /// </summary>
    /// <param name="innerOperator">The operator tree for the inner SELECT.</param>
    /// <param name="alias">The alias for this derived table.</param>
    public SubqueryOperator(QueryOperator innerOperator, string alias) : base(false)
    {
        _innerOperator = innerOperator;
        _alias = alias;
    }

    /// <summary>The inner operator tree.</summary>
    public QueryOperator InnerOperator => _innerOperator;

    /// <summary>The derived table alias.</summary>
    public string Alias => _alias;

    /// <inheritdoc/>
    protected override OperatorPlanDescription DescribeForExplainImpl()
    {
        return new OperatorPlanDescription("Subquery")
        {
            Properties = new Dictionary<string, string>
            {
                ["alias"] = _alias,
            },
            Children = [(InnerOperator, null)],
        };
    }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<RowBatch> ExecuteAsyncImpl(ExecutionContext context)
    {
        ColumnLookup? columnLookup = null;
        RowCopyOutputWriter writer = new(context);

        try
        {
            await foreach (RowBatch inputBatch in _innerOperator.ExecuteAsync(context).ConfigureAwait(false))
            {
                for (int i = 0; i < inputBatch.Count; i++)
                {
                    columnLookup ??= inputBatch.ColumnLookup;

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
}
