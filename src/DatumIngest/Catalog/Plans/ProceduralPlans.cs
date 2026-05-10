using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for procedural leaf statements that
/// differ only in label: <c>DECLARE</c> / <c>SET</c> / <c>PRINT</c> /
/// <c>BREAK</c> / <c>CONTINUE</c>. Each evaluates one expression (or
/// none) and applies one side effect (variable bind, mutation, print
/// event, or loop-control signal). No child plans.
/// <see cref="ExplainPlanNode.OperatorName"/> carries the discriminator.
/// </summary>
internal sealed class ProceduralLeafPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly Statement _statement;

    public ProceduralLeafPlan(TableCatalog catalog, Statement statement, string operatorName, string details)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        _catalog = catalog;
        _statement = statement;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = operatorName,
            Details = details,
            EstimatedRows = 0,
        };
    }

    /// <summary>Builds a plan for <c>DECLARE @name TypeName [= initializer]</c>.</summary>
    public static ProceduralLeafPlan ForDeclare(TableCatalog catalog, DeclareStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        string details = statement.TypeName is not null
            ? $"@{statement.VariableName} {statement.TypeName}"
            : $"@{statement.VariableName}";
        if (statement.Initializer is not null) details += " = <expr>";
        return new ProceduralLeafPlan(catalog, statement, "Declare", details);
    }

    /// <summary>Builds a plan for <c>SET @name = expression</c>.</summary>
    public static ProceduralLeafPlan ForSet(TableCatalog catalog, SetStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ProceduralLeafPlan(catalog, statement, "Set", $"@{statement.VariableName} = <expr>");
    }

    /// <summary>Builds a plan for <c>PRINT expression</c>.</summary>
    public static ProceduralLeafPlan ForPrint(TableCatalog catalog, PrintStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ProceduralLeafPlan(catalog, statement, "Print", "<expr>");
    }

    /// <summary>Builds a plan for <c>BREAK</c> — throws <see cref="LoopBreakSignal"/> on execute.</summary>
    public static ProceduralLeafPlan ForBreak(TableCatalog catalog, BreakStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ProceduralLeafPlan(catalog, statement, "Break", "exits innermost loop");
    }

    /// <summary>Builds a plan for <c>CONTINUE</c> — throws <see cref="LoopContinueSignal"/> on execute.</summary>
    public static ProceduralLeafPlan ForContinue(TableCatalog catalog, ContinueStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new ProceduralLeafPlan(catalog, statement, "Continue", "skips to next iteration");
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' operators on some paths — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        cancellationToken.ThrowIfCancellationRequested();

        switch (_statement)
        {
            case DeclareStatement decl:
                await ExecuteDeclareAsync(decl, batchContext, cancellationToken).ConfigureAwait(false);
                break;
            case SetStatement set:
                await ExecuteSetAsync(set, batchContext, cancellationToken).ConfigureAwait(false);
                break;
            case PrintStatement print:
                await ExecutePrintAsync(print, batchContext, cancellationToken).ConfigureAwait(false);
                break;
            case BreakStatement:
                throw LoopBreakSignal.Instance;
            case ContinueStatement:
                throw LoopContinueSignal.Instance;
            default:
                throw new InvalidOperationException(
                    $"ProceduralLeafPlan: unrecognised statement type {_statement.GetType().Name}.");
        }
        yield break;
    }
