using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for <c>CALL proc.name(args)</c> when the
/// target resolves to a registered procedure (not a function). Opens a
/// fresh <see cref="Execution.ExecutionContext"/> for the procedure body,
/// declares each parameter from the corresponding argument value
/// (evaluated in the caller's scope, lifted across the boundary), and
/// drives the body's statements through the same dispatch as a top-level
/// batch. Caps nested call depth so direct + mutual recursion fail fast
/// with a clear message instead of running until the call stack
/// overflows.
/// </summary>
internal sealed class ProcedureCallPlan : StatementPlan
{
    /// <summary>
    /// Maximum nested procedure-call depth before the executor refuses to
    /// open a new frame. Recursive procedures (direct or mutual) are not
    /// supported; this cap prevents the call stack from overflowing and
    /// gives the user a clear error instead. Matches the T-SQL convention
    /// of 32 levels of nested stored-procedure calls.
    /// </summary>
    public const int MaxProcedureCallDepth = 32;

    private readonly ProcedureDescriptor _descriptor;
    private readonly IReadOnlyList<Expression> _arguments;

    public ProcedureCallPlan(
        TableCatalog catalog,
        ProcedureDescriptor descriptor,
        IReadOnlyList<Expression> arguments)
        : base(catalog)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(arguments);
        _descriptor = descriptor;
        _arguments = arguments;

        ExplainTree = new ExplainPlanNode
        {
            OperatorName = "ProcedureCall",
            Details = $"{descriptor.QualifiedName}({arguments.Count} arg(s))",
            EstimatedRows = 0,
        };
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    public override string Kind => "call";
    public override bool IsProductive => false;

