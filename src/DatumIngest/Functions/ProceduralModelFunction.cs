using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Model;
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
        IValueStore variableStore = frame.Target;
        VariableScope scope = new();

        await BindParametersAsync(arguments, frame, variableStore, scope, cancellationToken).ConfigureAwait(false);

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
            typeRegistry: frame.Types);

        // The load-bearing tweak vs ProceduralUdfFunction: body frame
        // carries this model's descriptor so infer() can resolve its
        // bound session.
        EvaluationFrame bodyFrame = new(
            Row.Empty,
            variableStore,
            variableStore,
            outerRow: null,
            sidecarRegistry: frame.SidecarRegistry,
            types: frame.Types,
            typeIdTranslations: frame.TypeIdTranslations,
            currentModel: _descriptor);

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
        IValueStore variableStore,
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
                    sidecarRegistry: frame.SidecarRegistry,
                    types: frame.Types);
                value = await defaultEvaluator.EvaluateAsValueRefAsync(param.Default, defaultFrame, cancellationToken).ConfigureAwait(false);
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

            scope.Declare(param.Name, value);
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

    private sealed record ReturnSignal(ValueRef Value);
}