#pragma warning restore CS1998

    private static async Task ExecuteDeclareAsync(
        DeclareStatement decl, BatchContext batchContext, CancellationToken ct)
    {
        ValueRef bound;
        if (decl.Initializer is not null)
        {
            // When both a declared type and an initializer are supplied
            // (`DECLARE @sum INT64 = 0`), wrap the initializer in an
            // implicit CAST so the declared type wins over the literal's
            // narrow natural type.
            Expression effective = decl.TypeName is not null
                ? new CastExpression(decl.Initializer, decl.TypeName, decl.Span)
                : decl.Initializer;
            DataValue stable = await ProceduralEvaluator.EvaluateScalarAsync(effective, batchContext, ct).ConfigureAwait(false);
            bound = ProceduralEvaluator.LiftBoundaryValue(stable, batchContext);
        }
        else
        {
            // No initializer → bind a typed NULL. The parser requires
            // TypeName when there's no initializer, so non-null here.
            if (decl.TypeName is null
                || !TypeAnnotationResolver.TryParse(decl.TypeName, out DataKind kind, out bool isArray))
            {
                throw new InvalidOperationException(
                    $"DECLARE @{decl.VariableName}: cannot resolve type name '{decl.TypeName}'. " +
                    "Use a recognised SQL type (Int32, Float64, String, Boolean, Array<String>, etc.) or supply an initializer.");
            }
            bound = isArray ? ValueRef.NullArray(kind) : ValueRef.Null(kind);
        }
        batchContext.VariableScope.Declare(decl.VariableName, bound);
    }

    private static async Task ExecuteSetAsync(
        SetStatement set, BatchContext batchContext, CancellationToken ct)
    {
        DataValue stable = await ProceduralEvaluator.EvaluateScalarAsync(set.Value, batchContext, ct).ConfigureAwait(false);
        batchContext.VariableScope.Set(set.VariableName, ProceduralEvaluator.LiftBoundaryValue(stable, batchContext));
    }

    private static async Task ExecutePrintAsync(
        PrintStatement print, BatchContext batchContext, CancellationToken ct)
    {
        DataValue value = await ProceduralEvaluator.EvaluateScalarAsync(print.Value, batchContext, ct).ConfigureAwait(false);
        string? text = ProceduralEvaluator.RenderForPrint(value, batchContext.VariableStore);
        // No event channel on the plan contract — emission goes through
        // BatchContext.PrintSink. BatchExecutor's AST-walk emits
        // CellPrintBatchEvent directly via onEvent; standalone plan
        // callers that want PRINT visibility set PrintSink before
        // iteration. Null sink silently drops.
        batchContext.PrintSink?.Invoke(text);
    }
}

/// <summary>
/// <see cref="StatementPlan"/> for <c>BEGIN … END</c>. Pushes a fresh
/// <see cref="VariableScope"/> frame, iterates child plans in source
/// order forwarding any yielded batches, then pops the frame
/// regardless of how the iteration ended.
/// </summary>
internal sealed class BlockPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly BlockStatement _statement;
    private readonly IReadOnlyList<StatementPlan> _children;

    public BlockPlan(TableCatalog catalog, BlockStatement statement, IReadOnlyList<StatementPlan> children)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(children);
        _catalog = catalog;
        _statement = statement;
        _children = children;

        ExplainPlanNode tree = new()
        {
            OperatorName = "Block",
            Details = $"{children.Count} statement(s)",
            EstimatedRows = 0,
        };
        foreach (StatementPlan child in children)
        {
            tree.Children.Add(child.ExplainTree);
        }
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        _ = _statement;
        cancellationToken.ThrowIfCancellationRequested();
        batchContext.VariableScope.PushFrame();
        try
        {
            foreach (StatementPlan child in _children)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await foreach (RowBatch batch in child
                    .ExecuteAsync(cancellationToken, batchContext)
                    .ConfigureAwait(false))
                {
                    yield return batch;
                }
            }
        }
        finally
        {
            batchContext.VariableScope.PopFrame();
        }
    }
}

