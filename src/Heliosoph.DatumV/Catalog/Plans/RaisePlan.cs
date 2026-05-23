using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for <c>RAISE expression</c>. Evaluates the
/// message expression, renders it to a string, and throws — letting an
/// enclosing <c>TRY</c> catch it.
/// </summary>
internal sealed class RaisePlan : StatementPlan
{
    private readonly RaiseStatement _statement;

    public RaisePlan(TableCatalog catalog, RaiseStatement statement)
        : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        _statement = statement;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = "Raise",
            Details = "<expr>",
            EstimatedRows = 0,
        };
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    public override string Kind => "raise";
    public override bool IsProductive => false;

    /// <inheritdoc />
#pragma warning disable CS1998 // RAISE always throws; the iterator yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataValue messageValue = await ProceduralEvaluator
            .EvaluateScalarAsync(_statement.Message, context, cancellationToken)
            .ConfigureAwait(false);
        string message = ProceduralEvaluator.RenderForPrint(messageValue, context.VariableStore)
            ?? "RAISE: <null>";
        throw new ExecutionException(message);
#pragma warning disable CS0162 // Unreachable — needed so the compiler sees a yield.
        yield break;
#pragma warning restore CS0162
    }
#pragma warning restore CS1998
}
