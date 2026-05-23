using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for procedural <c>ASSERT predicate
/// [, message]</c>. Evaluates the predicate (NULL → false); throws when
/// false. The optional message expression formats the exception text; when
/// omitted, the failing predicate is shown verbatim.
/// </summary>
internal sealed class AssertPlan : StatementPlan
{
    private readonly AssertStatement _statement;

    public AssertPlan(TableCatalog catalog, AssertStatement statement)
        : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        _statement = statement;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = "Assert",
            Details = statement.Message is null ? "<predicate>" : "<predicate>, <message>",
            EstimatedRows = 0,
        };
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    public override string Kind => "assert";
    public override bool IsProductive => false;

    /// <inheritdoc />
#pragma warning disable CS1998 // ASSERT yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool holds = await ProceduralEvaluator
            .EvaluatePredicateAsync(_statement.Predicate, context, cancellationToken)
            .ConfigureAwait(false);
        if (holds) yield break;

        string message;
        if (_statement.Message is not null)
        {
            DataValue m = await ProceduralEvaluator
                .EvaluateScalarAsync(_statement.Message, context, cancellationToken)
                .ConfigureAwait(false);
            message = ProceduralEvaluator.RenderForPrint(m, context.VariableStore)
                ?? "Assertion failed.";
        }
        else
        {
            message = $"Assertion failed: {QueryExplainer.FormatExpression(_statement.Predicate)}";
        }
        throw new AssertionAbortException(message, _statement.Span);
    }
#pragma warning restore CS1998
}