/// <summary>
/// <see cref="StatementPlan"/> for <c>IF predicate then-stmt [ELSE else-stmt]</c>.
/// Evaluates the predicate and iterates the appropriate branch plan,
/// forwarding its yielded batches. NULL predicate is treated as false
/// (T-SQL 3VL).
/// </summary>
internal sealed class IfPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly IfStatement _statement;
    private readonly StatementPlan _thenPlan;
    private readonly StatementPlan? _elsePlan;

    public IfPlan(TableCatalog catalog, IfStatement statement, StatementPlan thenPlan, StatementPlan? elsePlan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(thenPlan);
        _catalog = catalog;
        _statement = statement;
        _thenPlan = thenPlan;
        _elsePlan = elsePlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "If",
            Details = elsePlan is null ? "no else branch" : "with else branch",
            EstimatedRows = 0,
        };
        ExplainPlanNode thenNode = thenPlan.ExplainTree;
        thenNode.ChildLabel = "then";
        tree.Children.Add(thenNode);
        if (elsePlan is not null)
        {
            ExplainPlanNode elseNode = elsePlan.ExplainTree;
            elseNode.ChildLabel = "else";
            tree.Children.Add(elseNode);
        }
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool predicate = await ProceduralEvaluator
            .EvaluatePredicateAsync(_statement.Predicate, batchContext, cancellationToken)
            .ConfigureAwait(false);
        StatementPlan? branch = predicate ? _thenPlan : _elsePlan;
        if (branch is null) yield break;
        await foreach (RowBatch batch in branch
            .ExecuteAsync(cancellationToken, batchContext)
            .ConfigureAwait(false))
        {
            yield return batch;
        }
    }
}

/// <summary>
/// <see cref="StatementPlan"/> for <c>WHILE predicate body</c>.
/// Re-evaluates the predicate each iteration; runs the body plan inside
/// a try-around-MoveNextAsync so <c>BREAK</c> /<c>CONTINUE</c> signals
/// thrown from nested procedural leaves unwind cleanly.
/// </summary>
internal sealed class WhilePlan : StatementPlan
{
    /// <summary>Cap on iterations to guarantee termination of malformed loops.</summary>
    private const int IterationCap = 1_000_000;

    private readonly TableCatalog _catalog;
    private readonly WhileStatement _statement;
    private readonly StatementPlan _bodyPlan;

    public WhilePlan(TableCatalog catalog, WhileStatement statement, StatementPlan bodyPlan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(bodyPlan);
        _catalog = catalog;
        _statement = statement;
        _bodyPlan = bodyPlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "While",
            Details = "predicate re-evaluated each iteration",
            EstimatedRows = 0,
        };
        ExplainPlanNode bodyNode = bodyPlan.ExplainTree;
        bodyNode.ChildLabel = "body";
        tree.Children.Add(bodyNode);
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        int iter = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (iter++ >= IterationCap)
            {
                throw new InvalidOperationException(
                    $"WHILE loop exceeded {IterationCap} iterations — likely a missing termination condition.");
            }
            bool keepGoing = await ProceduralEvaluator
                .EvaluatePredicateAsync(_statement.Predicate, batchContext, cancellationToken)
                .ConfigureAwait(false);
            if (!keepGoing) break;

            (LoopOutcome outcome, IAsyncEnumerator<RowBatch> bodyEnumerator) = (LoopOutcome.Completed, null!);
            bodyEnumerator = _bodyPlan.ExecuteAsync(cancellationToken, batchContext).GetAsyncEnumerator(cancellationToken);
            try
            {
                while (true)
                {
                    bool moved;
                    try
                    {
                        moved = await bodyEnumerator.MoveNextAsync().ConfigureAwait(false);
                    }
                    catch (LoopContinueSignal)
                    {
                        outcome = LoopOutcome.Continued;
                        break;
                    }
                    catch (LoopBreakSignal)
                    {
                        outcome = LoopOutcome.Broke;
                        break;
                    }
                    if (!moved) break;
                    yield return bodyEnumerator.Current;
                }
            }
            finally
            {
                await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
            }

            if (outcome == LoopOutcome.Broke) yield break;
            // LoopOutcome.Continued / Completed both fall through to the next predicate eval.
        }
    }
}

