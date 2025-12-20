using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Runtime adapter that lets a procedural UDF (a <c>CREATE FUNCTION … BEGIN…END</c>
/// registration) participate in the same scalar-dispatch pipeline as built-in
/// scalar functions. Constructed once per <see cref="UdfDescriptor"/> at
/// <c>CREATE FUNCTION</c> time; <see cref="Execute"/> runs the procedural body
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
    private readonly bool _returnIsArray;

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
        if (!TypeAnnotationResolver.TryParse(descriptor.ReturnTypeName, out _returnKind, out _returnIsArray))
        {
            throw new ArgumentException(
                $"Procedural UDF '{descriptor.Name}': cannot resolve return type '{descriptor.ReturnTypeName}'.",
                nameof(descriptor));
        }
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
    public bool ProducesArray => _returnIsArray;

    /// <inheritdoc/>
    /// <remarks>
    /// Conservative default: a procedural body can call any other function
    /// (including <c>random()</c>, <c>now()</c>, models) so we can't claim
    /// purity from outside. The catalog records the user's
    /// <see cref="UdfDescriptor.IsPure"/> assertion; the planner's CSE pass
    /// keys on that descriptor flag rather than this <see cref="IsPure"/>
    /// property when deciding whether to dedupe call sites.
    /// </remarks>
    public bool IsPure => false;

    /// <inheritdoc/>
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
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
            return ExecuteBody(arguments, frame);
        }
        finally
        {
            stack.Pop();
        }
    }

    private ValueRef ExecuteBody(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        // Local variables (parameters + DECLAREd) live in the call's target
        // arena. They become unreachable when the caller's row batch
        // recycles, so no per-call cleanup is needed.
        IValueStore variableStore = frame.Target;
        VariableScope scope = new();

        BindParameters(arguments, frame, variableStore, scope);

        // Body evaluator: same FunctionRegistry the outer query uses (so
        // nested udf./scalar calls resolve identically) but a fresh,
        // private VariableScope so the procedural locals don't leak in
        // either direction. No row context — procedural bodies can't
        // reference outer columns by name.
        ExpressionEvaluator evaluator = new(
            _functions,
            meter: null,
            outerRow: null,
            sourceSchema: null,
            letBindingExpressions: null,
            store: variableStore,
            sidecarRegistry: frame.SidecarRegistry,
            variableScope: scope,
            variableStore: variableStore,
            typeRegistry: null);

        EvaluationFrame bodyFrame = new(
            Row.Empty,
            variableStore,
            variableStore,
            outerRow: null,
            sidecarRegistry: frame.SidecarRegistry);

        ReturnSignal? signal = ExecuteStatements(_descriptor.StatementBody!, scope, evaluator, bodyFrame);
        if (signal is null)
        {
            throw new InvalidOperationException(
                $"Procedural UDF 'udf.{_descriptor.Name}' completed without RETURN. " +
                "Every control-flow path must terminate with RETURN expr.");
        }

        DataValue returnValue = signal.Value;

        // Apply RETURNS T coercion. The parser already required RETURNS T
        // for procedural bodies, so _returnKind is always concrete.
        if (!returnValue.IsNull && returnValue.Kind != _returnKind)
        {
            CastExpression syntheticCast = new(
                new LiteralValueExpression(returnValue),
                _descriptor.ReturnTypeName!,
                Span: null);
            returnValue = evaluator.Evaluate(syntheticCast, bodyFrame);
        }

        if (_descriptor.ReturnIsNotNull && returnValue.IsNull)
        {
            throw new InvalidOperationException(
                $"UDF 'udf.{_descriptor.Name}' return value must not be null.");
        }

        // Hand the value back through the caller's frame so any arena-backed
        // payload is read against frame.Target — the variable store is
        // frame.Target, so source == target and the lift is a straight read.
        return evaluator.EvaluateAsValueRef(
            new LiteralValueExpression(returnValue),
            bodyFrame);
    }

    /// <summary>
    /// Binds call-site arguments to the procedural body's parameter names in
    /// <paramref name="scope"/>. Each <see cref="ValueRef"/> is materialised
    /// into <paramref name="variableStore"/> so subsequent <c>@param</c>
    /// reads see a stable <see cref="DataValue"/> regardless of the caller's
    /// arena lifetime. <c>IS NOT NULL</c> annotations fire here (before any
    /// body code runs) so the failure points at the call boundary.
    /// </summary>
    private void BindParameters(
        ReadOnlySpan<ValueRef> arguments,
        in EvaluationFrame frame,
        IValueStore variableStore,
        VariableScope scope)
    {
        for (int i = 0; i < _descriptor.Parameters.Count; i++)
        {
            UdfParameter param = _descriptor.Parameters[i];
            DataValue value;
            if (i < arguments.Length)
            {
                value = arguments[i].ToDataValue(variableStore);
            }
            else if (param.Default is not null)
            {
                // Defaults are AST expression fragments. Evaluate them
                // against the body's frame (no parameters yet, but
                // defaults shouldn't reference parameters anyway — they're
                // call-site fallbacks).
                ExpressionEvaluator defaultEvaluator = new(
                    _functions,
                    meter: null,
                    store: variableStore,
                    sidecarRegistry: frame.SidecarRegistry);
                EvaluationFrame defaultFrame = new(
                    Row.Empty,
                    variableStore,
                    variableStore,
                    outerRow: null,
                    sidecarRegistry: frame.SidecarRegistry);
                value = defaultEvaluator.Evaluate(param.Default, defaultFrame);
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

            scope.Declare(param.Name, value);
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
    private static ReturnSignal? ExecuteStatements(
        IReadOnlyList<Statement> statements,
        VariableScope scope,
        ExpressionEvaluator evaluator,
        in EvaluationFrame frame)
    {
        foreach (Statement stmt in statements)
        {
            ReturnSignal? signal = ExecuteStatement(stmt, scope, evaluator, frame);
            if (signal is not null) return signal;
        }
        return null;
    }

    private static ReturnSignal? ExecuteStatement(
        Statement statement,
        VariableScope scope,
        ExpressionEvaluator evaluator,
        in EvaluationFrame frame)
    {
        switch (statement)
        {
            case ReturnStatement ret:
            {
                DataValue value = evaluator.Evaluate(ret.Value, frame);
                return new ReturnSignal(value);
            }
            case DeclareStatement decl:
            {
                DataValue declValue;
                if (decl.Initializer is not null)
                {
                    Expression effective = decl.TypeName is not null
                        ? new CastExpression(decl.Initializer, decl.TypeName, decl.Span)
                        : decl.Initializer;
                    declValue = evaluator.Evaluate(effective, frame);
                }
                else if (decl.TypeName is not null
                    && TypeAnnotationResolver.TryParse(decl.TypeName, out DataKind kind, out bool isArray))
                {
                    declValue = isArray ? DataValue.NullArrayOf(kind) : DataValue.Null(kind);
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
                DataValue value = evaluator.Evaluate(set.Value, frame);
                scope.Set(set.VariableName, value);
                return null;
            }
            case IfStatement ifStmt:
            {
                bool predicate = evaluator.EvaluateAsBoolean(ifStmt.Predicate, frame);
                Statement? branch = predicate ? ifStmt.Then : ifStmt.Else;
                return branch is null ? null : ExecuteStatement(branch, scope, evaluator, frame);
            }
            case WhileStatement whileStmt:
            {
                // Each iteration pushes a fresh frame so DECLAREs inside the
                // body don't accumulate across iterations.
                while (evaluator.EvaluateAsBoolean(whileStmt.Predicate, frame))
                {
                    scope.PushFrame();
                    try
                    {
                        ReturnSignal? signal = ExecuteStatement(whileStmt.Body, scope, evaluator, frame);
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
                    return ExecuteStatements(block.Statements, scope, evaluator, frame);
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
    /// Wraps a <c>RETURN</c>-produced value so the recursive statement
    /// walker can distinguish "no return yet" (<see langword="null"/>) from
    /// "return fired with a null value" (a non-null signal whose
    /// <see cref="Value"/> is <c>NULL</c>).
    /// </summary>
    private sealed record ReturnSignal(DataValue Value);
}