    /// <summary>
    /// Resolves <paramref name="call"/> against the catalog's procedure
    /// registry. Returns <see langword="true"/> + a ready-to-execute
    /// <see cref="ProcedureCallPlan"/> when the call targets a procedure;
    /// <see langword="false"/> when it targets a function (or the name is
    /// unknown — that surfaces as a clearer error at function-eval time
    /// from the fallback SELECT path).
    /// </summary>
    public static bool TryPlan(
        TableCatalog catalog,
        CallStatement call,
        out ProcedureCallPlan? plan)
    {
        if (call.Call is FunctionCallExpression funcCall
            && catalog.Procedures.TryResolve(
                funcCall.SchemaName, funcCall.FunctionName, catalog.SearchPath,
                out ProcedureDescriptor? descriptor))
        {
            plan = new ProcedureCallPlan(catalog, descriptor, funcCall.Arguments);
            return true;
        }
        plan = null;
        return false;
    }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext callerContext)
    {
        QualifiedName qn = _descriptor.QualifiedName;

        // Trailing arguments may be omitted when matching parameters carry
        // defaults; fill them in below from each parameter's Default
        // expression.
        int minRequired = MinArity(_descriptor.Parameters);
        if (_arguments.Count < minRequired || _arguments.Count > _descriptor.Parameters.Count)
        {
            throw new ExecutionException(
                minRequired == _descriptor.Parameters.Count
                    ? $"Procedure '{qn}' expects {_descriptor.Parameters.Count} argument(s), got {_arguments.Count}."
                    : $"Procedure '{qn}' expects {minRequired}–{_descriptor.Parameters.Count} argument(s), got {_arguments.Count}.");
        }

        // Cap nested-call depth before opening the new frame. Catches direct
        // recursion (proc A calls itself) and mutual recursion (A calls B
        // calls A) the same way — both produce ever-deeper contexts.
        int newDepth = callerContext.ProcedureCallDepth + 1;
        if (newDepth > MaxProcedureCallDepth)
        {
            throw new ExecutionException(
                $"Procedure call depth exceeded {MaxProcedureCallDepth} levels at " +
                $"'{qn}'. Procedural recursion is not supported — " +
                "rewrite as a recursive CTE or an iterative loop.");
        }

        // Evaluate each argument in the caller's scope so the args can
        // reference the caller's @vars. Capture the value as a managed
        // CLR object so it survives the boundary; we'll re-pack into the
        // procedure's variable store on the other side. When the caller
        // omits a trailing argument, fall back to the parameter's Default
        // expression — also evaluated in the caller's scope so defaults
        // can reference earlier @vars consistently.
        DataValue[] argValues = new DataValue[_descriptor.Parameters.Count];
        for (int i = 0; i < _descriptor.Parameters.Count; i++)
        {
            UdfParameter param = _descriptor.Parameters[i];
            Expression argExpr = i < _arguments.Count
                ? _arguments[i]
                : param.Default!;  // arity check above guarantees a default is present
            DataValue v = await ProceduralEvaluator
                .EvaluateScalarAsync(argExpr, callerContext, cancellationToken)
                .ConfigureAwait(false);

            if (param.IsNotNull && v.IsNull)
            {
                throw new ExecutionException(
                    $"Procedure '{qn}' parameter '@{param.Name}' must not be null.");
            }

            argValues[i] = v;
        }

        // Fresh ExecutionContext for the procedure body: own VariableScope
        // + VariableStore so the body's bindings stay isolated; inherits
        // the streaming surface (CellSink + cell-id allocator) so
        // productive descendants stream through the same wire. Bumps the
        // call depth so any nested CALL the body issues sees the running
        // total.
        using Execution.ExecutionContext procContext =
            callerContext.CreateProcedureChild(newDepth);
        for (int i = 0; i < _descriptor.Parameters.Count; i++)
        {
            // Stabilise from the caller's variable store into the
            // procedure's. EvaluateScalarAsync stabilised the value in
            // callerContext.VariableStore; re-stabilise into
            // procContext.VariableStore so the procedure body's reads
            // resolve against the right arena.
            procContext.Declare(
                _descriptor.Parameters[i].Name,
                argValues[i],
                callerContext.VariableStore);
        }

        // Run the body's statements through the catalog's PlanAsync
        // dispatch. Lazy planning per statement — eagerly planning the
        // body would infinite-loop through any recursive CALL inside it.
        // BREAK / CONTINUE that escape the procedure body convert to
        // user errors at the call boundary.
        IAsyncEnumerator<RowBatch>? bodyEnumerator = null;
        int bodyIndex = 0;
        while (true)
        {
            if (bodyEnumerator is null)
            {
                if (bodyIndex >= _descriptor.Body.Statements.Count) break;
                cancellationToken.ThrowIfCancellationRequested();
                Statement bodyStatement = _descriptor.Body.Statements[bodyIndex++];
                StatementPlan bodyPlan;
                try
                {
                    bodyPlan = await Catalog
                        .PlanAsync(bodyStatement, sourceText: null)
                        .ConfigureAwait(false);
                }
                catch (LoopBreakSignal)
                {
                    throw new ExecutionException(
                        $"BREAK in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
                }
                catch (LoopContinueSignal)
                {
                    throw new ExecutionException(
                        $"CONTINUE in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
                }
                bodyEnumerator = bodyPlan.ExecuteAsync(cancellationToken, procContext)
                    .GetAsyncEnumerator(cancellationToken);
            }

            bool moved;
            try
            {
                moved = await bodyEnumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (LoopBreakSignal)
            {
                await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
                throw new ExecutionException(
                    $"BREAK in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
            }
            catch (LoopContinueSignal)
            {
                await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
                throw new ExecutionException(
                    $"CONTINUE in procedure '{qn}' is only valid inside a WHILE or FOR loop.");
            }
            if (!moved)
            {
                await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
                bodyEnumerator = null;
                continue;
            }
            yield return bodyEnumerator.Current;
        }
    }

    private static int MinArity(IReadOnlyList<UdfParameter> parameters)
    {
        int min = 0;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null) break;
            min++;
        }
        return min;
    }
}