/// <summary>
/// <see cref="StatementPlan"/> for counter-FOR loops:
/// <c>FOR @i = start TO end body</c>. Auto-declares the loop variable
/// in a fresh frame, increments by one each iteration (STEP not yet
/// supported here — matches the BatchExecutor reference implementation),
/// catches <c>BREAK</c> / <c>CONTINUE</c> signals from the body.
/// </summary>
internal sealed class ForCounterPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly ForCounterStatement _statement;
    private readonly StatementPlan _bodyPlan;

    public ForCounterPlan(TableCatalog catalog, ForCounterStatement statement, StatementPlan bodyPlan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(bodyPlan);
        _catalog = catalog;
        _statement = statement;
        _bodyPlan = bodyPlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "ForCounter",
            Details = $"@{statement.VariableName}" + (statement.Step is null ? "" : " STEP <expr>"),
            EstimatedRows = 0,
        };
        ExplainPlanNode bodyNode = bodyPlan.ExplainTree;
        bodyNode.ChildLabel = "body";
        tree.Children.Add(bodyNode);
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataValue startVal = await ProceduralEvaluator
            .EvaluateScalarAsync(_statement.Start, batchContext, cancellationToken)
            .ConfigureAwait(false);
        DataValue endVal = await ProceduralEvaluator
            .EvaluateScalarAsync(_statement.End, batchContext, cancellationToken)
            .ConfigureAwait(false);
        long start = ProceduralEvaluator.ToInt64(startVal, $"FOR @{_statement.VariableName} start");
        long end = ProceduralEvaluator.ToInt64(endVal, $"FOR @{_statement.VariableName} end");

        batchContext.VariableScope.PushFrame();
        try
        {
            if (start > end) yield break;
            for (long i = start; i <= end; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValueRef iValue = ValueRef.FromInt64(i);
                if (i == start)
                {
                    batchContext.VariableScope.Declare(_statement.VariableName, iValue);
                }
                else
                {
                    batchContext.VariableScope.Set(_statement.VariableName, iValue);
                }

                LoopOutcome outcome = LoopOutcome.Completed;
                IAsyncEnumerator<RowBatch> bodyEnumerator = _bodyPlan
                    .ExecuteAsync(cancellationToken, batchContext)
                    .GetAsyncEnumerator(cancellationToken);
                try
                {
                    while (true)
                    {
                        bool moved;
                        try
                        {
                            moved = await bodyEnumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (LoopContinueSignal)
                        {
                            outcome = LoopOutcome.Continued;
                            break;
                        }
                        catch (LoopBreakSignal)
                        {
                            outcome = LoopOutcome.Broke;
                            break;
                        }
                        if (!moved) break;
                        yield return bodyEnumerator.Current;
                    }
                }
                finally
                {
                    await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
                }

                if (outcome == LoopOutcome.Broke) yield break;
            }
        }
        finally
        {
            batchContext.VariableScope.PopFrame();
        }
    }
}

/// <summary>
/// <see cref="StatementPlan"/> for cursor-FOR loops:
/// <c>FOR @row IN (SELECT …) body</c>. Drives the source plan
/// batch-by-batch, binds each row to a struct-valued loop variable
/// (field names taken from the source's column lookup), and iterates
/// the body plan per row inside a fresh scope frame.
/// </summary>
internal sealed class ForInPlan : StatementPlan
{
    private readonly TableCatalog _catalog;
    private readonly ForInStatement _statement;
    private readonly StatementPlan _sourcePlan;
    private readonly StatementPlan _bodyPlan;

    public ForInPlan(
        TableCatalog catalog,
        ForInStatement statement,
        StatementPlan sourcePlan,
        StatementPlan bodyPlan)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(sourcePlan);
        ArgumentNullException.ThrowIfNull(bodyPlan);
        _catalog = catalog;
        _statement = statement;
        _sourcePlan = sourcePlan;
        _bodyPlan = bodyPlan;

        ExplainPlanNode tree = new()
        {
            OperatorName = "ForIn",
            Details = $"@{statement.VariableName} bound per source row",
            EstimatedRows = 0,
        };
        ExplainPlanNode sourceNode = sourcePlan.ExplainTree;
        sourceNode.ChildLabel = "source";
        tree.Children.Add(sourceNode);
        ExplainPlanNode bodyNode = bodyPlan.ExplainTree;
        bodyNode.ChildLabel = "body";
        tree.Children.Add(bodyNode);
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<string>? fieldNames = null;
        ushort rowTypeId = 0;
        bool broke = false;

