using System.Diagnostics;

using DatumIngest.Catalog.Registries;
using DatumIngest.Diagnostics;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Runtime adapter that lets a SQL-defined model (a <c>CREATE MODEL …
/// BEGIN…END</c> registration) participate in the scalar-dispatch
/// pipeline. Constructed once per <see cref="ModelDescriptor"/> at
/// <c>CREATE MODEL</c> time; <see cref="ExecuteAsync"/> runs the
/// procedural body per call against a fresh <see cref="VariableScope"/>
/// bound with the call-site arguments, with the model descriptor pushed
/// onto the evaluation frame so the body's <c>infer()</c> calls can
/// resolve their session binding.
/// </summary>
/// <remarks>
/// <para>
/// Structurally mirrors <see cref="ProceduralUdfFunction"/> — same body
/// interpreter, same parameter binding, same RETURN-coercion rules. Two
/// differences:
/// <list type="bullet">
///   <item><description>
///     The body frame is built with
///     <see cref="EvaluationFrame.WithCurrentModel"/> set to this
///     descriptor, so the <c>infer()</c> scalar can pull its bound
///     <c>IInferenceSession</c> from <see cref="EvaluationFrame.CurrentModel"/>.
///   </description></item>
///   <item><description>
///     Cycle detection uses a separate <see cref="AsyncLocal{T}"/> stack
///     from <see cref="ProceduralUdfFunction"/>. A model body can call a
///     UDF and a UDF can call a model without artificially tripping
///     each other's cycle guards; cycles are only detected within each
///     class of routine.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// <strong>Purity.</strong> Always reports <see cref="IsPure"/> as
/// <see langword="false"/>. Model invocations are expensive and may have
/// subtle non-determinism (sampling, RNG seeded by clock); the planner's
/// CSE pass shouldn't merge identical call sites without explicit opt-in.
/// </para>
/// </remarks>
public sealed class ProceduralModelFunction : IScalarFunction
{
    /// <summary>
    /// Per-async-context stack of currently-executing model names. Used
    /// for cycle detection — disjoint from the UDF stack so models and
    /// UDFs can call each other freely; only self-cycles within the
    /// same routine class are flagged.
    /// </summary>
    private static readonly AsyncLocal<Stack<string>?> _invocationStack = new();

    private readonly ModelDescriptor _descriptor;
    private readonly FunctionRegistry _functions;
    private readonly DataKind _returnKind;

    /// <summary>
    /// One slot per declared parameter, in the same order as
    /// <see cref="ModelDescriptor.Parameters"/>. Entries are <see langword="null"/>
    /// when the parameter has no declared <c>CHECK</c> clause; non-null entries
    /// are the canonicalised typed check evaluated against each bound value
    /// before the body runs. Resolved once at construction time so per-row
    /// parameter binding stays a single array lookup.
    /// </summary>
    private readonly ParameterCheck?[] _parameterChecks;

    /// <summary>
    /// Cached "any parameter carries a <see cref="CustomCheck"/>" flag — the
    /// dispatch path skips the (slightly more expensive) scope-bound
    /// evaluator construction when no parameter needs it.
    /// </summary>
    private readonly bool _hasCustomChecks;

    /// <summary>
    /// Creates an adapter for <paramref name="descriptor"/>. The
    /// descriptor's <see cref="ModelDescriptor.ReturnTypeName"/> must
    /// parse to a known <see cref="DataKind"/> — the parser already
    /// enforces a non-null annotation on CREATE MODEL, so this throws
    /// only when the annotation is syntactically unparseable.
    /// </summary>
    public ProceduralModelFunction(ModelDescriptor descriptor, FunctionRegistry functions)
    {
        _descriptor = descriptor;
        _functions = functions;
        if (!TypeAnnotationResolver.TryParse(descriptor.ReturnTypeName, out _returnKind, out _))
        {
            throw new ArgumentException(
                $"Model '{descriptor.QualifiedName}': cannot resolve return type '{descriptor.ReturnTypeName}'.",
                nameof(descriptor));
        }

        _parameterChecks = new ParameterCheck?[descriptor.Parameters.Count];
        bool hasCustom = false;
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            UdfParameter p = descriptor.Parameters[i];
            _parameterChecks[i] = p.Check is null
                ? null
                : ParameterCheckWalker.Canonicalise(p.Check, p.Name);
            if (_parameterChecks[i] is CustomCheck) hasCustom = true;
        }
        _hasCustomChecks = hasCustom;
    }

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        int max = _descriptor.Parameters.Count;
        int min = MinRequiredArity(_descriptor.Parameters);
        if (argumentKinds.Length < min || argumentKinds.Length > max)
        {
            string expected = min == max
                ? $"{max} argument(s)"
                : $"{min}–{max} argument(s)";
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}' expects {expected}, got {argumentKinds.Length}.");
        }
        return _returnKind;
    }

    /// <inheritdoc/>
    public bool IsPure => false;

    /// <inheritdoc/>
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        Stack<string> stack = _invocationStack.Value ??= new Stack<string>();
        foreach (string active in stack)
        {
            if (string.Equals(active, _descriptor.Name, StringComparison.OrdinalIgnoreCase))
            {
                string chain = string.Join(" → ", stack.Reverse().Append(_descriptor.Name));
                throw new InvalidOperationException(
                    $"Cyclic model call detected: {chain}. " +
                    "Models cannot call themselves directly or transitively.");
            }
        }

        stack.Push(_descriptor.Name);
        DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: enter, depth={stack.Count}, args={arguments.Length}");
        try
        {
            // Acquire a residency lease so the model's session loads
            // through the residency manager (triggering weight-cost
            // measurement + calibration auto-trigger) regardless of
            // whether this call was hoisted into MIO. Without this,
            // nested model calls from inside another model body — e.g.
            // a delegating TextGenerator view calling its underlying
            // ChatCompleter sibling — load their GGUF via the dispatcher
            // directly, completely bypassing residency. Result: the
            // chat model's VRAM never registers in system.residency_snapshot,
            // the chip shows 0 B, and calibration never fires.
            //
            // The MIO path already calls AcquireAsync before invoking
            // the adapter's InferBatchAsync; nested ProceduralModelFunction
            // calls land here instead, so we mirror the acquisition.
            // AcquireAsync is reference-counted — re-entrant from the
            // same query holding an outer lease is safe.
            //
            // Catalog-less hosts (some test fixtures) skip the
            // acquisition entirely — the registrar's ModelCatalogEntry
            // is missing and the descriptor's BoundSessions handles
            // session loading directly.
            ModelCatalog? models = frame.Context?.Models;
            if (models is not null && models.TryGetEntry(_descriptor.Name) is not null)
            {
                using ModelLease lease = await models
                    .AcquireAsync(_descriptor.Name, cancellationToken)
                    .ConfigureAwait(false);
                return await RunBodyTracedAsync(arguments, frame, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunBodyTracedAsync(arguments, frame, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            stack.Pop();
        }
    }

    private async ValueTask<ValueRef> RunBodyTracedAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            ValueRef result = await ExecuteBodyAsync(arguments, frame, cancellationToken).ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: exit kind={result.Kind} isArray={result.IsArray}");
            return result;
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Columnar batched dispatch for a model whose body is straight-line
    /// (DECLARE / SET / RETURN only, no IF / WHILE / BLOCK — see
    /// <c>ProceduralModelAdapter.IsStraightLineBody</c>). Walks the
    /// body's statements once across <c>rowInputs.Count</c> rows, evaluating
    /// each statement's expression as a column. Cross-row batching kicks in
    /// at each top-level <see cref="FunctionCallExpression"/>: arguments
    /// resolve to columns, then the function's
    /// <see cref="IScalarFunction.ExecuteBatchAsync"/> packs and dispatches
    /// once (the big win for <c>infer()</c>). Anything the columnar
    /// evaluator doesn't know how to vectorize falls back to per-row
    /// evaluation via the existing <see cref="ExpressionEvaluator"/> — same
    /// results, just no packing for that slot.
    /// </summary>
    /// <remarks>
    /// Parameter binding mirrors the per-row path: each row goes through
    /// <see cref="BindParametersAsync"/> against a private scope, then
    /// the per-row bound values are gathered into columns. This pays
    /// N × bind-cost but inherits the existing CHECK / coercion / default
    /// semantics exactly. Cycle detection pushes/pops once for the entire
    /// batch (the body runs once logically).
    /// </remarks>
    internal async ValueTask<ValueRef[]> ExecuteModelBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> rowInputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> rowOverrides,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        int rowCount = rowInputs.Count;
        if (rowCount == 0) return Array.Empty<ValueRef>();

        Stack<string> stack = _invocationStack.Value ??= new Stack<string>();
        foreach (string active in stack)
        {
            if (string.Equals(active, _descriptor.Name, StringComparison.OrdinalIgnoreCase))
            {
                string chain = string.Join(" → ", stack.Reverse().Append(_descriptor.Name));
                throw new InvalidOperationException(
                    $"Cyclic model call detected: {chain}. " +
                    "Models cannot call themselves directly or transitively.");
            }
        }

        stack.Push(_descriptor.Name);
        DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: batched enter, rowCount={rowCount}");
        try
        {
            // See ExecuteAsync for the rationale on AcquireAsync here —
            // nested batched model calls (e.g. a column-major delegating
            // model invoking its sibling) must participate in residency
            // tracking too.
            ModelCatalog? models = frame.Context?.Models;
            if (models is not null && models.TryGetEntry(_descriptor.Name) is not null)
            {
                using ModelLease lease = await models
                    .AcquireAsync(_descriptor.Name, cancellationToken)
                    .ConfigureAwait(false);
                return await RunBatchedBodyTracedAsync(
                    rowInputs, rowOverrides, frame, rowCount, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunBatchedBodyTracedAsync(
                rowInputs, rowOverrides, frame, rowCount, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            stack.Pop();
        }
    }

    private async ValueTask<ValueRef[]> RunBatchedBodyTracedAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> rowInputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> rowOverrides,
        EvaluationFrame frame,
        int rowCount,
        CancellationToken cancellationToken)
    {
        try
        {
            ValueRef[] results = await ExecuteBatchedBodyAsync(
                rowInputs, rowOverrides, frame, rowCount, cancellationToken).ConfigureAwait(false);
            DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: batched exit, rowCount={rowCount}");
            return results;
        }
        catch (Exception ex)
        {
            DatumActivity.Scalars.Trace($"[model] {_descriptor.Name}: batched THREW {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }

    private async ValueTask<ValueRef[]> ExecuteBatchedBodyAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> rowInputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> rowOverrides,
        EvaluationFrame outerFrame,
        int rowCount,
        CancellationToken cancellationToken)
    {
        Arena variableStore = (Arena)outerFrame.Target;
        // One column per declared variable / parameter, each holding rowCount
        // ValueRefs in row order. Case-insensitive to match VariableScope's
        // lookup contract.
        Dictionary<string, ValueRef[]> columns = new(StringComparer.OrdinalIgnoreCase);

        // Per-row parameter binding. Reuses the per-row BindParametersAsync so
        // CHECK / coercion / default-evaluation semantics are exactly the
        // same shape as the non-batched path — any error a per-row call would
        // raise is raised here with the same row identity.
        for (int row = 0; row < rowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ValueRef> inputs = rowInputs[row];
            IReadOnlyList<ValueRef> overrides = rowOverrides.Count > row
                ? rowOverrides[row]
                : Array.Empty<ValueRef>();
            int supplied = inputs.Count + overrides.Count;
            ValueRef[] args = new ValueRef[supplied];
            for (int i = 0; i < inputs.Count; i++) args[i] = inputs[i];
            for (int i = 0; i < overrides.Count; i++) args[inputs.Count + i] = overrides[i];

            VariableScope perRowScope = new(outerFrame.Accountant);
            await BindParametersAsync(args, outerFrame, variableStore, perRowScope, cancellationToken)
                .ConfigureAwait(false);

            for (int p = 0; p < _descriptor.Parameters.Count; p++)
            {
                string name = _descriptor.Parameters[p].Name;
                if (!columns.TryGetValue(name, out ValueRef[]? col))
                {
                    col = new ValueRef[rowCount];
                    columns[name] = col;
                }
                col[row] = perRowScope.Get(name);
            }
        }

        // Body frame carries the model descriptor so infer() can resolve its
        // session, exactly like the per-row body's frame. The batched body
        // doesn't declare a single VariableScope (each row has its own perRowScope
        // above + the body's straight-line DECLARE/SET path threads columns through
        // EvaluateColumnAsync, not the frame), so the derived context only needs
        // Store overridden.
        using Execution.ExecutionContext bodyContext = outerFrame.Context.Derive(
            store: variableStore);
        EvaluationFrame bodyFrame = new(Row.Empty, variableStore, bodyContext, currentModel: _descriptor);

        // Walk the body. The IsStraightLineBody contract guarantees the
        // sequence is DECLARE/SET-only up to a terminal RETURN — no IF /
        // WHILE / BLOCK / BREAK / CONTINUE to handle.
        ValueRef[]? resultColumn = null;
        foreach (Statement stmt in _descriptor.StatementBody)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (stmt)
            {
                case DeclareStatement decl:
                {
                    ValueRef[] col;
                    if (decl.Initializer is not null)
                    {
                        Expression effective = decl.TypeName is not null
                            ? new CastExpression(decl.Initializer, decl.TypeName, decl.Span)
                            : decl.Initializer;
                        col = await EvaluateColumnAsync(
                            effective, columns, rowCount, bodyFrame, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (decl.TypeName is not null
                        && TypeAnnotationResolver.TryParse(decl.TypeName, out DataKind kind, out bool isArray))
                    {
                        ValueRef nullValue = isArray ? ValueRef.NullArray(kind) : ValueRef.Null(kind);
                        col = new ValueRef[rowCount];
                        for (int i = 0; i < rowCount; i++) col[i] = nullValue;
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"DECLARE @{decl.VariableName}: cannot resolve type name '{decl.TypeName}'. " +
                            "Use a recognised SQL type or supply an initializer.");
                    }
                    if (columns.ContainsKey(decl.VariableName))
                    {
                        throw new InvalidOperationException(
                            $"Variable '@{decl.VariableName}' is already declared.");
                    }
                    columns[decl.VariableName] = col;
                    break;
                }
                case SetStatement set:
                {
                    ValueRef[] col = await EvaluateColumnAsync(
                        set.Value, columns, rowCount, bodyFrame, cancellationToken)
                        .ConfigureAwait(false);
                    if (!columns.ContainsKey(set.VariableName))
                    {
                        throw new InvalidOperationException(
                            $"Variable '@{set.VariableName}' is not declared.");
                    }
                    columns[set.VariableName] = col;
                    break;
                }
                case ReturnStatement ret:
                {
                    resultColumn = await EvaluateColumnAsync(
                        ret.Value, columns, rowCount, bodyFrame, cancellationToken)
                        .ConfigureAwait(false);
                    break;
                }
                default:
                    throw new InvalidOperationException(
                        $"Statement type '{stmt.GetType().Name}' is not allowed in a batchable model body. " +
                        "ProceduralModelAdapter.IsStraightLineBody should have rejected this body upstream.");
            }
            if (resultColumn is not null) break;
        }

        if (resultColumn is null)
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}' body completed without RETURN.");
        }

        // Post-process each row's return value exactly as the per-row path
        // does: kind coercion (when RETURN's kind differs from RETURNS T),
        // named-type TypeId stamping, and IS NOT NULL enforcement. The per-row
        // pass keeps these semantics identical between batched and per-row
        // dispatch — a small per-row cost in exchange for parity.
        // Post-evaluator runs cast-on-RETURN; it doesn't need a variable
        // scope because the cast operates on the just-evaluated returnValue,
        // not on body-declared variables.
        ExpressionEvaluator postEvaluator = bodyContext.CreateEvaluator();

        for (int row = 0; row < rowCount; row++)
        {
            ValueRef returnValue = resultColumn[row];

            if (!returnValue.IsNull && returnValue.Kind != _returnKind)
            {
                DataValue asData = returnValue.ToDataValue(variableStore, returnValue.TypeId, outerFrame.Types);
                CastExpression syntheticCast = new(
                    new LiteralValueExpression(asData),
                    _descriptor.ReturnTypeName,
                    Span: null);
                returnValue = await postEvaluator
                    .EvaluateAsValueRefAsync(syntheticCast, bodyFrame, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!returnValue.IsNull
                && returnValue.Kind == DataKind.Struct
                && !returnValue.IsArray
                && returnValue.TypeId == 0
                && outerFrame.Types is { } types
                && _descriptor.ReturnTypeName is { } returnTypeName
                && TypeAnnotationResolver.IsNamedType(returnTypeName))
            {
                int namedTypeId = types.GetTypeIdByName(returnTypeName);
                if (namedTypeId != TypeRegistry.NoType)
                {
                    ReadOnlySpan<ValueRef> structFields = returnValue.GetStructFields();
                    ValueRef[] copy = new ValueRef[structFields.Length];
                    structFields.CopyTo(copy);
                    returnValue = ValueRef.FromStruct(copy, (ushort)namedTypeId);
                }
            }

            if (_descriptor.ReturnIsNotNull && returnValue.IsNull)
            {
                throw new InvalidOperationException(
                    $"Model '{_descriptor.QualifiedName}' return value must not be null (row {row}).");
            }

            resultColumn[row] = returnValue;
        }

        return resultColumn;
    }

    /// <summary>
    /// Evaluates <paramref name="expr"/> across <paramref name="rowCount"/>
    /// rows, producing one <see cref="ValueRef"/> per row. The vectorized
    /// fast paths are:
    /// <list type="bullet">
    /// <item>literal — same value broadcast across every row;</item>
    /// <item>variable reference — direct column lookup, zero copy;</item>
    /// <item>function call — recursively evaluate each argument as a column,
    /// then call <see cref="IScalarFunction.ExecuteBatchAsync"/> once. This
    /// is where <c>infer()</c>'s packing fires.</item>
    /// </list>
    /// Anything else falls back to N invocations of the per-row
    /// <see cref="ExpressionEvaluator"/> against a synthesized per-row scope.
    /// The fallback always produces correct results; it just doesn't batch.
    /// </summary>
    private async ValueTask<ValueRef[]> EvaluateColumnAsync(
        Expression expr,
        Dictionary<string, ValueRef[]> columns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        switch (expr)
        {
            case LiteralValueExpression literal:
            {
                ValueRef broadcast = ExpressionEvaluator.ToValueRef(literal.Value, frame);
                ValueRef[] result = new ValueRef[rowCount];
                for (int i = 0; i < rowCount; i++) result[i] = broadcast;
                return result;
            }
            case LiteralExpression literalExpr:
            {
                // Parser-produced literal (the common shape — array literals
                // like `[1, 3, 518, 518]`, scalar constants like `480`,
                // boolean `true`, etc.). Identical to a hoisted
                // LiteralValueExpression for our purposes: evaluate the
                // constant once via the body's evaluator and broadcast it
                // across the row column. Without this case the per-row
                // safety net fires for every literal, blowing the columnar
                // benefit on real model bodies (the GPU waits while CPU
                // rebuilds a VariableScope + ExpressionEvaluator N times
                // per literal per statement).
                // frame.Context is always set when this batched path runs
                // (the body's Derive context wired it). Reuse it directly.
                ExpressionEvaluator broadcastEvaluator = frame.Context!.CreateEvaluator();
                ValueRef broadcast = await broadcastEvaluator
                    .EvaluateAsValueRefAsync(literalExpr, frame, cancellationToken)
                    .ConfigureAwait(false);
                ValueRef[] result = new ValueRef[rowCount];
                for (int i = 0; i < rowCount; i++) result[i] = broadcast;
                return result;
            }
            case ColumnReference colRef
                when colRef.TableName is null
                    && columns.TryGetValue(colRef.ColumnName, out ValueRef[]? col):
            {
                // Direct column hit — return the shared array. The caller
                // never mutates a column it gets back from EvaluateColumnAsync,
                // so sharing is safe.
                return col;
            }
            case CastExpression cast:
            {
                // Evaluate the inner expression columnarly. Then if every
                // row already matches the declared kind/arrayness, skip the
                // cast entirely — the common case for typed DECLAREs like
                // `DECLARE x Float32[] = image_to_tensor_chw(...)` where the
                // function's return shape already matches the annotation.
                // Without this fast path, every typed DECLARE falls into the
                // per-row safety net, which defeats columnar batching at the
                // statement level.
                ValueRef[] inner = await EvaluateColumnAsync(
                    cast.Expression, columns, rowCount, frame, cancellationToken)
                    .ConfigureAwait(false);
                if (!TypeAnnotationResolver.TryParse(cast.TargetType, out DataKind targetKind, out bool targetIsArray))
                {
                    return await EvaluatePerRowAsync(cast, columns, rowCount, frame, cancellationToken)
                        .ConfigureAwait(false);
                }
                bool anyNeedsCoercion = false;
                for (int i = 0; i < rowCount; i++)
                {
                    ValueRef v = inner[i];
                    if (v.IsNull) continue;
                    if (v.IsArray != targetIsArray) { anyNeedsCoercion = true; break; }
                    DataKind effectiveKind = targetIsArray ? v.ArrayElementKind : v.Kind;
                    if (effectiveKind != targetKind) { anyNeedsCoercion = true; break; }
                }
                if (!anyNeedsCoercion) return inner;
                return await EvaluatePerRowAsync(cast, columns, rowCount, frame, cancellationToken)
                    .ConfigureAwait(false);
            }
            case FunctionCallExpression functionCall
                when CanVectorizeFunctionCall(functionCall):
            {
                IScalarFunction? fn = _functions.TryGetScalar(functionCall.FunctionName);
                if (fn is null)
                {
                    DatumActivity.Scalars.Trace(
                        $"model.{_descriptor.Name}.col-fallback unresolved-fn={functionCall.FunctionName}");
                    return await EvaluatePerRowAsync(expr, columns, rowCount, frame, cancellationToken)
                        .ConfigureAwait(false);
                }

                ReadOnlyMemory<ValueRef>[] argCols =
                    new ReadOnlyMemory<ValueRef>[functionCall.Arguments.Count];
                for (int i = 0; i < functionCall.Arguments.Count; i++)
                {
                    ValueRef[] argColumn = await EvaluateColumnAsync(
                        functionCall.Arguments[i], columns, rowCount, frame, cancellationToken)
                        .ConfigureAwait(false);
                    argCols[i] = argColumn.AsMemory();
                }
                using Activity? span = DatumActivity.Scalars.StartActivity(
                    $"col.{functionCall.FunctionName}(rows={rowCount})");
                return await fn.ExecuteBatchAsync(argCols, rowCount, frame, cancellationToken)
                    .ConfigureAwait(false);
            }
            default:
                DatumActivity.Scalars.Trace(
                    $"model.{_descriptor.Name}.col-fallback expr={expr.GetType().Name}");
                return await EvaluatePerRowAsync(expr, columns, rowCount, frame, cancellationToken)
                    .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Per-row safety net for expressions the columnar evaluator doesn't
    /// know how to vectorize (CASE, binary operators on non-trivial inputs,
    /// sub-queries, …). Synthesises a row-local <see cref="VariableScope"/>
    /// populated from <paramref name="columns"/> for the row and invokes the
    /// regular <see cref="ExpressionEvaluator.EvaluateAsValueRefAsync"/> N
    /// times.
    /// </summary>
    private async ValueTask<ValueRef[]> EvaluatePerRowAsync(
        Expression expr,
        Dictionary<string, ValueRef[]> columns,
        int rowCount,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        Arena store = (Arena)frame.Target;
        ValueRef[] result = new ValueRef[rowCount];
        for (int row = 0; row < rowCount; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VariableScope scope = new(frame.Accountant);
            foreach (KeyValuePair<string, ValueRef[]> kv in columns)
            {
                scope.Declare(kv.Key, kv.Value[row]);
            }
            using DatumIngest.Execution.ExecutionContext rowContext = frame.Context!.Derive(
                store: store,
                variableScope: scope,
                variableStore: store);
            ExpressionEvaluator evaluator = rowContext.CreateEvaluator();
            result[row] = await evaluator
                .EvaluateAsValueRefAsync(expr, frame, cancellationToken)
                .ConfigureAwait(false);
        }
        return result;
    }

    /// <summary>
    /// Quickly rejects function calls that carry call-site features the
    /// columnar evaluator doesn't honour: aggregate ORDER BY, DISTINCT,
    /// WITHIN GROUP, filter predicates. Such calls fall through to the
    /// per-row safety net so semantics never silently diverge.
    /// </summary>
    private static bool CanVectorizeFunctionCall(FunctionCallExpression fc)
    {
        if (fc.Distinct) return false;
        if (fc.OrderBy is { Count: > 0 }) return false;
        if (fc.WithinGroupOrderBy is { Count: > 0 }) return false;
        return true;
    }

    private async ValueTask<ValueRef> ExecuteBodyAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        Arena variableStore = (Arena)frame.Target;
        // Body shares the surrounding plan's accountant via frame.Accountant —
        // DECLARE'd payloads inside this body count against the outer budget.
        VariableScope scope = new(frame.Accountant);

        await BindParametersAsync(arguments, frame, variableStore, scope, cancellationToken).ConfigureAwait(false);

        using DatumIngest.Execution.ExecutionContext bodyContext = frame.Context!.Derive(
            store: variableStore,
            variableScope: scope,
            variableStore: variableStore);
        ExpressionEvaluator evaluator = bodyContext.CreateEvaluator();
        // Body frame additionally carries this model's descriptor so infer()
        // resolves its bound session. CreateFrame sets the LambdaInvoker;
        // currentModel is the only thing it doesn't pick up.
        EvaluationFrame bodyFrame = evaluator.CreateFrame(Row.Empty).WithCurrentModel(_descriptor);

        ReturnSignal? signal = await ExecuteStatementsAsync(
            _descriptor.StatementBody, scope, evaluator, bodyFrame, cancellationToken).ConfigureAwait(false);
        if (signal is null)
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}' body completed without RETURN. " +
                "Every control-flow path must terminate with RETURN expr.");
        }

        ValueRef returnValue = signal.Value;

        if (!returnValue.IsNull && returnValue.Kind != _returnKind)
        {
            // Slow-path numeric/string coercion: materialise via the
            // existing CastExpression pipeline. Round-trips through
            // DataValue once — only triggered on kind mismatch.
            DataValue asData = returnValue.ToDataValue(variableStore, returnValue.TypeId, frame.Types);
            CastExpression syntheticCast = new(
                new LiteralValueExpression(asData),
                _descriptor.ReturnTypeName,
                Span: null);
            returnValue = await evaluator.EvaluateAsValueRefAsync(syntheticCast, bodyFrame, cancellationToken).ConfigureAwait(false);
        }

        // Named-type TypeId stamping. When the descriptor declares a
        // named-struct return (e.g. `RETURNS ScoredLabel`) and the body's
        // RETURN evaluated a struct literal, the literal produces a
        // struct value with TypeId=0 — the SQL evaluator doesn't intern
        // a TypeId for ad-hoc struct literals. Downstream consumers
        // (system.* renderers, ImageDrawBoundingBoxes, the catalog's
        // schema resolution) all rely on TypeId to recover the
        // BoundingBox/ScoredLabel/etc. shape. Stamp the named-type
        // TypeId from the per-query registry here so the body's
        // `RETURN {label: ..., score: ...}` carries the same shape
        // metadata that a postprocess-function-built value would.
        if (!returnValue.IsNull
            && returnValue.Kind == DataKind.Struct
            && !returnValue.IsArray
            && returnValue.TypeId == 0
            && frame.Types is { } types
            && _descriptor.ReturnTypeName is { } returnTypeName
            && TypeAnnotationResolver.IsNamedType(returnTypeName))
        {
            int namedTypeId = types.GetTypeIdByName(returnTypeName);
            if (namedTypeId != TypeRegistry.NoType)
            {
                ReadOnlySpan<ValueRef> structFields = returnValue.GetStructFields();
                ValueRef[] copy = new ValueRef[structFields.Length];
                structFields.CopyTo(copy);
                returnValue = ValueRef.FromStruct(copy, (ushort)namedTypeId);
            }
        }

        if (_descriptor.ReturnIsNotNull && returnValue.IsNull)
        {
            throw new InvalidOperationException(
                $"Model '{_descriptor.QualifiedName}' return value must not be null.");
        }

        return returnValue;
    }

    /// <summary>
    /// Binds call-site arguments to the body's parameter names. Same
    /// semantics as <see cref="ProceduralUdfFunction"/>: materialise
    /// arguments into the call's <see cref="EvaluationFrame.Target"/>
    /// store; evaluate any defaults for omitted trailing arguments;
    /// enforce <c>IS NOT NULL</c> annotations before the body runs.
    /// </summary>
    private async ValueTask BindParametersAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        Arena variableStore,
        VariableScope scope,
        CancellationToken cancellationToken)
    {
        int paramCount = _descriptor.Parameters.Count;
        int suppliedCount = arguments.Length;
        // Copy the supplied ValueRefs out of the ReadOnlyMemory's span into a
        // managed array — spans can't be preserved across an await boundary
        // (default-evaluation needs await), and ValueRef is a struct so the
        // copy is cheap.
        ValueRef[] supplied = new ValueRef[suppliedCount];
        arguments.Span.CopyTo(supplied);

        // Child context for parameter binding — same Store / VariableScope /
        // VariableStore as the body's bodyContext, built once here so the
        // optional check + param evaluators below share it.
        using DatumIngest.Execution.ExecutionContext paramContext = frame.Context!.Derive(
            store: variableStore,
            variableScope: scope,
            variableStore: variableStore);

        // Custom checks need an evaluator with the scope wired in so the
        // parameter name resolves to its just-declared value (and earlier
        // params resolve to theirs). Constructed lazily — only if any
        // parameter's check fell into the CustomCheck escape hatch, which
        // is rare in practice; typed checks (BetweenCheck, InCheck, …)
        // don't need it.
        ExpressionEvaluator? checkEvaluator = null;
        EvaluationFrame checkFrame = default;
        if (_hasCustomChecks)
        {
            checkEvaluator = paramContext.CreateEvaluator();
            checkFrame = checkEvaluator.CreateFrame(Row.Empty);
        }

        ExpressionEvaluator? paramEvaluator = null;
        EvaluationFrame paramFrame = default;

        for (int i = 0; i < paramCount; i++)
        {
            UdfParameter param = _descriptor.Parameters[i];
            ValueRef value;
            if (i < suppliedCount)
            {
                value = supplied[i];
            }
            else if (param.Default is not null)
            {
                paramEvaluator ??= paramContext.CreateEvaluator();
                if (paramFrame.Source is null)
                {
                    paramFrame = paramEvaluator.CreateFrame(Row.Empty);
                }
                value = await paramEvaluator.EvaluateAsValueRefAsync(param.Default, paramFrame, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Model '{_descriptor.QualifiedName}' parameter '@{param.Name}' has no argument and no default.");
            }

            if (param.IsNotNull && value.IsNull)
            {
                throw new InvalidOperationException(
                    $"Model '{_descriptor.QualifiedName}' parameter '@{param.Name}' must not be null.");
            }

            // Coerce the bound value to the parameter's declared type.
            // SQL literals pick the narrowest type that exactly represents
            // them — `0.5` parses as Float32 (exact in binary) but `0.9`
            // parses as Float64 (inexact) — so a model that declares
            // `conf_thresh Float32` and is called as `model(img, 0.9)`
            // would otherwise see a Float64 in scope and surface as a
            // signature mismatch the next time a downstream function with
            // an exact Float32 parameter touches it. The cast funnels
            // every supplied argument through the same coercion path the
            // body's DECLARE statements use.
            if (!value.IsNull && param.TypeName is not null
                && TypeAnnotationResolver.TryParse(param.TypeName, out DataKind declaredKind, out bool declaredIsArray)
                && NeedsCoercion(value, declaredKind, declaredIsArray))
            {
                paramEvaluator ??= paramContext.CreateEvaluator();
                if (paramFrame.Source is null)
                {
                    paramFrame = paramEvaluator.CreateFrame(Row.Empty);
                }
                DataValue asData = value.ToDataValue(variableStore, value.TypeId, frame.Types);
                CastExpression cast = new(
                    new LiteralValueExpression(asData),
                    param.TypeName,
                    Span: null);
                value = await paramEvaluator
                    .EvaluateAsValueRefAsync(cast, paramFrame, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Declare into scope BEFORE running the check so any CustomCheck
            // expression can reference the just-bound parameter (and any
            // earlier parameter) through the evaluator's variable lookup.
            // Typed checks don't need the scope, but the ordering is uniform.
            scope.Declare(param.Name, value);

            ParameterCheck? check = _parameterChecks[i];
            if (check is null) continue;

            if (check is CustomCheck cc)
            {
                // NULL passes any check (matches typed-check semantics).
                if (value.IsNull) continue;
                bool ok = await checkEvaluator!
                    .EvaluateAsBooleanAsync(cc.Expr, checkFrame, cancellationToken)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    throw new FunctionArgumentException(
                        _descriptor.QualifiedName.ToString(),
                        $"parameter '@{param.Name}': value violates CHECK constraint.");
                }
            }
            else
            {
                string? error = check.Validate(value);
                if (error is not null)
                {
                    throw new FunctionArgumentException(
                        _descriptor.QualifiedName.ToString(),
                        $"parameter '@{param.Name}': {error}");
                }
            }
        }
    }

    private static async ValueTask<ReturnSignal?> ExecuteStatementsAsync(
        IReadOnlyList<Statement> statements,
        VariableScope scope,
        ExpressionEvaluator evaluator,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        foreach (Statement stmt in statements)
        {
            ReturnSignal? signal = await ExecuteStatementAsync(stmt, scope, evaluator, frame, cancellationToken).ConfigureAwait(false);
            if (signal is not null) return signal;
        }
        return null;
    }

    private static async ValueTask<ReturnSignal?> ExecuteStatementAsync(
        Statement statement,
        VariableScope scope,
        ExpressionEvaluator evaluator,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        switch (statement)
        {
            case ReturnStatement ret:
            {
                ValueRef value = await evaluator.EvaluateAsValueRefAsync(ret.Value, frame, cancellationToken).ConfigureAwait(false);
                return new ReturnSignal(value);
            }
            case DeclareStatement decl:
            {
                ValueRef declValue;
                if (decl.Initializer is not null)
                {
                    Expression effective = decl.TypeName is not null
                        ? new CastExpression(decl.Initializer, decl.TypeName, decl.Span)
                        : decl.Initializer;
                    declValue = await evaluator.EvaluateAsValueRefAsync(effective, frame, cancellationToken).ConfigureAwait(false);
                }
                else if (decl.TypeName is not null
                    && TypeAnnotationResolver.TryParse(decl.TypeName, out DataKind kind, out bool isArray))
                {
                    declValue = isArray ? ValueRef.NullArray(kind) : ValueRef.Null(kind);
                }
                else
                {
                    throw new InvalidOperationException(
                        $"DECLARE @{decl.VariableName}: cannot resolve type name '{decl.TypeName}'. " +
                        "Use a recognised SQL type or supply an initializer.");
                }
                scope.Declare(decl.VariableName, declValue);
                return null;
            }
            case SetStatement set:
            {
                ValueRef value = await evaluator.EvaluateAsValueRefAsync(set.Value, frame, cancellationToken).ConfigureAwait(false);
                scope.Set(set.VariableName, value);
                return null;
            }
            case IfStatement ifStmt:
            {
                bool predicate = await evaluator.EvaluateAsBooleanAsync(ifStmt.Predicate, frame, cancellationToken).ConfigureAwait(false);
                Statement? branch = predicate ? ifStmt.Then : ifStmt.Else;
                return branch is null ? null : await ExecuteStatementAsync(branch, scope, evaluator, frame, cancellationToken).ConfigureAwait(false);
            }
            case WhileStatement whileStmt:
            {
                while (await evaluator.EvaluateAsBooleanAsync(whileStmt.Predicate, frame, cancellationToken).ConfigureAwait(false))
                {
                    scope.PushFrame();
                    try
                    {
                        ReturnSignal? signal = await ExecuteStatementAsync(whileStmt.Body, scope, evaluator, frame, cancellationToken).ConfigureAwait(false);
                        if (signal is not null) return signal;
                    }
                    finally
                    {
                        scope.PopFrame();
                    }
                }
                return null;
            }
            case BlockStatement block:
            {
                scope.PushFrame();
                try
                {
                    return await ExecuteStatementsAsync(block.Statements, scope, evaluator, frame, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    scope.PopFrame();
                }
            }
            case BreakStatement:
            case ContinueStatement:
                throw new InvalidOperationException(
                    "BREAK / CONTINUE are not supported inside model bodies in v1. " +
                    "Use IF / RETURN to short-circuit.");
            default:
                throw new InvalidOperationException(
                    $"Statement type '{statement.GetType().Name}' is not allowed inside a model body.");
        }
    }

    private static int MinRequiredArity(IReadOnlyList<UdfParameter> parameters)
    {
        int min = 0;
        foreach (UdfParameter p in parameters)
        {
            if (p.Default is not null) break;
            min++;
        }
        return min;
    }

    /// <summary>
    /// True when the bound <paramref name="value"/>'s runtime shape differs
    /// from the parameter's declared <paramref name="declaredKind"/> /
    /// <paramref name="declaredIsArray"/> enough to require a CAST round-
    /// trip. Cheap kind/array check up front so the common already-matches
    /// case doesn't pay the cast overhead.
    /// </summary>
    private static bool NeedsCoercion(ValueRef value, DataKind declaredKind, bool declaredIsArray)
    {
        if (value.IsArray != declaredIsArray) return true;
        if (declaredIsArray)
        {
            return value.ArrayElementKind != declaredKind;
        }
        return value.Kind != declaredKind;
    }

    private sealed record ReturnSignal(ValueRef Value);
}
