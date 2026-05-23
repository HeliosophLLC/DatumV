using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for <c>TRY try-stmt CATCH @err catch-stmt
/// [FINALLY finally-stmt]</c>. C#-style try/catch/finally semantics —
/// CATCH binds the exception message to <c>@&lt;ErrorVariableName&gt;</c>
/// in a fresh scope frame; FINALLY runs unconditionally. <c>BREAK</c> /
/// <c>CONTINUE</c> / cancellation signals pass through CATCH (FINALLY
/// still runs); a throw from FINALLY supersedes any pending exception.
/// </summary>
internal sealed class TryPlan : StatementPlan
{
    private readonly TryStatement _statement;
    private readonly StatementPlan _tryBodyPlan;
    private readonly StatementPlan _catchBodyPlan;
    private readonly StatementPlan? _finallyBodyPlan;

    public TryPlan(
        TableCatalog catalog,
        TryStatement statement,
        StatementPlan tryBodyPlan,
        StatementPlan catchBodyPlan,
        StatementPlan? finallyBodyPlan)
        : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(tryBodyPlan);
        ArgumentNullException.ThrowIfNull(catchBodyPlan);
        _statement = statement;
        _tryBodyPlan = tryBodyPlan;
        _catchBodyPlan = catchBodyPlan;
        _finallyBodyPlan = finallyBodyPlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "Try",
            Details = finallyBodyPlan is null
                ? $"CATCH @{statement.ErrorVariableName}"
                : $"CATCH @{statement.ErrorVariableName} FINALLY",
            EstimatedRows = 0,
        };
        ExplainPlanNode tryNode = tryBodyPlan.ExplainTree;
        tryNode.ChildLabel = "try";
        tree.Children.Add(tryNode);
        ExplainPlanNode catchNode = catchBodyPlan.ExplainTree;
        catchNode.ChildLabel = "catch";
        tree.Children.Add(catchNode);
        if (finallyBodyPlan is not null)
        {
            ExplainPlanNode finallyNode = finallyBodyPlan.ExplainTree;
            finallyNode.ChildLabel = "finally";
            tree.Children.Add(finallyNode);
        }
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    public override string Kind => "try";
    public override bool IsProductive => false;

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        // The try/catch/finally machinery doesn't compose with `yield return`
        // inside the iterator, so the bodies are drained — they're procedural
        // wrappers without their own row stream. Productive descendants
        // inside the bodies stream through context.CellSink directly.
        cancellationToken.ThrowIfCancellationRequested();
        await RunAsync(context, cancellationToken).ConfigureAwait(false);
        yield break;
    }

    private async Task RunAsync(Execution.ExecutionContext context, CancellationToken ct)
    {
        ExceptionDispatchInfo? pending = null;

        try
        {
            try
            {
                await DrainAsync(_tryBodyPlan, context, ct).ConfigureAwait(false);
            }
            catch (LoopBreakSignal) { throw; }
            catch (LoopContinueSignal) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Bind the exception message into a fresh frame so the catch
                // body can reference @<ErrorVariableName>; pop on exit so the
                // binding doesn't leak to the surrounding scope or to FINALLY.
                context.VariableScope.PushFrame();
                try
                {
                    ValueRef messageValue = ValueRef.FromString(ex.Message ?? string.Empty);
                    context.VariableScope.Declare(_statement.ErrorVariableName, messageValue);
                    try
                    {
                        await DrainAsync(_catchBodyPlan, context, ct).ConfigureAwait(false);
                    }
                    catch (LoopBreakSignal) { throw; }
                    catch (LoopContinueSignal) { throw; }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception catchEx)
                    {
                        // Catch body itself threw — defer to finally, then
                        // propagate. ExceptionDispatchInfo preserves the
                        // original stack trace.
                        pending = ExceptionDispatchInfo.Capture(catchEx);
                    }
                }
                finally
                {
                    context.VariableScope.PopFrame();
                }
            }
        }
        finally
        {
            // Always run the finally body, regardless of how try/catch exited.
            // A throw from finally supersedes any pending exception (matches
            // C# semantics). Pass any current cancellation through normally.
            if (_finallyBodyPlan is not null)
            {
                await DrainAsync(_finallyBodyPlan, context, ct).ConfigureAwait(false);
            }
        }

        pending?.Throw();
    }

    private static async Task DrainAsync(
        StatementPlan plan, Execution.ExecutionContext context, CancellationToken ct)
    {
        await foreach (RowBatch batch in plan
            .ExecuteAsync(ct, context)
            .ConfigureAwait(false))
        {
            // Drain — bodies of TRY/CATCH/FINALLY are procedural, no rows
            // to forward. Productive descendants self-stream via CellSink.
            _ = batch;
        }
    }
}