        // Reuse one ExecutionContext across the loop — ambient state is
        // stable for the duration of the FOR-IN.
        using DatumIngest.Execution.ExecutionContext context = _catalog.CreateExecutionContext(
            store: batchContext.VariableStore,
            types: batchContext.Types,
            accountant: batchContext.Accountant,
            cancellationToken: cancellationToken);

        await foreach (RowBatch batch in _sourcePlan
            .ExecuteAsync(cancellationToken, batchContext)
            .ConfigureAwait(false))
        {
            if (broke) break;
            fieldNames ??= batch.ColumnLookup.ColumnNames;
            int colCount = fieldNames.Count;

            for (int rowIdx = 0; rowIdx < batch.Count; rowIdx++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Row row = batch[rowIdx];

                // Lift each field directly into a managed-payload ValueRef
                // against the producing batch's arena, then assemble a
                // ValueRef.FromStruct — the resulting binding has no arena
                // dependency and survives the batch recycle without
                // touching VariableStore.
                EvaluationFrame batchFrame = new(row, batch.Arena, batchContext.VariableStore, context);
                ValueRef[] fieldRefs = new ValueRef[colCount];
                for (int j = 0; j < colCount; j++)
                {
                    fieldRefs[j] = ExpressionEvaluator.ToValueRef(row[j], batchFrame);
                }
                if (rowTypeId == 0)
                {
                    StructFieldDescriptor[] fieldDescriptors = new StructFieldDescriptor[colCount];
                    for (int j = 0; j < colCount; j++)
                    {
                        int fieldTypeId = batchContext.Types.InternScalarType(fieldRefs[j].Kind);
                        fieldDescriptors[j] = new StructFieldDescriptor(fieldNames[j], fieldTypeId);
                    }
                    rowTypeId = (ushort)batchContext.Types.InternStructType(fieldDescriptors);
                }
                ValueRef rowStruct = ValueRef.FromStruct(fieldRefs, rowTypeId);

                batchContext.VariableScope.PushFrame();
                LoopOutcome outcome = LoopOutcome.Completed;
                try
                {
                    batchContext.VariableScope.Declare(_statement.VariableName, rowStruct, fieldNames);
                    IAsyncEnumerator<RowBatch> bodyEnumerator = _bodyPlan
                        .ExecuteAsync(cancellationToken, batchContext)
                        .GetAsyncEnumerator(cancellationToken);
                    try
                    {
                        while (true)
                        {
                            bool moved;
                            try
                            {
                                moved = await bodyEnumerator.MoveNextAsync().ConfigureAwait(false);
                            }
                            catch (LoopContinueSignal)
                            {
                                outcome = LoopOutcome.Continued;
                                break;
                            }
                            catch (LoopBreakSignal)
                            {
                                outcome = LoopOutcome.Broke;
                                break;
                            }
                            if (!moved) break;
                            yield return bodyEnumerator.Current;
                        }
                    }
                    finally
                    {
                        await bodyEnumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    batchContext.VariableScope.PopFrame();
                }

                if (outcome == LoopOutcome.Broke)
                {
                    broke = true;
                    break;
                }
            }
        }
    }
}

/// <summary>
/// Outcome of one loop iteration's body. Returned implicitly via the
/// caught signal type so the outer loop can decide whether to continue,
/// break, or fall through.
/// </summary>
internal enum LoopOutcome
{
    /// <summary>Body completed all iterations of its enumerator naturally.</summary>
    Completed,
    /// <summary>Body raised <see cref="LoopContinueSignal"/> — skip remainder of this iteration.</summary>
    Continued,
    /// <summary>Body raised <see cref="LoopBreakSignal"/> — terminate the loop.</summary>
    Broke,
}
