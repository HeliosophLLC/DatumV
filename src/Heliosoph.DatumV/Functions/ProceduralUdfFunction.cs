using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Runtime adapter that lets a procedural UDF (a <c>CREATE FUNCTION … BEGIN…END</c>
/// registration) participate in the same scalar-dispatch pipeline as built-in
/// scalar functions. Constructed once per <see cref="UdfDescriptor"/> at
/// <c>CREATE FUNCTION</c> time; <see cref="ExecuteAsync"/> runs the procedural body
/// per call against a fresh <see cref="VariableScope"/> bound with the call-
/// site arguments.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Body interpretation.</strong> The body is walked statement-by-
/// statement against a private evaluator. Supported procedural shapes mirror
/// what the parser accepts: <c>DECLARE</c>, <c>SET</c>, <c>IF</c>/<c>ELSE</c>,
/// <c>WHILE</c>, <c>BEGIN</c>/<c>END</c>, and <c>RETURN</c>. The body must
/// terminate via a <c>RETURN expr</c>; any path that runs off the end raises
/// an error.
/// </para>
/// <para>
/// <strong>Cycle detection.</strong> A per-async-context invocation stack
/// holds the names of the currently-executing procedural UDFs. Re-entering a
/// name already on the stack throws — recursion is not supported in v1, same
/// rule as macro UDFs. The stack is <see cref="AsyncLocal{T}"/>-scoped so it
/// flows correctly through any await boundaries that surround the call (the
/// scalar dispatch path itself is sync but the surrounding query pipeline is
/// async).
/// </para>
/// <para>
/// <strong>Arena lifetime.</strong> All local variables and intermediate
/// values live in the call's <see cref="EvaluationFrame.Target"/> — the
/// arena that the call's result is also written into. Once the function
/// returns, those values become unreachable as soon as the caller's row
/// batch recycles, so there's no per-invocation cleanup to do.
/// </para>
/// </remarks>
public sealed class ProceduralUdfFunction : IScalarFunction
{
    /// <summary>
    /// Per-async-context stack of currently-executing procedural-UDF names.
    /// Used for cycle detection; mirrors the <c>UdfInliner</c>'s plan-time
    /// stack but operates at runtime because procedural UDFs aren't inlined.
    /// </summary>
    private static readonly AsyncLocal<Stack<string>?> _invocationStack = new();

    private readonly UdfDescriptor _descriptor;
    private readonly FunctionRegistry _functions;
    private readonly DataKind _returnKind;

    /// <summary>
    /// One slot per declared parameter, in the same order as
    /// <see cref="UdfDescriptor.Parameters"/>. Non-null entries are the
    /// canonicalised typed check evaluated against each bound value
    /// before the body runs. Resolved once at construction time so per-row
    /// parameter binding stays a single array lookup.
    /// </summary>
    private readonly ParameterCheck?[] _parameterChecks;

    /// <summary>
    /// Cached "any parameter carries a <see cref="CustomCheck"/>" flag — the
    /// dispatch path only constructs the scope-bound check evaluator when
    /// at least one parameter needs it.
    /// </summary>
    private readonly bool _hasCustomChecks;

    /// <summary>
    /// Creates an adapter for <paramref name="descriptor"/>. The descriptor
    /// must be procedural (<see cref="UdfDescriptor.IsProcedural"/> is
    /// <see langword="true"/>) and carry a non-null
    /// <see cref="UdfDescriptor.ReturnTypeName"/>; both invariants are
    /// already enforced by the parser and the catalog at registration time.
    /// </summary>
    /// <param name="descriptor">The procedural UDF's descriptor.</param>
    /// <param name="functions">The catalog's scalar registry — passed to the body's evaluator so nested function calls resolve identically to a top-level expression.</param>
    public ProceduralUdfFunction(UdfDescriptor descriptor, FunctionRegistry functions)
    {
        if (!descriptor.IsProcedural)
        {
            throw new ArgumentException(
                $"UDF '{descriptor.Name}' is not procedural; ProceduralUdfFunction only adapts BEGIN…END bodies.",
                nameof(descriptor));
        }
        if (descriptor.ReturnTypeName is null)
        {
            throw new ArgumentException(
                $"Procedural UDF '{descriptor.Name}' has no declared return type. " +
                "RETURNS T is required for procedural functions.",
                nameof(descriptor));
        }

        _descriptor = descriptor;
        _functions = functions;
        if (!TypeAnnotationResolver.TryParse(descriptor.ReturnTypeName, out _returnKind, out _))
        {
            throw new ArgumentException(
                $"Procedural UDF '{descriptor.Name}': cannot resolve return type '{descriptor.ReturnTypeName}'.",
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
    /// <remarks>
    /// Validates arity (must be at least the count of leading parameters
    /// without defaults, at most the total count). The argument kinds
    /// themselves aren't checked against the parameter type annotations
    /// — those are advisory hints for the editor and any required
    /// coercion happens at evaluation time when the parameter is read.
    /// </remarks>
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
                $"UDF 'udf.{_descriptor.Name}' expects {expected}, got {argumentKinds.Length}.");
        }
        return _returnKind;
    }

    /// <inheritdoc/>
    public bool IsPure => _descriptor.IsPure;

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
                    $"Cyclic procedural UDF call detected: {chain}. " +
                    "Procedural UDFs cannot call themselves directly or transitively.");
            }
        }

        stack.Push(_descriptor.Name);
        try
        {
            return await ExecuteBodyAsync(arguments, frame, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            stack.Pop();
        }
    }

    private async ValueTask<ValueRef> ExecuteBodyAsync(
        ReadOnlyMemory<ValueRef> arguments, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        // Local variables (parameters + DECLAREd) live in the call's target
        // arena. They become unreachable when the caller's row batch
        // recycles, so no per-call cleanup is needed. Cast to Arena because
        // the procedural body's child ExecutionContext needs the concrete
        // type for lifecycle management — every caller of a scalar function
        // passes an Arena-backed frame in practice.
        Arena variableStore = (Arena)frame.Target;
        // Procedural UDF body shares the surrounding plan's accountant via
        // frame.Accountant — DECLARE'd payloads inside this body count
        // against the outer budget the same way a top-level DECLARE would.
        VariableScope scope = new(frame.Accountant);

        await BindParametersAsync(arguments, frame, variableStore, scope, cancellationToken).ConfigureAwait(false);

        // Body evaluator: same FunctionRegistry the outer query uses (so
        // nested udf./scalar calls resolve identically) but a fresh,
        // private VariableScope so the procedural locals don't leak in
        // either direction. No row context — procedural bodies can't
        // reference outer columns by name. The child context borrows
        // ambient state from frame.Context; Store/VariableScope/VariableStore
        // override that to point at this call's per-call arena + scope.
        using Execution.ExecutionContext bodyContext = frame.Context.Derive(
            store: variableStore,
            variableScope: scope,
            variableStore: variableStore);
        ExpressionEvaluator evaluator = bodyContext.CreateEvaluator();
        EvaluationFrame bodyFrame = evaluator.CreateFrame(Row.Empty);

        ReturnSignal? signal = await ExecuteStatementsAsync(_descriptor.StatementBody!, scope, evaluator, bodyFrame, cancellationToken).ConfigureAwait(false);
        if (signal is null)
        {
            throw new InvalidOperationException(
                $"Procedural UDF 'udf.{_descriptor.Name}' completed without RETURN. " +
                "Every control-flow path must terminate with RETURN expr.");
        }

        ValueRef returnValue = signal.Value;

        // Apply RETURNS T coercion. The parser already required RETURNS T
        // for procedural bodies, so _returnKind is always concrete.
        if (!returnValue.IsNull && returnValue.Kind != _returnKind)
        {
            // Slow-path: round-trip through DataValue + CastExpression for
            // numeric/string coercion. Only triggered when the body's RETURN
            // expression's kind doesn't match the declared return type.
            DataValue asData = returnValue.ToDataValue(variableStore, returnValue.TypeId, frame.Types);
            CastExpression syntheticCast = new(
                new LiteralValueExpression(asData),
                _descriptor.ReturnTypeName!,
                Span: null);
            returnValue = await evaluator.EvaluateAsValueRefAsync(syntheticCast, bodyFrame, cancellationToken).ConfigureAwait(false);
        }

        if (_descriptor.ReturnIsNotNull && returnValue.IsNull)
        {
            throw new InvalidOperationException(
                $"UDF 'udf.{_descriptor.Name}' return value must not be null.");
        }

        return returnValue;
    }

    /// <summary>
    /// Binds call-site arguments to the procedural body's parameter names in
    /// <paramref name="scope"/>. Each <see cref="ValueRef"/> flows in
    /// unchanged — managed payloads stay managed, no arena materialisation
    /// at the parameter boundary. <c>IS NOT NULL</c> annotations fire here
    /// (before any body code runs) so the failure points at the call boundary.
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

        // Child context for parameter binding: same shape as the body's
        // bodyContext, but built once here so checkEvaluator + paramEvaluator
        // share it. Borrows ambient state from frame.Context; overrides
        // Store/VariableScope/VariableStore to point at this call's arena.
        using Execution.ExecutionContext paramContext = frame.Context.Derive(
            store: variableStore,
            variableScope: scope,
            variableStore: variableStore);

        // Same lazy-evaluator pattern as ProceduralModelFunction — only
        // constructed when at least one CHECK fell into the CustomCheck
        // escape hatch (rare; typed checks short-circuit through
        // ParameterCheck.Validate directly).
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
                // Defaults are AST expression fragments. Evaluate them
                // against the body's frame (no parameters yet, but
                // defaults shouldn't reference parameters anyway — they're
                // call-site fallbacks).
                paramEvaluator ??= paramContext.CreateEvaluator();
                if (paramFrame.Source is null)
                {
                    paramFrame = paramEvaluator.CreateFrame(Row.Empty);
                }
                value = await paramEvaluator.EvaluateAsValueRefAsync(param.Default, paramFrame, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Arity validation in ValidateArguments already rejected
                // this case; reachable only via a programmatically built
                // call expression that bypasses validation.
                throw new InvalidOperationException(
                    $"UDF 'udf.{_descriptor.Name}' parameter '@{param.Name}' has no argument and no default.");
            }

            if (param.IsNotNull && value.IsNull)
            {
                throw new InvalidOperationException(
                    $"UDF 'udf.{_descriptor.Name}' parameter '@{param.Name}' must not be null.");
            }

            // Coerce the bound value to the parameter's declared type — see
            // the matching block in ProceduralModelFunction for the rationale
            // (SQL literals pick the narrowest exact type, so `0.5` is
            // Float32 but `0.9` is Float64; an unchecked bind drops the
            // mismatch into the body's downstream signature checks).
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

            // Declare before the check so any CustomCheck expression can
            // resolve the just-bound parameter (and any earlier parameter)
            // through the scope-bound evaluator's variable lookup.
            scope.Declare(param.Name, value);

            ParameterCheck? check = _parameterChecks[i];
            if (check is null) continue;

            if (check is CustomCheck cc)
            {
                if (value.IsNull) continue;
                bool ok = await checkEvaluator!
                    .EvaluateAsBooleanAsync(cc.Expr, checkFrame, cancellationToken)
                    .ConfigureAwait(false);
                if (!ok)
                {
                    throw new FunctionArgumentException(
                        _descriptor.Name,
                        $"parameter '@{param.Name}': value violates CHECK constraint.");
                }
            }
            else
            {
                string? error = check.Validate(value);
                if (error is not null)
                {
                    throw new FunctionArgumentException(
                        _descriptor.Name,
                        $"parameter '@{param.Name}': {error}");
                }
            }
        }
    }

    /// <summary>
    /// Executes <paramref name="statements"/> in order. Returns a non-
    /// <see langword="null"/> <see cref="ReturnSignal"/> as soon as a
    /// <c>RETURN</c> fires (including from inside a nested control-flow
    /// block) so the caller can stop walking siblings; returns
    /// <see langword="null"/> when the sequence completes by falling off
    /// the end. v1 doesn't support BREAK/CONTINUE in procedural UDF bodies
    /// (the parser allows them but they're rejected here as out-of-loop
    /// constructs that have no useful semantics for a scalar function).
    /// </summary>
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
                // Each iteration pushes a fresh frame so DECLAREs inside the
                // body don't accumulate across iterations.
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
                    "BREAK / CONTINUE are not supported inside procedural UDF bodies in v1. " +
                    "Use IF / RETURN to short-circuit.");
            default:
                throw new InvalidOperationException(
                    $"Statement type '{statement.GetType().Name}' is not allowed inside a procedural UDF body.");
        }
    }

    /// <summary>
    /// Returns the minimum required argument count: the count of leading
    /// parameters that have no default. The catalog's
    /// <c>ValidateDefaultsContiguous</c> check guarantees defaults sit at
    /// the tail of the parameter list, so the prefix length is the minimum
    /// legal arity.
    /// </summary>
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
    /// <paramref name="declaredIsArray"/> enough to require a CAST round-trip.
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

    /// <summary>
    /// Wraps a <c>RETURN</c>-produced value so the recursive statement
    /// walker can distinguish "no return yet" (<see langword="null"/>) from
    /// "return fired with a null value" (a non-null signal whose
    /// <see cref="Value"/> is <c>NULL</c>).
    /// </summary>
    private sealed record ReturnSignal(ValueRef Value);
}
