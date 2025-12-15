using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Evaluates AST <see cref="Expression"/> nodes against a <see cref="Row"/>.
/// Per-call context (row, source arena for reading, target arena for writing, outer
/// row) is passed through an <see cref="EvaluationFrame"/>. A backward-compatible
/// <c>Row</c> overload reuses the store supplied at construction for both arenas.
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly FunctionRegistry _functions;
    private readonly QueryMeter? _meter;

    /// <summary>
    /// Persistent store used for (1) per-evaluator caches (<see cref="_inValueSetCache"/>)
    /// whose <see cref="DataValue"/> entries must outlive any single batch, and
    /// (2) the <see cref="Row"/>-only overload of
    /// <see cref="Evaluate(Expression, Row)"/> which constructs a default frame using this
    /// store for both the source and target arenas. Callers that want true two-arena
    /// behaviour should invoke the <see cref="EvaluationFrame"/>-based overloads instead.
    /// </summary>
    private readonly IValueStore? _store;

    /// <summary>
    /// The persistent value store this evaluator was constructed with, or <see langword="null"/>
    /// if none was supplied. Exposed so callers that don't have a separate
    /// <see cref="EvaluationFrame"/>/<see cref="InvocationFrame"/> in scope can still build one
    /// keyed off the same store.
    /// </summary>
    public IValueStore? Store => _store;

    /// <summary>
    /// Optional sidecar registry for resolving <c>FlagInSidecar</c> DataValues. Threaded into
    /// <see cref="EvaluationFrame.SidecarRegistry"/> by the simple <see cref="Evaluate(Expression, Row)"/>
    /// overload and into the <see cref="InvocationFrame"/> built for scalar-function dispatch,
    /// so image / byte-array functions can resolve sidecar-backed payloads.
    /// </summary>
    private readonly SidecarRegistry? _sidecarRegistry;

    private readonly Row? _outerRow;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Procedural variable scope chain — the visibility side of the variable
    /// substrate. Walked innermost-first when resolving a
    /// <c>VariableExpression</c> at evaluation time. <see langword="null"/>
    /// when the evaluator runs outside a procedural batch (every existing
    /// query path); referencing <c>@var</c> in that case throws.
    /// </summary>
    private readonly VariableScope? _variableScope;

    /// <summary>
    /// Borrowed reference to the procedure-lifetime arena holding bound
    /// variable payloads. Source store for the stabilise that copies
    /// variable values out into the active <see cref="EvaluationFrame.Target"/>
    /// arena on read. Paired with <see cref="_variableScope"/> — both are
    /// non-null inside a procedural batch, both null outside it.
    /// </summary>
    private readonly IValueStore? _variableStore;

    /// <summary>
    /// Maps LET binding names to their source expressions. Used by
    /// <see cref="EvaluateStructFieldAccess"/> when the schema doesn't carry struct
    /// field metadata for a hidden <c>__destructure_N</c> binding: if the binding's
    /// original expression is a <see cref="StructLiteralExpression"/>, field names
    /// can be recovered from the AST without schema.
    /// </summary>
    private readonly IReadOnlyDictionary<string, Expression>? _letBindingExpressions;

    /// <summary>
    /// Optional per-query type registry for stamping type-ids onto struct literals and
    /// using the registry as the primary resolution path in struct field access.
    /// Null when constructed via the field-based overload without one.
    /// </summary>
    private readonly TypeRegistry? _typeRegistry;

    /// <summary>
    /// Compiled regex cache for case-sensitive LIKE patterns. Avoids recompiling
    /// the same SQL LIKE pattern on every row comparison.
    /// </summary>
    private readonly Dictionary<string, Regex> _likeRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiled regex cache for case-insensitive ILIKE patterns.
    /// </summary>
    private readonly Dictionary<string, Regex> _iLikeRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Compiled regex cache for REGEXP patterns (user-supplied regular expressions).
    /// </summary>
    private readonly Dictionary<string, Regex> _regexpCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Cached resolved DataKind for each CASE expression, computed once on first evaluation.
    /// Used to coerce branch results to a consistent type for downstream consumers.
    /// </summary>
    private readonly Dictionary<CaseExpression, DataKind?> _caseResolvedKindCache = new();

    /// <summary>
    /// Cached hash set of non-null literal values for each <see cref="InExpression"/> whose
    /// <see cref="InExpression.Values"/> are all <see cref="LiteralExpression"/>.
    /// Built on first evaluation to convert O(n) linear scans into O(1) hash lookups.
    /// The <see langword="bool"/> tracks whether any value in the list was <see langword="null"/>
    /// (needed for SQL three-valued logic).
    /// </summary>
    private readonly Dictionary<InExpression, (HashSet<DataValue> NonNullValues, bool HasNull)> _inValueSetCache = new();

    /// <summary>
    /// Per-call-site cache of which <see cref="FunctionCallExpression"/> nodes have already
    /// passed <see cref="IScalarFunction.ValidateArguments"/>. The validation depends only on
    /// the static argument kinds (which are stable for the duration of a query), so it's safe
    /// to run exactly once per call site on first invocation. This catches errors like
    /// <c>blur(file)</c> (arity) and <c>blur(file, 'x')</c> (type) at the first row instead of
    /// letting the function body crash with an opaque <c>IndexOutOfRangeException</c> /
    /// <c>InvalidOperationException</c>.
    /// </summary>
    private readonly HashSet<FunctionCallExpression> _validatedScalarCalls = new();

    /// <summary>
    /// Creates an evaluator that can resolve function calls.
    /// </summary>
    /// <param name="functions">Registry of available functions.</param>
    /// <param name="meter">Optional meter for accumulating Query Unit costs, or <see langword="null"/> for unmetered execution.</param>
    /// <param name="outerRow">
    /// Optional outer row from a correlated scalar subquery, or <see langword="null"/> when not inside
    /// a correlated subquery. Column references that cannot be resolved against the current row
    /// will fall back to this row. Only consulted by the <see cref="Evaluate(Expression, Row)"/>
    /// backward-compatible overload; the <see cref="EvaluationFrame"/>-based overloads carry the
    /// outer row per-call.
    /// </param>
    /// <param name="sourceSchema">
    /// Optional query output schema used to resolve struct field names at evaluation time.
    /// When provided, struct field access via a column reference can locate field positions by name
    /// without scanning the AST at runtime.
    /// </param>
    /// <param name="letBindingExpressions">
    /// Optional map of LET binding names to their source expressions. Used as a fallback when
    /// struct field access cannot be resolved via <paramref name="sourceSchema"/> — if the
    /// referenced binding's expression is a struct literal, field positions are recovered from
    /// the AST. Enables named destructuring of struct literals through hidden bindings.
    /// </param>
    /// <param name="store">
    /// Optional persistent value store used for per-evaluator caches (cast target names, IN
    /// literal sets) and as the default source/target arena for the <see cref="Row"/>-only
    /// <see cref="Evaluate(Expression, Row)"/> overload. For true two-arena evaluation use the
    /// <see cref="EvaluationFrame"/>-based overloads.
    /// </param>
    /// <param name="sidecarRegistry">
    /// Optional registry for resolving <c>FlagInSidecar</c> DataValues (LBO payloads stored in
    /// <c>.datum-blob</c> sidecars). Threaded into both the simple <see cref="Evaluate(Expression, Row)"/>
    /// frame and the per-call <see cref="InvocationFrame"/> built for scalar dispatch so image /
    /// byte-array functions can resolve sidecar-backed values.
    /// </param>
    /// <param name="variableScope">
    /// Optional procedural variable scope chain. When non-<see langword="null"/>,
    /// <see cref="VariableExpression"/> references resolve via this chain;
    /// otherwise referencing <c>@var</c> throws.
    /// </param>
    /// <param name="variableStore">
    /// Optional procedure-lifetime store paired with <paramref name="variableScope"/>:
    /// the source arena from which bound variable payloads are stabilised on read.
    /// Both must be supplied (or both omitted) for the variable path to work.
    /// </param>
    /// <param name="typeRegistry">
    /// Optional per-query type registry. When provided, struct literals stamp a type-id
    /// at construction and struct field access uses the registry as the primary resolution
    /// path before falling back to schema/AST-based resolution.
    /// </param>
    public ExpressionEvaluator(
        FunctionRegistry functions,
        QueryMeter? meter = null,
        Row? outerRow = null,
        Schema? sourceSchema = null,
        IReadOnlyDictionary<string, Expression>? letBindingExpressions = null,
        IValueStore? store = null,
        SidecarRegistry? sidecarRegistry = null,
        VariableScope? variableScope = null,
        IValueStore? variableStore = null,
        TypeRegistry? typeRegistry = null)
    {
        _functions = functions;
        _meter = meter;
        _store = store;
        _outerRow = outerRow;
        _sourceSchema = sourceSchema;
        _letBindingExpressions = letBindingExpressions;
        _sidecarRegistry = sidecarRegistry;
        _variableScope = variableScope;
        _variableStore = variableStore;
        _typeRegistry = typeRegistry;
    }

    /// <summary>
    /// Convenience constructor that pulls every shared dependency from <paramref name="context"/> —
    /// the function registry, query meter, default value store, outer row, and sidecar registry.
    /// Operator-specific extras (<paramref name="sourceSchema"/>, <paramref name="letBindingExpressions"/>)
    /// stay explicit since they're not on the context.
    /// </summary>
    /// <param name="context">Execution context the evaluator runs under.</param>
    /// <param name="sourceSchema">See the field-based constructor.</param>
    /// <param name="letBindingExpressions">See the field-based constructor.</param>
    public ExpressionEvaluator(
        ExecutionContext context,
        Schema? sourceSchema = null,
        IReadOnlyDictionary<string, Expression>? letBindingExpressions = null)
        : this(
            context.FunctionRegistry,
            context.QueryMeter,
            context.OuterRow,
            sourceSchema,
            letBindingExpressions,
            context.Store,
            context.SidecarRegistry,
            context.VariableScope,
            context.VariableStore,
            context.Types)
    {
    }

    /// <summary>Resolves a string DataValue against the frame's source arena.</summary>
    private static string Str(DataValue v, in EvaluationFrame frame) => v.AsString(frame.Source);

    // ──────────────────── Public entry points ────────────────────

    /// <summary>
    /// Evaluates an expression tree against the given row, using the store supplied at
    /// construction for both reads and writes. Convenience overload for callers that don't
    /// yet distinguish source and target arenas.
    /// </summary>
    public DataValue Evaluate(Expression expression, Row row)
    {
        IValueStore store = _store ?? ThrowStoreRequired();
        return Evaluate(expression, new EvaluationFrame(row, store, store, _outerRow, _sidecarRegistry));
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean, using the store
    /// supplied at construction. Convenience overload.
    /// </summary>
    public bool EvaluateAsBoolean(Expression expression, Row row)
    {
        IValueStore store = _store ?? ThrowStoreRequired();
        return EvaluateAsBoolean(expression, new EvaluationFrame(row, store, store, _outerRow, _sidecarRegistry));
    }

    private static IValueStore ThrowStoreRequired() =>
        throw new InvalidOperationException(
            "ExpressionEvaluator was constructed without a store; use the EvaluationFrame overload or supply a store.");

    /// <summary>
    /// Evaluates an expression tree against the given frame and returns the result.
    /// </summary>
    /// <param name="expression">The AST expression to evaluate.</param>
    /// <param name="frame">Row + arenas + outer row for this evaluation.</param>
    /// <returns>The computed result.</returns>
    public DataValue Evaluate(Expression expression, in EvaluationFrame frame)
    {
        try
        {
            return expression switch
            {
                // Hoisted literals: produced by LiteralHoister before execution so the
                // DataValue is already materialized. Zero-cost read compared to the
                // switch-on-CLR-type + FromX() path taken by LiteralExpression below.
                LiteralValueExpression hoisted => hoisted.Value,
                LiteralExpression literal => EvaluateLiteral(literal, frame),
                ColumnReference column => EvaluateColumn(column, frame),
                BinaryExpression binary => EvaluateBinary(binary, frame),
                UnaryExpression unary => EvaluateUnary(unary, frame),
                FunctionCallExpression function => EvaluateFunction(function, frame),
                InExpression inExpr => EvaluateIn(inExpr, frame),
                BetweenExpression between => EvaluateBetween(between, frame),
                IsNullExpression isNull => EvaluateIsNull(isNull, frame),
                CastExpression cast => EvaluateCast(cast, frame),
                AtTimeZoneExpression atz => EvaluateAtTimeZone(atz, frame),
                CaseExpression caseExpr => EvaluateCase(caseExpr, frame),
                LikeExpression like => EvaluateLikeEscape(like, frame),
                WindowFunctionCallExpression window => throw new InvalidOperationException(
                    $"Window function '{window.FunctionName}' was not rewritten by the query planner. " +
                    "Window functions must be used with an OVER clause and are only allowed in SELECT and ORDER BY."),
                ScanExpression => throw new InvalidOperationException(
                    "SCAN expression was not rewritten by the query planner. " +
                    "SCAN expressions must appear in SELECT or LET bindings."),
                SubqueryExpression => throw new InvalidOperationException(
                    "Subquery expression was not rewritten by the query planner."),
                InSubqueryExpression => throw new InvalidOperationException(
                    "IN (SELECT ...) was not rewritten by the query planner into a semi-join."),
                ExistsExpression => throw new InvalidOperationException(
                    "[NOT] EXISTS (SELECT ...) was not rewritten by the query planner into a semi-join."),
                CurrentTimestampExpression ct => EvaluateTemporalConstant(ct),
                ParameterExpression parameter => throw new InvalidOperationException(
                    $"Unbound parameter '${parameter.Name}'. Parameters must be bound before evaluation."),
                VariableExpression variable => EvaluateVariable(variable, frame),
                LambdaExpression => throw new InvalidOperationException(
                    "Lambda expressions cannot be evaluated as standalone values. " +
                    "They must appear as arguments to higher-order functions such as array_transform or array_filter."),
                StructLiteralExpression structLiteral => EvaluateStructLiteral(structLiteral, frame),
                IndexAccessExpression indexAccess => EvaluateIndexAccess(indexAccess, frame),
                TypeLiteralExpression typeLiteral => EvaluateTypeLiteral(typeLiteral),
                _ => throw new InvalidOperationException(
                    $"Unsupported expression type: {expression.GetType().Name}.")
            };
        }
        catch (Exception ex) when (ex is not ExpressionEvaluationException)
        {
            SourceSpan? span = expression.TryGetSourceSpan();
            if (span is not null)
            {
                throw new ExpressionEvaluationException(
                    $"[Line {span.Line}, Col {span.Column}] {ex.Message}", span, ex);
            }

            throw;
        }
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean (truthy/falsy).
    /// Null is treated as false. Scalar 0 is false; non-zero is true.
    /// </summary>
    public bool EvaluateAsBoolean(Expression expression, in EvaluationFrame frame)
    {
        ValueRef result = EvaluateAsValueRef(expression, frame);

        if (result.IsNull)
        {
            return false;
        }

        return result.Kind switch
        {
            DataKind.Boolean => result.AsBoolean(),
            DataKind.Float32 => result.AsFloat32() != 0f,
            DataKind.Float64 => result.AsFloat64() != 0.0,
            DataKind.UInt8 => result.AsUInt8() != 0,
            DataKind.Int8 => result.AsInt8() != 0,
            DataKind.Int16 => result.AsInt16() != 0,
            DataKind.UInt16 => result.AsUInt16() != 0,
            DataKind.Int32 => result.AsInt32() != 0,
            DataKind.UInt32 => result.AsUInt32() != 0,
            DataKind.Int64 => result.AsInt64() != 0,
            DataKind.UInt64 => result.AsUInt64() != 0,
            DataKind.String => !string.IsNullOrEmpty(result.AsString()),
            _ => true,
        };
    }

    private DataValue EvaluateLiteral(LiteralExpression literal, in EvaluationFrame frame)
    {
        if (literal.Value is null)
        {
            return DataValue.UnknownNull();
        }

        return literal.Value switch
        {
            DataValue dataValue => dataValue,
            sbyte int8Value => DataValue.FromInt8(int8Value),
            short int16Value => DataValue.FromInt16(int16Value),
            int intValue => DataValue.FromInt32(intValue),
            long longValue => DataValue.FromInt64(longValue),
            float floatValue => DataValue.FromFloat32(floatValue),
            double doubleValue => DataValue.FromFloat64(doubleValue),
            string stringValue => DataValue.FromString(stringValue, frame.Target),
            bool boolValue => DataValue.FromBoolean(boolValue),
            _ => throw new InvalidOperationException(
                $"Unsupported literal type: {literal.Value.GetType().Name}."),
        };
    }

    private static DataValue EvaluateTypeLiteral(TypeLiteralExpression typeLiteral)
    {
        if (!Enum.TryParse<DataKind>(typeLiteral.TypeName, ignoreCase: true, out DataKind kind))
        {
            throw new InvalidOperationException(
                $"Unknown type name: '{typeLiteral.TypeName}'.");
        }

        return DataValue.FromType(kind);
    }

    /// <summary>
    /// Validates a scalar function call's argument kinds via
    /// <see cref="IScalarFunction.ValidateArguments"/>. On failure, wraps the function's
    /// argument exception with the call site's source span and rethrows as an
    /// <see cref="ExpressionEvaluationException"/> so the user sees a clean
    /// <c>[Line N, Col C] foo(): expects ...</c> error.
    /// </summary>
    private static void ValidateScalarCallSiteOrThrow(
        IScalarFunction scalarFunction,
        FunctionCallExpression function,
        ReadOnlySpan<ValueRef> arguments)
    {
        DataKind[] argumentKinds = ArrayPool<DataKind>.Shared.Rent(arguments.Length);
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                argumentKinds[i] = arguments[i].Kind;
            }

            try
            {
                scalarFunction.ValidateArguments(argumentKinds.AsSpan(0, arguments.Length));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FunctionArgumentException)
            {
                SourceSpan? span = function.Span;
                string prefix = span is not null
                    ? $"[Line {span.Line}, Col {span.Column}] "
                    : string.Empty;
                throw new ExpressionEvaluationException($"{prefix}{ex.Message}", span, ex);
            }
        }
        finally
        {
            ArrayPool<DataKind>.Shared.Return(argumentKinds);
        }
    }

    /// <summary>
    /// Fallback evaluation for <see cref="CurrentTimestampExpression"/> when the
    /// <see cref="TemporalConstantFolder"/> pass has not been applied (e.g. direct
    /// programmatic API usage). Uses <see cref="DateTimeOffset.UtcNow"/> as the clock.
    /// </summary>
    private static DataValue EvaluateTemporalConstant(CurrentTimestampExpression ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return ct.Kind switch
        {
            CurrentTimestampKind.CurrentDate => DataValue.FromDate(DateOnly.FromDateTime(now.UtcDateTime)),
            CurrentTimestampKind.CurrentTime => DataValue.FromTime(TimeOnly.FromTimeSpan(now.TimeOfDay)),
            CurrentTimestampKind.CurrentTimestamp => DataValue.FromDateTime(now),
            _ => throw new InvalidOperationException($"Unknown CurrentTimestampKind: {ct.Kind}"),
        };
    }

    /// <summary>
    /// Resolves a <c>VariableExpression</c> against the procedural variable
    /// scope chain. Walks innermost-first, then stabilises the bound value
    /// from <see cref="_variableStore"/> into <see cref="EvaluationFrame.Target"/>
    /// so downstream operations read it the same way they read any other
    /// arena-backed value. Throws if the evaluator wasn't constructed with
    /// a scope (i.e. the query is running outside a procedural batch) or
    /// if the variable isn't bound in any enclosing frame.
    /// </summary>
    private DataValue EvaluateVariable(VariableExpression variable, in EvaluationFrame frame)
    {
        if (_variableScope is null || _variableStore is null)
        {
            throw new InvalidOperationException(
                $"Variable '@{variable.Name}' referenced outside a procedural batch — only DECLARE / SET / IF / WHILE / FOR statements (and the queries inside them) can resolve variables.");
        }

        if (!_variableScope.TryGet(variable.Name, out DataValue value))
        {
            throw new InvalidOperationException(
                $"Variable '@{variable.Name}' is not declared in any enclosing scope.");
        }

        // Stabilise from the procedure-lifetime variable store into the
        // frame's target arena. The variable scope holds a value with
        // offsets in _variableStore; downstream code reads against
        // frame.Source (or frame.Target when a result is expected). One
        // copy per read isolates the multi-store concern to this branch.
        return DataValueRetention.Stabilize(value, _variableStore, frame.Target);
    }

    private static DataValue EvaluateColumn(ColumnReference column, in EvaluationFrame frame)
    {
        Row row = frame.Row;

        // For qualified references (table.column), try the full qualified name first,
        // then the unqualified column name.
        if (column.QualifiedName is not null)
        {
            if (row.TryGetValue(column.QualifiedName, out DataValue qualifiedValue))
            {
                return qualifiedValue;
            }
        }

        if (row.TryGetValue(column.ColumnName, out DataValue value))
        {
            return value;
        }

        // Fall back to the outer row for correlated subquery column resolution.
        if (frame.OuterRow is Row outerRow)
        {
            if (column.QualifiedName is not null &&
                outerRow.TryGetValue(column.QualifiedName, out DataValue outerQualifiedValue))
            {
                return outerQualifiedValue;
            }

            if (outerRow.TryGetValue(column.ColumnName, out DataValue outerValue))
            {
                return outerValue;
            }
        }

        throw new InvalidOperationException(
            column.TableName is not null
                ? $"Column '{column.TableName}.{column.ColumnName}' not found in row."
                : $"Column '{column.ColumnName}' not found in row.");
    }

    private DataValue EvaluateBinary(BinaryExpression binary, in EvaluationFrame frame) =>
        ToDataValue(EvaluateBinaryAsValueRef(binary, frame), frame);

    /// <summary>
    /// ValueRef-native binary expression evaluation. Operands are pulled in as
    /// ValueRef (not DataValue), so predicate-context callers consume the
    /// resulting boolean without any function-result string ever crossing the
    /// arena boundary. Result is always inline (Boolean / Float32 / Duration).
    /// </summary>
    private ValueRef EvaluateBinaryAsValueRef(BinaryExpression binary, in EvaluationFrame frame)
    {
        // Short-circuit for AND/OR — uses EvaluateAsBoolean which itself routes
        // through EvaluateAsValueRef.
        if (binary.Operator == BinaryOperator.And)
        {
            if (!EvaluateAsBoolean(binary.Left, frame)) return ValueRef.FromBoolean(false);
            if (!EvaluateAsBoolean(binary.Right, frame)) return ValueRef.FromBoolean(false);
            return ValueRef.FromBoolean(true);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            if (EvaluateAsBoolean(binary.Left, frame)) return ValueRef.FromBoolean(true);
            if (EvaluateAsBoolean(binary.Right, frame)) return ValueRef.FromBoolean(true);
            return ValueRef.FromBoolean(false);
        }

        ValueRef left = EvaluateAsValueRef(binary.Left, frame);
        ValueRef right = EvaluateAsValueRef(binary.Right, frame);

        if (left.IsNull || right.IsNull)
        {
            if (binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                or BinaryOperator.LessThan or BinaryOperator.GreaterThan
                or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThanOrEqual
                or BinaryOperator.Like or BinaryOperator.ILike or BinaryOperator.Regexp
                or BinaryOperator.And or BinaryOperator.Or)
            {
                return ValueRef.Null(DataKind.Boolean);
            }

            // Arithmetic NULL propagation. Use the promoted result kind so the
            // null carries the same type tag the non-null path would have. For
            // a NULL operand we still know the OTHER operand's kind (or both
            // kinds if both are typed-NULL), so promotion is well-defined.
            // Falls back to Float32 when promotion can't be determined — same
            // shape the old code always emitted.
            DataKind nullKind = TryPromoteArithmeticKind(left.Kind, right.Kind, binary.Operator)
                ?? DataKind.Float32;
            return ValueRef.Null(nullKind);
        }

        return binary.Operator switch
        {
            BinaryOperator.Add when left.Kind == DataKind.Duration && right.Kind == DataKind.Duration
                => ValueRef.FromDuration(left.AsDuration() + right.AsDuration()),
            BinaryOperator.Subtract when left.Kind == DataKind.Duration && right.Kind == DataKind.Duration
                => ValueRef.FromDuration(left.AsDuration() - right.AsDuration()),
            BinaryOperator.Add => DispatchArithmetic(left, right, BinaryOperator.Add),
            BinaryOperator.Subtract => DispatchArithmetic(left, right, BinaryOperator.Subtract),
            BinaryOperator.Multiply => DispatchArithmetic(left, right, BinaryOperator.Multiply),
            BinaryOperator.Divide => DispatchArithmetic(left, right, BinaryOperator.Divide),
            BinaryOperator.Modulo => DispatchArithmetic(left, right, BinaryOperator.Modulo),
            BinaryOperator.Power => DispatchArithmetic(left, right, BinaryOperator.Power),
            BinaryOperator.Equal => CompareValuesValueRef(left, right, 0),
            BinaryOperator.NotEqual => CompareValuesValueRef(left, right, 0, negate: true),
            BinaryOperator.LessThan => CompareValuesValueRef(left, right, -1),
            BinaryOperator.GreaterThan => CompareValuesValueRef(left, right, 1),
            BinaryOperator.LessThanOrEqual => CompareValuesLeValueRef(left, right),
            BinaryOperator.GreaterThanOrEqual => CompareValuesGeValueRef(left, right),
            BinaryOperator.Like => EvaluateLikeValueRef(left, right),
            BinaryOperator.ILike => EvaluateILikeValueRef(left, right),
            BinaryOperator.Regexp => EvaluateRegexpValueRef(left, right),
            _ => throw new InvalidOperationException(
                $"Unsupported binary operator: {binary.Operator}."),
        };
    }

    private DataValue EvaluateUnary(UnaryExpression unary, in EvaluationFrame frame) =>
        ToDataValue(EvaluateUnaryAsValueRef(unary, frame), frame);

    /// <summary>
    /// ValueRef-native unary expression evaluation. Result is always inline
    /// (Boolean for NOT, Float32 for negate).
    /// </summary>
    private ValueRef EvaluateUnaryAsValueRef(UnaryExpression unary, in EvaluationFrame frame)
    {
        ValueRef operand = EvaluateAsValueRef(unary.Operand, frame);

        if (operand.IsNull)
        {
            return unary.Operator == UnaryOperator.Not
                ? ValueRef.Null(DataKind.Boolean)
                : ValueRef.Null(NegateResultKind(operand.Kind));
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => ValueRef.FromBoolean(!EvaluateAsBoolean(unary.Operand, frame)),
            UnaryOperator.Negate => NegatePreservingKind(operand),
            _ => throw new InvalidOperationException(
                $"Unsupported unary operator: {unary.Operator}."),
        };
    }

    /// <summary>
    /// Result kind of unary <c>-</c> on <paramref name="operandKind"/>.
    /// Mirrors the binary-arithmetic widening rules: small integers
    /// widen to Int32, Int64/UInt64 stay at Int64, 128-bit operands
    /// stay at Int128, floats and Decimal preserve their kind. Non-
    /// numeric operands fall back to Float32 (matches the prior
    /// "always Float32" shape so legacy callers don't surprise).
    /// </summary>
    private static DataKind NegateResultKind(DataKind operandKind) => operandKind switch
    {
        DataKind.Int8 or DataKind.UInt8
            or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32
            or DataKind.Boolean
            => DataKind.Int32,
        DataKind.UInt32 or DataKind.Int64 or DataKind.UInt64 => DataKind.Int64,
        DataKind.Int128 or DataKind.UInt128 => DataKind.Int128,
        DataKind.Float16 or DataKind.Float32 => DataKind.Float32,
        DataKind.Float64 => DataKind.Float64,
        DataKind.Decimal => DataKind.Decimal,
        _ => DataKind.Float32,
    };

    private static ValueRef NegatePreservingKind(ValueRef operand) => NegateResultKind(operand.Kind) switch
    {
        DataKind.Int32 => ValueRef.FromInt32(unchecked(-ToInt32Promoted(operand))),
        DataKind.Int64 => ValueRef.FromInt64(unchecked(-ToInt64Promoted(operand))),
        DataKind.Int128 => ValueRef.FromInt128(unchecked(-ToInt128Promoted(operand))),
        DataKind.Float64 => ValueRef.FromFloat64(-ToDoubleValueRef(operand)),
        DataKind.Decimal => ValueRef.FromDecimal(-ToDecimalPromoted(operand)),
        _ => ValueRef.FromFloat32(-ToFloatValueRef(operand)),
    };

    private DataValue EvaluateFunction(FunctionCallExpression function, in EvaluationFrame frame) =>
        ToDataValue(EvaluateFunctionAsValueRef(function, frame), frame);

    /// <summary>
    /// Evaluates a function call directly as a <see cref="ValueRef"/>. Used as
    /// the inner step of nested function chains so intermediate values stay in
    /// managed memory rather than round-tripping through the arena: in
    /// <c>outer(middle(inner(x)))</c>, only <c>outer</c>'s top-level result
    /// crosses the <see cref="ToDataValue"/> boundary.
    /// </summary>
    private ValueRef EvaluateFunctionAsValueRef(FunctionCallExpression function, in EvaluationFrame frame)
    {
        IScalarFunction? scalarFunction = _functions.TryGetScalar(function.FunctionName);

        if (scalarFunction is null)
        {
            throw new InvalidOperationException(
                $"Unknown function: '{function.FunctionName}'.");
        }

        int argumentCount = function.Arguments.Count;
        ValueRef[] arguments = ArrayPool<ValueRef>.Shared.Rent(argumentCount);
        try
        {
            for (int index = 0; index < argumentCount; index++)
            {
                arguments[index] = EvaluateAsValueRef(function.Arguments[index], frame);
            }

            if (_validatedScalarCalls.Add(function))
            {
                ValidateScalarCallSiteOrThrow(scalarFunction, function, arguments.AsSpan(0, argumentCount));
            }

            ValueRef result = scalarFunction.Execute(arguments.AsSpan(0, argumentCount), in frame);
            _meter?.Add(scalarFunction.QueryUnitCost);
            return result;
        }
        finally
        {
            // Clear references so the pool doesn't root managed payloads.
            arguments.AsSpan(0, argumentCount).Clear();
            ArrayPool<ValueRef>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Evaluates an expression as a <see cref="ValueRef"/>. Function call
    /// expressions short-circuit to <see cref="EvaluateFunctionAsValueRef"/>
    /// to keep nested chains in managed memory; everything else falls back to
    /// the existing <see cref="Evaluate(Expression, in EvaluationFrame)"/>
    /// path and lifts the result via <see cref="ToValueRef"/>.
    /// </summary>
    /// <remarks>
    /// This matters for <c>outer(middle(inner(x)))</c>-style chains: each
    /// recursive call into <see cref="EvaluateFunctionAsValueRef"/> produces a
    /// managed <see cref="ValueRef"/> the next stage consumes directly, so the
    /// only arena write is the outermost call's <see cref="ToDataValue"/>.
    /// Earlier intermediates become unreachable as soon as the next stage's
    /// result is constructed and are reclaimed by the GC.
    /// </remarks>
    public ValueRef EvaluateAsValueRef(Expression expression, in EvaluationFrame frame)
    {
        // Predicate-relevant expression types route through ValueRef-native
        // handlers so no intermediate result writes to the arena. Anything else
        // falls back to the DataValue path and lifts via ToValueRef — those
        // expression types either produce inline values (literals, etc.) where
        // the lift is free, or they're rare enough in predicate contexts that
        // the optimisation isn't worth duplicating their handlers.
        switch (expression)
        {
            case FunctionCallExpression functionCall:
                return EvaluateFunctionAsValueRef(functionCall, frame);
            case BinaryExpression binary:
                return EvaluateBinaryAsValueRef(binary, frame);
            case UnaryExpression unary:
                return EvaluateUnaryAsValueRef(unary, frame);
            case IsNullExpression isNull:
                return EvaluateIsNullAsValueRef(isNull, frame);
        }

        DataValue raw = Evaluate(expression, frame);
        return ToValueRef(raw, frame);
    }

    /// <summary>
    /// Materialises a <see cref="DataValue"/> argument into a
    /// <see cref="ValueRef"/>: arena-backed strings/arrays are read into managed
    /// payloads, sidecar-backed values are loaded via the registry, and inline
    /// values pass through unchanged.
    /// </summary>
    private static ValueRef ToValueRef(DataValue value, in EvaluationFrame frame)
    {
        if (value.IsNull)
        {
            return ValueRef.Null(value.Kind);
        }

        if (value.IsInline)
        {
            return ValueRef.FromInline(value);
        }

        // Non-inline: resolve managed payload from source store / sidecar.
        // Byte arrays (UInt8 + IsArray) and Image both carry byte content.
        if (value.IsByteArrayKind || value.Kind == DataKind.Image)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(frame.Source, frame.SidecarRegistry);
            return ValueRef.FromBytes(value.Kind, bytes.ToArray(), isArray: value.IsByteArrayKind);
        }

        return value.Kind switch
        {
            DataKind.String =>
                ValueRef.FromString(value.AsString(frame.Source, frame.SidecarRegistry)),
            _ => throw new InvalidOperationException(
                $"Cannot convert non-inline DataValue of kind {value.Kind} into a ValueRef. "
                + "Add support to ExpressionEvaluator.ToValueRef when this kind reaches the function boundary."),
        };
    }

    /// <summary>
    /// Lowers a function-result <see cref="ValueRef"/> back into a
    /// <see cref="DataValue"/> against <paramref name="frame"/>'s target arena.
    /// Thin wrapper around <see cref="ValueRef.ToDataValue"/> that picks the
    /// frame's target store; the recursion for struct/array values is
    /// handled by ValueRef itself.
    /// </summary>
    private static DataValue ToDataValue(ValueRef value, in EvaluationFrame frame) =>
        value.ToDataValue(frame.Target);

    private DataValue EvaluateIn(InExpression inExpr, in EvaluationFrame frame)
    {
        DataValue target = Evaluate(inExpr.Expression, frame);

        if (target.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        // Fast path: when all values are literals (e.g. constant-folded from an
        // uncorrelated IN subquery), build a HashSet once and do O(1) lookups
        // instead of O(n) linear scans on every row.
        if (TryGetOrBuildLiteralValueSet(inExpr, frame, out HashSet<DataValue> valueSet, out bool hasNullCandidate))
        {
            bool found = valueSet.Contains(target);

            // When types differ (e.g. Float64 column vs Int32 literals), HashSet
            // uses strict DataValue equality which is kind-sensitive. Fall back to
            // numeric comparison for cross-type matches.
            if (!found)
            {
                foreach (DataValue candidate in valueSet)
                {
                    if (CompareDataValues(target, candidate, frame) == 0)
                    {
                        found = true;
                        break;
                    }
                }
            }

            if (found)
            {
                return DataValue.FromBoolean(!inExpr.Negated);
            }

            if (hasNullCandidate)
            {
                return DataValue.Null(DataKind.Boolean);
            }

            return DataValue.FromBoolean(inExpr.Negated);
        }

        // Slow path: values contain non-literal expressions that depend on the row.
        return EvaluateInLinear(inExpr, target, frame);
    }

    /// <summary>
    /// Attempts to retrieve or build a cached <see cref="HashSet{T}"/> of literal values
    /// for the given <see cref="InExpression"/>. Returns <see langword="false"/> if any
    /// value is not a <see cref="LiteralExpression"/>, indicating the linear path is needed.
    /// The cache uses the evaluator's persistent <c>_store</c> so entries outlive any batch.
    /// </summary>
    private bool TryGetOrBuildLiteralValueSet(
        InExpression inExpr,
        in EvaluationFrame frame,
        out HashSet<DataValue> valueSet,
        out bool hasNull)
    {
        if (_inValueSetCache.TryGetValue(inExpr, out (HashSet<DataValue> NonNullValues, bool HasNull) cached))
        {
            valueSet = cached.NonNullValues;
            hasNull = cached.HasNull;
            return true;
        }

        HashSet<DataValue> set = new();
        bool anyNull = false;

        // Materialize literal values into the persistent store so the cache entries
        // remain valid across batches. Falls back to the frame's target arena when
        // no persistent store is configured.
        IValueStore cacheStore = _store ?? frame.Target;
        EvaluationFrame cacheFrame = new(frame.Row, frame.Source, cacheStore, frame.OuterRow, frame.SidecarRegistry);

        foreach (Expression valueExpression in inExpr.Values)
        {
            if (valueExpression is not (LiteralExpression or LiteralValueExpression))
            {
                valueSet = null!;
                hasNull = false;
                return false;
            }

            DataValue value = valueExpression switch
            {
                LiteralValueExpression lv => lv.Value,
                LiteralExpression le => EvaluateLiteral(le, cacheFrame),
                _ => throw new InvalidOperationException("unreachable"),
            };
            if (value.IsNull)
            {
                anyNull = true;
            }
            else
            {
                set.Add(value);
            }
        }

        _inValueSetCache[inExpr] = (set, anyNull);
        valueSet = set;
        hasNull = anyNull;
        return true;
    }

    /// <summary>
    /// Linear-scan fallback for IN expressions with non-literal values.
    /// </summary>
    private DataValue EvaluateInLinear(InExpression inExpr, DataValue target, in EvaluationFrame frame)
    {
        bool hasNullCandidate = false;

        foreach (Expression valueExpression in inExpr.Values)
        {
            DataValue candidate = Evaluate(valueExpression, frame);
            if (candidate.IsNull)
            {
                hasNullCandidate = true;
                continue;
            }

            if (CompareDataValues(target, candidate, frame) == 0)
            {
                return DataValue.FromBoolean(!inExpr.Negated);
            }
        }

        // SQL three-valued logic: if no match was found but a NULL candidate
        // existed, the result is UNKNOWN (NULL) rather than a definite answer.
        // For NOT IN, this means rows are filtered out by EvaluateAsBoolean.
        if (hasNullCandidate)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        return DataValue.FromBoolean(inExpr.Negated);
    }

    private DataValue EvaluateBetween(BetweenExpression between, in EvaluationFrame frame)
    {
        DataValue target = Evaluate(between.Expression, frame);
        DataValue low = Evaluate(between.Low, frame);
        DataValue high = Evaluate(between.High, frame);

        if (target.IsNull || low.IsNull || high.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        float targetValue = ToFloat(target);
        float lowValue = ToFloat(low);
        float highValue = ToFloat(high);

        bool inRange = targetValue >= lowValue && targetValue <= highValue;
        if (between.Negated)
        {
            inRange = !inRange;
        }

        return DataValue.FromBoolean(inRange);
    }

    private DataValue EvaluateIsNull(IsNullExpression isNull, in EvaluationFrame frame) =>
        ToDataValue(EvaluateIsNullAsValueRef(isNull, frame), frame);

    /// <summary>
    /// ValueRef-native IS NULL check. Avoids reading the inner expression's
    /// payload from the arena when the predicate only needs a null/non-null bit.
    /// </summary>
    private ValueRef EvaluateIsNullAsValueRef(IsNullExpression isNull, in EvaluationFrame frame)
    {
        ValueRef value = EvaluateAsValueRef(isNull.Expression, frame);
        bool result = value.IsNull;
        if (isNull.Negated)
        {
            result = !result;
        }
        return ValueRef.FromBoolean(result);
    }

    private readonly Dictionary<string, TimeZoneInfo> _timeZoneCache = new(StringComparer.OrdinalIgnoreCase);

    private DataValue EvaluateCast(CastExpression cast, in EvaluationFrame frame)
    {
        IScalarFunction? castFunction = _functions.TryGetScalar("cast");
        if (castFunction is null)
        {
            throw new InvalidOperationException("Cast function not registered.");
        }

        ValueRef[] arguments = ArrayPool<ValueRef>.Shared.Rent(2);
        try
        {
            arguments[0] = EvaluateAsValueRef(cast.Expression, frame);
            arguments[1] = ValueRef.FromString(cast.TargetType);
            ValueRef result = castFunction.Execute(arguments.AsSpan(0, 2), in frame);
            _meter?.Add(castFunction.QueryUnitCost);
            return ToDataValue(result, frame);
        }
        finally
        {
            arguments.AsSpan(0, 2).Clear();
            ArrayPool<ValueRef>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Evaluates an AT TIME ZONE expression by converting the DateTimeOffset to the
    /// specified IANA timezone. The instant in time is preserved; only the UTC offset
    /// (and therefore the displayed local time) changes.
    /// </summary>
    private DataValue EvaluateAtTimeZone(AtTimeZoneExpression atz, in EvaluationFrame frame)
    {
        DataValue value = Evaluate(atz.Expression, frame);

        if (value.IsNull)
        {
            return DataValue.Null(DataKind.DateTime);
        }

        DataValue tzValue = Evaluate(atz.TimeZone, frame);
        string tzName = Str(tzValue, frame);

        if (!_timeZoneCache.TryGetValue(tzName, out TimeZoneInfo? tz))
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
            _timeZoneCache[tzName] = tz;
        }

        DateTimeOffset converted = TimeZoneInfo.ConvertTime(value.ToDateTimeOffset(), tz);
        return DataValue.FromDateTime(converted);
    }

    /// <summary>
    /// Evaluates a CASE expression with short-circuit semantics and implicit
    /// branch type coercion. On first evaluation, resolves the common output type
    /// across all branches; subsequent evaluations reuse the cached result.
    /// Simple CASE compares the operand against each WHEN value for equality.
    /// Searched CASE evaluates each WHEN condition as a boolean predicate.
    /// Only the matching THEN branch is evaluated.
    /// </summary>
    private DataValue EvaluateCase(CaseExpression caseExpression, in EvaluationFrame frame)
    {
        DataValue result = EvaluateCaseBranch(caseExpression, frame);

        // Resolve target kind once per CaseExpression and coerce the result.
        if (!_caseResolvedKindCache.TryGetValue(caseExpression, out DataKind? resolvedKind))
        {
            resolvedKind = ResolveCaseTargetKind(caseExpression, frame);
            _caseResolvedKindCache[caseExpression] = resolvedKind;
        }

        if (resolvedKind is not null && !result.IsNull && result.Kind != resolvedKind.Value)
        {
            return TypeCoercion.CoerceValue(result, resolvedKind.Value);
        }

        // Ensure typed nulls match the resolved kind so output writers see consistent types.
        if (resolvedKind is not null && result.IsNull && result.Kind != resolvedKind.Value)
        {
            return DataValue.Null(resolvedKind.Value);
        }

        return result;
    }

    /// <summary>
    /// Evaluates the matching CASE branch without coercion — pure short-circuit logic.
    /// </summary>
    private DataValue EvaluateCaseBranch(CaseExpression caseExpression, in EvaluationFrame frame)
    {
        if (caseExpression.Operand is not null)
        {
            // Simple CASE: compare operand against each WHEN value.
            DataValue operand = Evaluate(caseExpression.Operand, frame);

            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                DataValue whenValue = Evaluate(whenClause.Condition, frame);
                if (!operand.IsNull && !whenValue.IsNull && CompareDataValues(operand, whenValue, frame) == 0)
                {
                    return Evaluate(whenClause.Result, frame);
                }
            }
        }
        else
        {
            // Searched CASE: evaluate each WHEN condition as boolean.
            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                if (EvaluateAsBoolean(whenClause.Condition, frame))
                {
                    return Evaluate(whenClause.Result, frame);
                }
            }
        }

        // No match: return ELSE result or typed null.
        if (caseExpression.ElseResult is not null)
        {
            return Evaluate(caseExpression.ElseResult, frame);
        }

        return DataValue.Null(DataKind.Float32);
    }

    /// <summary>
    /// Resolves the target DataKind for a CASE expression by building a schema from
    /// the current row and delegating to <see cref="ExpressionTypeResolver"/>.
    /// Falls back to AST-level inference when a row-derived schema cannot be built.
    /// </summary>
    private DataKind? ResolveCaseTargetKind(CaseExpression caseExpression, in EvaluationFrame frame)
    {
        Row row = frame.Row;

        // Build a schema from the row so column references resolve to their actual types.
        if (row.FieldCount > 0)
        {
            ColumnInfo[] columns = new ColumnInfo[row.FieldCount];
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            bool hasDuplicates = false;

            for (int index = 0; index < row.FieldCount; index++)
            {
                string name = row.ColumnNames[index];
                if (!seen.Add(name))
                {
                    hasDuplicates = true;
                    break;
                }

                columns[index] = new ColumnInfo(name, row[index].Kind, row[index].IsNull);
            }

            if (!hasDuplicates)
            {
                Schema rowSchema = new(columns);
                return ExpressionTypeResolver.ResolveType(caseExpression, rowSchema, _functions);
            }
        }

        // Fallback: AST-level inference without schema (handles literal-only branches).
        return InferCaseExpressionKind(caseExpression);
    }

    /// <summary>
    /// Lightweight CASE type inference from the AST alone, without schema.
    /// Handles the common scenario of literal-mixed branches.
    /// Returns null when any branch type cannot be determined.
    /// </summary>
    private DataKind? InferCaseExpressionKind(CaseExpression caseExpression)
    {
        DataKind? commonKind = null;

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            DataKind? branchKind = InferLiteralExpressionKind(whenClause.Result);
            if (branchKind is null)
            {
                return null;
            }

            commonKind = commonKind is null
                ? branchKind
                : ExpressionTypeResolver.UnifyCaseBranchKinds(commonKind.Value, branchKind.Value);

            if (commonKind is null)
            {
                return null;
            }
        }

        if (caseExpression.ElseResult is not null)
        {
            DataKind? elseKind = InferLiteralExpressionKind(caseExpression.ElseResult);
            if (elseKind is not null && commonKind is not null)
            {
                commonKind = ExpressionTypeResolver.UnifyCaseBranchKinds(commonKind.Value, elseKind.Value);
            }
        }

        return commonKind;
    }

    /// <summary>
    /// Infers the DataKind of an expression from its AST structure alone.
    /// Returns null for expressions that require schema context (column references).
    /// </summary>
    private static DataKind? InferLiteralExpressionKind(Expression expression)
    {
        return expression switch
        {
            LiteralValueExpression lv => lv.Value.IsNull ? DataKind.Unknown : lv.Value.Kind,
            LiteralExpression { Value: string } => DataKind.String,
            LiteralExpression { Value: sbyte } => DataKind.Int8,
            LiteralExpression { Value: short } => DataKind.Int16,
            LiteralExpression { Value: int } => DataKind.Int32,
            LiteralExpression { Value: long } => DataKind.Int64,
            LiteralExpression { Value: float } => DataKind.Float32,
            LiteralExpression { Value: double } => DataKind.Float64,
            LiteralExpression { Value: bool } => DataKind.Boolean,
            LiteralExpression { Value: null } => DataKind.Unknown,
            CastExpression cast => ExpressionTypeResolver.ResolveCastTargetKind(cast.TargetType),
            BinaryExpression => DataKind.Float32,
            UnaryExpression => DataKind.Float32,
            InExpression => DataKind.Boolean,
            BetweenExpression => DataKind.Boolean,
            IsNullExpression => DataKind.Boolean,
            LikeExpression => DataKind.Boolean,
            _ => null,
        };
    }

    // ──────────────────── Arithmetic helpers ────────────────────

    /// <summary>
    /// Evaluates a binary arithmetic operator (<c>+ - * / % **</c>) with
    /// runtime kind promotion. Picks a target kind from the operand kinds
    /// + operator (so <c>Int64 + Int64 → Int64</c>, <c>Decimal × Float → Float64</c>,
    /// <c>5 / 2 → Float32</c> for SQL ergonomics) and applies the op in
    /// that type. Result is always inline; the only allocation is the
    /// returned <see cref="ValueRef"/> struct.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why per-row dispatch.</strong> Plan-time
    /// <see cref="ExpressionTypeResolver.ResolveType"/> reports the same
    /// promoted target kind via <see cref="TryPromoteArithmeticKind"/>,
    /// so output schemas stay in sync with the values rows carry. The
    /// runtime still computes promotion per row because evaluation has
    /// to read live operand kinds anyway — branch prediction makes the
    /// cost of a few switch arms negligible compared to the previous
    /// "everything to Float32" path that lost precision on Int64 sums
    /// and tripped hard errors on Int128 / Decimal.
    /// </para>
    /// <para>
    /// <strong>Promotion rules.</strong> See
    /// <see cref="PromoteArithmeticKind"/>. The short version: Power and
    /// Divide always return float (preserving the SQL "5 / 2 → 2.5"
    /// expectation); Decimal beats float; integer + integer stays
    /// integer at the wider operand's bit width (Int8 + Int8 widens to
    /// Int32 in C# style; Int64 stays Int64); strings get parsed as
    /// Float64 the same way they did before.
    /// </para>
    /// </remarks>
    private static ValueRef DispatchArithmetic(ValueRef left, ValueRef right, BinaryOperator op)
    {
        DataKind target = PromoteArithmeticKind(left.Kind, right.Kind, op);
        return target switch
        {
            DataKind.Int32 => ApplyInt32(left, right, op),
            DataKind.Int64 => ApplyInt64(left, right, op),
            DataKind.Int128 => ApplyInt128(left, right, op),
            DataKind.Float32 => ApplyFloat32(left, right, op),
            DataKind.Float64 => ApplyFloat64(left, right, op),
            DataKind.Decimal => ApplyDecimal(left, right, op),
            _ => throw new InvalidOperationException(
                $"Internal error: PromoteArithmeticKind returned unsupported target {target} " +
                $"for {left.Kind} {op} {right.Kind}."),
        };
    }

    /// <summary>
    /// Pure-function variant of <see cref="PromoteArithmeticKind"/> that
    /// returns null when promotion is undefined. Used by the NULL
    /// short-circuit in binary evaluation so the propagated null carries
    /// the same kind the non-null path would have produced, and by
    /// <see cref="ExpressionTypeResolver"/> for plan-time schema
    /// inference. Defers to the throwing version's logic; the only
    /// branch that matters is the "neither side resolves" fallback,
    /// where we'd rather return null than crash.
    /// </summary>
    internal static DataKind? TryPromoteArithmeticKind(DataKind left, DataKind right, BinaryOperator op)
    {
        try { return PromoteArithmeticKind(left, right, op); }
        catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Computes the promoted result kind for a binary arithmetic
    /// operator. Mirrors C# numeric promotion with SQL-ergonomic
    /// adjustments — Divide always returns float so <c>5 / 2 → 2.5</c>
    /// matches user expectation; integer arithmetic widens to Int32
    /// (matching <c>byte + byte → int</c> in C#) so small literals
    /// don't pin everything to Int8.
    /// </summary>
    internal static DataKind PromoteArithmeticKind(DataKind left, DataKind right, BinaryOperator op)
    {
        // String coercion: parse to Float64 (preserves prior behaviour).
        if (left == DataKind.String || right == DataKind.String)
        {
            return DataKind.Float64;
        }

        // Power is intrinsically float-y (MathF.Pow / Math.Pow).
        if (op == BinaryOperator.Power)
        {
            return AnyFloat64(left, right) || AnyDecimal(left, right)
                ? DataKind.Float64
                : DataKind.Float32;
        }

        // Divide: SQL ergonomics — always float so int / int → real number.
        // Users wanting truncated integer division can cast first.
        if (op == BinaryOperator.Divide)
        {
            if (AnyDecimal(left, right)) return DataKind.Decimal;
            return AnyFloat64(left, right) ? DataKind.Float64 : DataKind.Float32;
        }

        // Decimal precedence over floats and integers (preserves precision).
        if (AnyDecimal(left, right))
        {
            return DataKind.Decimal;
        }

        // Float promotion: pick the wider float when any operand is float.
        if (AnyFloat64(left, right)) return DataKind.Float64;
        if (left == DataKind.Float32 || right == DataKind.Float32) return DataKind.Float32;
        if (left == DataKind.Float16 || right == DataKind.Float16)
        {
            // Float16 + integer → Float32 for safety; Float16 + Float16 also
            // bumps to Float32 because the binary op outputs land in the
            // wider container.
            return DataKind.Float32;
        }

        // Time / Duration arithmetic falls back to float seconds for the
        // mixed cases (Duration + Duration is special-cased in the caller).
        if (left == DataKind.Time || right == DataKind.Time
            || left == DataKind.Duration || right == DataKind.Duration)
        {
            return DataKind.Float32;
        }

        // 128-bit integer preservation: any 128-bit operand pulls the result
        // into Int128. Mixed signed/unsigned 128-bit lands in Int128 too —
        // UInt128.MaxValue won't fit, but no realistic workload hits that.
        if (left is DataKind.Int128 or DataKind.UInt128
            || right is DataKind.Int128 or DataKind.UInt128)
        {
            return DataKind.Int128;
        }

        // 64-bit preservation: Int64 / UInt64 / UInt32 (which doesn't fit
        // signed 32-bit) all land in Int64.
        if (left is DataKind.Int64 or DataKind.UInt64 or DataKind.UInt32
            || right is DataKind.Int64 or DataKind.UInt64 or DataKind.UInt32)
        {
            return DataKind.Int64;
        }

        // Smaller integers + booleans: widen to Int32 (matches C# semantics).
        if (IsSmallIntegerOrBool(left) && IsSmallIntegerOrBool(right))
        {
            return DataKind.Int32;
        }

        throw new InvalidOperationException(
            $"Cannot perform {op} on operands of kinds {left} and {right}.");
    }

    private static bool AnyFloat64(DataKind l, DataKind r)
        => l == DataKind.Float64 || r == DataKind.Float64;

    private static bool AnyDecimal(DataKind l, DataKind r)
        => l == DataKind.Decimal || r == DataKind.Decimal;

    private static bool IsSmallIntegerOrBool(DataKind k) => k is
        DataKind.Boolean
        or DataKind.Int8 or DataKind.UInt8
        or DataKind.Int16 or DataKind.UInt16
        or DataKind.Int32;

    // ──────────────────── Per-target-kind apply helpers ────────────────────

    private static ValueRef ApplyInt32(ValueRef left, ValueRef right, BinaryOperator op)
    {
        int a = ToInt32Promoted(left);
        int b = ToInt32Promoted(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromInt32(unchecked(a + b)),
            BinaryOperator.Subtract => ValueRef.FromInt32(unchecked(a - b)),
            BinaryOperator.Multiply => ValueRef.FromInt32(unchecked(a * b)),
            BinaryOperator.Modulo => b != 0
                ? ValueRef.FromInt32(a % b)
                : ValueRef.Null(DataKind.Int32),
            _ => throw new InvalidOperationException(
                $"Internal error: Int32 dispatch hit for {op} (Divide/Power should have promoted to float)."),
        };
    }

    private static ValueRef ApplyInt64(ValueRef left, ValueRef right, BinaryOperator op)
    {
        long a = ToInt64Promoted(left);
        long b = ToInt64Promoted(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromInt64(unchecked(a + b)),
            BinaryOperator.Subtract => ValueRef.FromInt64(unchecked(a - b)),
            BinaryOperator.Multiply => ValueRef.FromInt64(unchecked(a * b)),
            BinaryOperator.Modulo => b != 0
                ? ValueRef.FromInt64(a % b)
                : ValueRef.Null(DataKind.Int64),
            _ => throw new InvalidOperationException(
                $"Internal error: Int64 dispatch hit for {op}."),
        };
    }

    private static ValueRef ApplyInt128(ValueRef left, ValueRef right, BinaryOperator op)
    {
        Int128 a = ToInt128Promoted(left);
        Int128 b = ToInt128Promoted(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromInt128(unchecked(a + b)),
            BinaryOperator.Subtract => ValueRef.FromInt128(unchecked(a - b)),
            BinaryOperator.Multiply => ValueRef.FromInt128(unchecked(a * b)),
            BinaryOperator.Modulo => b != Int128.Zero
                ? ValueRef.FromInt128(a % b)
                : ValueRef.Null(DataKind.Int128),
            _ => throw new InvalidOperationException(
                $"Internal error: Int128 dispatch hit for {op}."),
        };
    }

    private static ValueRef ApplyFloat32(ValueRef left, ValueRef right, BinaryOperator op)
    {
        float a = ToFloatValueRef(left);
        float b = ToFloatValueRef(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromFloat32(a + b),
            BinaryOperator.Subtract => ValueRef.FromFloat32(a - b),
            BinaryOperator.Multiply => ValueRef.FromFloat32(a * b),
            BinaryOperator.Divide => ValueRef.FromFloat32(b != 0f ? a / b : float.NaN),
            BinaryOperator.Modulo => ValueRef.FromFloat32(b != 0f ? a % b : float.NaN),
            BinaryOperator.Power => ValueRef.FromFloat32(MathF.Pow(a, b)),
            _ => throw new InvalidOperationException(
                $"Internal error: Float32 dispatch hit for {op}."),
        };
    }

    private static ValueRef ApplyFloat64(ValueRef left, ValueRef right, BinaryOperator op)
    {
        double a = ToDoubleValueRef(left);
        double b = ToDoubleValueRef(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromFloat64(a + b),
            BinaryOperator.Subtract => ValueRef.FromFloat64(a - b),
            BinaryOperator.Multiply => ValueRef.FromFloat64(a * b),
            BinaryOperator.Divide => ValueRef.FromFloat64(b != 0.0 ? a / b : double.NaN),
            BinaryOperator.Modulo => ValueRef.FromFloat64(b != 0.0 ? a % b : double.NaN),
            BinaryOperator.Power => ValueRef.FromFloat64(Math.Pow(a, b)),
            _ => throw new InvalidOperationException(
                $"Internal error: Float64 dispatch hit for {op}."),
        };
    }

    private static ValueRef ApplyDecimal(ValueRef left, ValueRef right, BinaryOperator op)
    {
        decimal a = ToDecimalPromoted(left);
        decimal b = ToDecimalPromoted(right);
        return op switch
        {
            BinaryOperator.Add => ValueRef.FromDecimal(a + b),
            BinaryOperator.Subtract => ValueRef.FromDecimal(a - b),
            BinaryOperator.Multiply => ValueRef.FromDecimal(a * b),
            BinaryOperator.Divide => b != 0m
                ? ValueRef.FromDecimal(a / b)
                : ValueRef.Null(DataKind.Decimal),
            BinaryOperator.Modulo => b != 0m
                ? ValueRef.FromDecimal(a % b)
                : ValueRef.Null(DataKind.Decimal),
            _ => throw new InvalidOperationException(
                $"Internal error: Decimal dispatch hit for {op}."),
        };
    }

    /// <summary>
    /// Coerces a ValueRef to <see cref="int"/> for the Int32 arithmetic
    /// path. Promotion has already filtered out everything wider than 32
    /// bits, so the only kinds reaching here are Int8/UInt8/Int16/UInt16/
    /// Int32 and Boolean.
    /// </summary>
    private static int ToInt32Promoted(ValueRef v) => v.Kind switch
    {
        DataKind.Boolean => v.AsBoolean() ? 1 : 0,
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        _ => throw new InvalidOperationException(
            $"Cannot extract Int32 from {v.Kind} in promoted Int32 arithmetic."),
    };

    /// <summary>
    /// Coerces a ValueRef to <see cref="long"/> for the Int64 arithmetic
    /// path. Accepts every integer kind up to UInt64 (with the UInt64 →
    /// long cast wrapping at 2^63 — workloads that need full UInt64
    /// range should run through Int128 or float).
    /// </summary>
    private static long ToInt64Promoted(ValueRef v) => v.Kind switch
    {
        DataKind.Boolean => v.AsBoolean() ? 1L : 0L,
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => unchecked((long)v.AsUInt64()),
        _ => throw new InvalidOperationException(
            $"Cannot extract Int64 from {v.Kind} in promoted Int64 arithmetic."),
    };

    private static Int128 ToInt128Promoted(ValueRef v) => v.Kind switch
    {
        DataKind.Boolean => v.AsBoolean() ? (Int128)1 : (Int128)0,
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => v.AsUInt64(),
        DataKind.Int128 => v.AsInt128(),
        DataKind.UInt128 => unchecked((Int128)v.AsUInt128()),
        _ => throw new InvalidOperationException(
            $"Cannot extract Int128 from {v.Kind} in promoted Int128 arithmetic."),
    };

    private static decimal ToDecimalPromoted(ValueRef v) => v.Kind switch
    {
        DataKind.Boolean => v.AsBoolean() ? 1m : 0m,
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => v.AsUInt64(),
        DataKind.Float16 => (decimal)(double)v.AsFloat16(),
        DataKind.Float32 => (decimal)v.AsFloat32(),
        DataKind.Float64 => (decimal)v.AsFloat64(),
        DataKind.Decimal => v.AsDecimal(),
        _ => throw new InvalidOperationException(
            $"Cannot extract Decimal from {v.Kind} in promoted Decimal arithmetic."),
    };

    private static double ToDoubleValueRef(ValueRef value)
    {
        switch (value.Kind)
        {
            case DataKind.Boolean: return value.AsBoolean() ? 1.0 : 0.0;
            case DataKind.UInt8: return value.AsUInt8();
            case DataKind.Int8: return value.AsInt8();
            case DataKind.Int16: return value.AsInt16();
            case DataKind.UInt16: return value.AsUInt16();
            case DataKind.Int32: return value.AsInt32();
            case DataKind.UInt32: return value.AsUInt32();
            case DataKind.Int64: return value.AsInt64();
            case DataKind.UInt64: return value.AsUInt64();
            case DataKind.Int128: return (double)value.AsInt128();
            case DataKind.UInt128: return (double)value.AsUInt128();
            case DataKind.Float16: return (double)value.AsFloat16();
            case DataKind.Float32: return value.AsFloat32();
            case DataKind.Float64: return value.AsFloat64();
            case DataKind.Decimal: return (double)value.AsDecimal();
            case DataKind.Duration: return value.AsDuration().TotalSeconds;
            case DataKind.Time:
            {
                TimeOnly t = value.AsTime();
                return t.Hour * 3600 + t.Minute * 60 + t.Second + t.Millisecond / 1000.0;
            }
            case DataKind.String:
                if (double.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                {
                    return parsed;
                }
                throw new InvalidOperationException($"Cannot convert string '{value.AsString()}' to number.");
            default:
                throw new InvalidOperationException($"Cannot use {value.Kind} in arithmetic.");
        }
    }

    private static float ToFloatValueRef(ValueRef value)
    {
        switch (value.Kind)
        {
            case DataKind.Boolean: return value.AsBoolean() ? 1f : 0f;
            case DataKind.UInt8: return value.AsUInt8();
            case DataKind.Int8: return value.AsInt8();
            case DataKind.Int16: return value.AsInt16();
            case DataKind.UInt16: return value.AsUInt16();
            case DataKind.Int32: return value.AsInt32();
            case DataKind.UInt32: return value.AsUInt32();
            case DataKind.Int64: return value.AsInt64();
            case DataKind.UInt64: return value.AsUInt64();
            case DataKind.Int128: return (float)value.AsInt128();
            case DataKind.UInt128: return (float)value.AsUInt128();
            case DataKind.Float16: return (float)value.AsFloat16();
            case DataKind.Float32: return value.AsFloat32();
            case DataKind.Float64: return (float)value.AsFloat64();
            case DataKind.Decimal: return (float)value.AsDecimal();
            case DataKind.Duration: return (float)value.AsDuration().TotalSeconds;
            case DataKind.Time:
            {
                TimeOnly t = value.AsTime();
                return (float)(t.Hour * 3600 + t.Minute * 60 + t.Second + t.Millisecond / 1000.0);
            }
            case DataKind.String:
                if (float.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                {
                    return parsed;
                }
                throw new InvalidOperationException($"Cannot convert string '{value.AsString()}' to number.");
            default:
                throw new InvalidOperationException($"Cannot use {value.Kind} in arithmetic.");
        }
    }

    private static bool TryGetValueRefAsDouble(ValueRef value, out double result)
    {
        switch (value.Kind)
        {
            case DataKind.UInt8: result = value.AsUInt8(); return true;
            case DataKind.Int8: result = value.AsInt8(); return true;
            case DataKind.Int16: result = value.AsInt16(); return true;
            case DataKind.UInt16: result = value.AsUInt16(); return true;
            case DataKind.Int32: result = value.AsInt32(); return true;
            case DataKind.UInt32: result = value.AsUInt32(); return true;
            case DataKind.Int64: result = value.AsInt64(); return true;
            case DataKind.UInt64: result = value.AsUInt64(); return true;
            case DataKind.Float32: result = value.AsFloat32(); return true;
            case DataKind.Float64: result = value.AsFloat64(); return true;
            default: result = 0; return false;
        }
    }

    private static float ToFloat(DataValue value)
    {
        if (value.TryToFloat(out float f)) return f;
        return value.Kind switch
        {
            DataKind.Duration => (float)value.AsDuration().TotalSeconds,
            DataKind.Time => (float)(value.AsTime().Hour * 3600 + value.AsTime().Minute * 60 + value.AsTime().Second + value.AsTime().Millisecond / 1000.0),
            DataKind.String => float.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : throw new InvalidOperationException($"Cannot convert string '{value.AsString()}' to number."),
            _ => throw new InvalidOperationException($"Cannot use {value.Kind} in arithmetic."),
        };
    }

    // ──────────────────── Comparison helpers ────────────────────

    /// <summary>
    /// Compares two <see cref="DataValue"/>s using the arenas carried by <paramref name="frame"/>.
    /// For non-inline String operands the comparer needs arena bytes to resolve
    /// UTF-8 payloads; we pass <see cref="EvaluationFrame.Source"/> for the left operand (row
    /// values typically live in the input batch's arena) and <see cref="EvaluationFrame.Target"/>
    /// for the right (materialised literals go there). This convention matches the common
    /// <c>column OP literal</c> shape; flipped operand order with a non-inline literal on the
    /// left is not yet supported without literal hoisting.
    /// </summary>
    private static int CompareDataValues(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        return DataValueComparer.Compare(left, frame.Source, right, frame.Target);
    }

    /// <summary>
    /// ValueRef counterpart of <see cref="CompareDataValues"/>. Reads accessors
    /// directly off the ValueRefs without arena resolution. Both operands
    /// non-null is the contract — the binary handler short-circuits NULL
    /// before calling.
    /// </summary>
    private static int CompareValueRefs(ValueRef left, ValueRef right)
    {
        // Numeric coercion handles every cross-numeric pairing in one path.
        if (TryGetValueRefAsDouble(left, out double l) && TryGetValueRefAsDouble(right, out double r))
        {
            return l.CompareTo(r);
        }

        if (left.Kind == DataKind.String && right.Kind == DataKind.String)
        {
            return string.CompareOrdinal(left.AsString(), right.AsString());
        }

        if (left.Kind == right.Kind)
        {
            return left.Kind switch
            {
                DataKind.Boolean => left.AsBoolean().CompareTo(right.AsBoolean()),
                DataKind.Date => left.AsDate().CompareTo(right.AsDate()),
                DataKind.DateTime => left.AsDateTime().CompareTo(right.AsDateTime()),
                DataKind.Time => left.AsTime().CompareTo(right.AsTime()),
                DataKind.Duration => left.AsDuration().CompareTo(right.AsDuration()),
                DataKind.Uuid => left.AsUuid().CompareTo(right.AsUuid()),
                DataKind.Type => ((byte)left.AsType()).CompareTo((byte)right.AsType()),
                _ => throw new InvalidOperationException(
                    $"Cannot compare values of kind {left.Kind}."),
            };
        }

        throw new InvalidOperationException(
            $"Cannot compare {left.Kind} with {right.Kind}.");
    }

    private static ValueRef CompareValuesValueRef(ValueRef left, ValueRef right, int expectedSign, bool negate = false)
    {
        int comparison = CompareValueRefs(left, right);
        bool result = expectedSign == 0 ? comparison == 0 : (expectedSign < 0 ? comparison < 0 : comparison > 0);
        if (negate)
        {
            result = !result;
        }
        return ValueRef.FromBoolean(result);
    }

    private static ValueRef CompareValuesLeValueRef(ValueRef left, ValueRef right) =>
        ValueRef.FromBoolean(CompareValueRefs(left, right) <= 0);

    private static ValueRef CompareValuesGeValueRef(ValueRef left, ValueRef right) =>
        ValueRef.FromBoolean(CompareValueRefs(left, right) >= 0);

    // ───────────────── Struct and index-access evaluation ─────────────────

    private DataValue EvaluateStructLiteral(StructLiteralExpression literal, in EvaluationFrame frame)
    {
        DataValue[] fields = new DataValue[literal.Fields.Count];
        for (int index = 0; index < literal.Fields.Count; index++)
        {
            fields[index] = Evaluate(literal.Fields[index].Value, frame);
        }

        ushort typeId = 0;
        if (_typeRegistry is not null)
        {
            var fieldDescriptors = new StructFieldDescriptor[literal.Fields.Count];
            for (int i = 0; i < literal.Fields.Count; i++)
            {
                int fieldTypeId = _typeRegistry.InternScalarType(fields[i].Kind);
                fieldDescriptors[i] = new StructFieldDescriptor(literal.Fields[i].Name, fieldTypeId);
            }
            typeId = (ushort)_typeRegistry.InternStructType(fieldDescriptors);
        }

        return DataValue.FromStruct(fields, frame.Target, typeId);
    }

    /// <summary>
    /// Whether <paramref name="kind"/> can be used as a positional ordinal in
    /// <c>struct[i]</c> indexing. Mirrors <see cref="DataValue.TryToFloat(out float)"/>'s
    /// supported numeric kinds — the implementation funnels through
    /// <see cref="ToFloat"/>, so the check must accept every kind that helper
    /// recognises (otherwise small numeric literals like <c>1</c>, parsed as
    /// <see cref="DataKind.Int8"/>, fall through to the named-field path and
    /// trip a String-only conversion).
    /// </summary>
    private static bool IsPositionalIndexKind(DataKind kind) => kind is
        DataKind.Int8 or DataKind.UInt8
        or DataKind.Int16 or DataKind.UInt16
        or DataKind.Int32 or DataKind.UInt32
        or DataKind.Int64 or DataKind.UInt64
        or DataKind.Int128 or DataKind.UInt128
        or DataKind.Float16 or DataKind.Float32 or DataKind.Float64
        or DataKind.Decimal;

    private DataValue EvaluateIndexAccess(IndexAccessExpression indexAccess, in EvaluationFrame frame)
    {
        DataValue source = Evaluate(indexAccess.Source, frame);

        if (source.IsNull)
        {
            return source;
        }

        DataValue index = Evaluate(indexAccess.Index, frame);

        if (source.IsArray)
        {
            // Typed array (Kind=elementKind + IsArray). Element kind is source.Kind.
            if (index.Kind == DataKind.String)
            {
                throw new InvalidOperationException(
                    $"Named field access ('{Str(index, frame)}') is not supported on Array<{source.Kind}> — " +
                    $"use positional destructuring: LET (a, b, ...) = expr.");
            }

            int position = (int)ToFloat(index);
            return ReadTypedArrayElement(source, position, frame);
        }

        if (source.Kind == DataKind.Struct)
        {
            // Integer / float index → positional (ordinal) access by declaration
            // order. Numeric literals like @row[1] parse to the narrowest type
            // that fits (Int8 for small values), so the kind check covers every
            // numeric kind TryToFloat handles rather than only Int32/Int64.
            if (IsPositionalIndexKind(index.Kind))
            {
                DataValue[] fields = source.AsStruct(frame.Source);
                int position = (int)ToFloat(index);
                if (position < 0 || position >= fields.Length)
                {
                    return DataValue.NullStruct(source.TypeId);
                }
                return fields[position];
            }

            // String index → named field access.
            return EvaluateStructFieldAccess(source, index, indexAccess, frame);
        }

        throw new InvalidOperationException(
            $"Index access is not supported on {source.Kind} values.");
    }

    /// <summary>
    /// Reads a single element from a typed array (<c>Kind=elementKind + IsArray</c>)
    /// at <paramref name="position"/> and returns it as a freshly-built scalar
    /// <see cref="DataValue"/>. Dispatches by element kind across the typed-
    /// array accessors (<see cref="DataValue.AsStringArray"/> /
    /// <see cref="DataValue.AsImageArray"/> / <see cref="DataValue.AsStructArray"/>
    /// for reference kinds, <see cref="DataValue.AsArraySpan{T}"/> for
    /// fixed-width primitives). Out-of-range returns a typed null of the
    /// element kind.
    /// </summary>
    /// <remarks>
    /// Reading the i-th element via the bulk accessor (e.g. AsStringArray
    /// reads all elements) is wasteful for single-index access. Future
    /// optimisation: per-kind GetArrayElementAt(position) accessors that read
    /// one slot's bytes via the slot block. Punted because there are no
    /// repeated-index-access hotspots today.
    /// </remarks>
    private DataValue ReadTypedArrayElement(DataValue source, int position, in EvaluationFrame frame)
    {
        DataKind elementKind = source.Kind;
        switch (elementKind)
        {
            case DataKind.String:
            {
                string[] elements = source.AsStringArray(frame.Source, frame.SidecarRegistry);
                if (position < 0 || position >= elements.Length)
                {
                    return DataValue.Null(DataKind.String);
                }
                return DataValue.FromString(elements[position], frame.Target);
            }
            case DataKind.Image:
            {
                byte[][] elements = source.AsImageArray(frame.Source, frame.SidecarRegistry);
                if (position < 0 || position >= elements.Length)
                {
                    return DataValue.Null(DataKind.Image);
                }
                return DataValue.FromImage(elements[position], frame.Target);
            }
            case DataKind.Struct:
            {
                DataValue[][] elements = source.AsStructArray(frame.Source, frame.SidecarRegistry);
                if (position < 0 || position >= elements.Length)
                {
                    return DataValue.NullStruct(0);
                }
                DataValue[] fields = elements[position];
                return DataValue.FromStruct(fields, frame.Target);
            }
        }

        // Fixed-width primitives. The single-element read goes through
        // AsArraySpan<T>; we wrap the resulting scalar back as a DataValue.
        return ReadFixedWidthArrayElement(source, position, frame);
    }

    private static DataValue ReadFixedWidthArrayElement(DataValue source, int position, in EvaluationFrame frame)
    {
        DataKind elementKind = source.Kind;
        return elementKind switch
        {
            DataKind.Boolean => ReadBooleanElement(source, position, frame),
            DataKind.Int8 => ReadElement<sbyte>(source, position, frame, DataValue.FromInt8, DataValue.Null(DataKind.Int8)),
            DataKind.UInt8 => ReadElement<byte>(source, position, frame, DataValue.FromUInt8, DataValue.Null(DataKind.UInt8)),
            DataKind.Int16 => ReadElement<short>(source, position, frame, DataValue.FromInt16, DataValue.Null(DataKind.Int16)),
            DataKind.UInt16 => ReadElement<ushort>(source, position, frame, DataValue.FromUInt16, DataValue.Null(DataKind.UInt16)),
            DataKind.Float16 => ReadElement<Half>(source, position, frame, DataValue.FromFloat16, DataValue.Null(DataKind.Float16)),
            DataKind.Int32 => ReadElement<int>(source, position, frame, DataValue.FromInt32, DataValue.Null(DataKind.Int32)),
            DataKind.UInt32 => ReadElement<uint>(source, position, frame, DataValue.FromUInt32, DataValue.Null(DataKind.UInt32)),
            DataKind.Float32 => ReadElement<float>(source, position, frame, DataValue.FromFloat32, DataValue.Null(DataKind.Float32)),
            DataKind.Date => ReadElement<int>(source, position, frame,
                dayNumber => DataValue.FromDate(DateOnly.FromDayNumber(dayNumber)), DataValue.Null(DataKind.Date)),
            DataKind.Int64 => ReadElement<long>(source, position, frame, DataValue.FromInt64, DataValue.Null(DataKind.Int64)),
            DataKind.UInt64 => ReadElement<ulong>(source, position, frame, DataValue.FromUInt64, DataValue.Null(DataKind.UInt64)),
            DataKind.Float64 => ReadElement<double>(source, position, frame, DataValue.FromFloat64, DataValue.Null(DataKind.Float64)),
            DataKind.Time => ReadElement<long>(source, position, frame,
                ticks => DataValue.FromTime(new TimeOnly(ticks)), DataValue.Null(DataKind.Time)),
            DataKind.Duration => ReadElement<long>(source, position, frame,
                ticks => DataValue.FromDuration(new TimeSpan(ticks)), DataValue.Null(DataKind.Duration)),
            DataKind.Decimal => ReadElement<decimal>(source, position, frame, DataValue.FromDecimal, DataValue.Null(DataKind.Decimal)),
            DataKind.Int128 => ReadElement<Int128>(source, position, frame, DataValue.FromInt128, DataValue.Null(DataKind.Int128)),
            DataKind.UInt128 => ReadElement<UInt128>(source, position, frame, DataValue.FromUInt128, DataValue.Null(DataKind.UInt128)),
            DataKind.Uuid => ReadElement<Guid>(source, position, frame, DataValue.FromUuid, DataValue.Null(DataKind.Uuid)),
            _ => throw new InvalidOperationException(
                $"Index access on Array<{elementKind}> is not yet wired through ReadFixedWidthArrayElement."),
        };
    }

    private static DataValue ReadBooleanElement(DataValue source, int position, in EvaluationFrame frame)
    {
        ReadOnlySpan<byte> elements = source.AsArraySpan<byte>(frame.Source, frame.SidecarRegistry);
        if (position < 0 || position >= elements.Length)
        {
            return DataValue.Null(DataKind.Boolean);
        }
        return DataValue.FromBoolean(elements[position] != 0);
    }

    private static DataValue ReadElement<T>(
        DataValue source, int position, in EvaluationFrame frame,
        Func<T, DataValue> wrap, DataValue outOfRangeNull) where T : unmanaged
    {
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        if (position < 0 || position >= elements.Length)
        {
            return outOfRangeNull;
        }
        return wrap(elements[position]);
    }

    private DataValue EvaluateStructFieldAccess(
        DataValue source, DataValue index, IndexAccessExpression indexAccess, in EvaluationFrame frame)
    {
        DataValue[] fields = source.AsStruct(frame.Source);
        string fieldName = Str(index, frame);

        // Fast path: value already carries a type-id stamped at construction time.
        // Avoids schema/AST walking for values emitted by EvaluateStructLiteral or
        // model-invocation scatter that stamped the type-id at construction.
        if (_typeRegistry is not null && source.TypeId != 0)
        {
            TypeDescriptor? typeDesc = _typeRegistry.GetDescriptor(source.TypeId);
            if (typeDesc is not null)
            {
                int idx = typeDesc.FindFieldIndex(fieldName);
                if (idx >= 0)
                    return idx < fields.Length ? fields[idx] : DataValue.NullStruct(source.TypeId);
                return DataValue.NullStruct(source.TypeId);
            }
        }

        // Procedural variable bound to a struct via FOR-IN — field names live
        // alongside the binding on the variable scope, so we can resolve named
        // access without scanning a schema or AST. Forward-compatible with the
        // planned per-query type registry: when that lands, the variable scope
        // will surface the registry's TypeDescriptor here instead of a raw
        // string list, and this branch collapses into a registry lookup.
        if (indexAccess.Source is VariableExpression varExpr
            && _variableScope is not null
            && _variableScope.TryGetFieldNames(varExpr.Name, out IReadOnlyList<string>? variableFieldNames)
            && variableFieldNames is not null)
        {
            for (int i = 0; i < variableFieldNames.Count; i++)
            {
                if (string.Equals(variableFieldNames[i], fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return i < fields.Length ? fields[i] : DataValue.NullStruct(source.TypeId);
                }
            }
            return DataValue.NullStruct(source.TypeId);
        }

        // Try to resolve field position from schema when source is a column reference.
        if (indexAccess.Source is ColumnReference colRef)
        {
            IReadOnlyList<ColumnInfo>? columnFields = FindStructColumnFields(colRef, _sourceSchema);
            if (columnFields is not null)
            {
                return LookupFieldByName(fields, columnFields, fieldName, source.TypeId);
            }
        }

        // For struct literals, the field names are available in the AST.
        if (indexAccess.Source is StructLiteralExpression literal)
        {
            for (int i = 0; i < literal.Fields.Count; i++)
            {
                if (string.Equals(literal.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return i < fields.Length ? fields[i] : DataValue.Null(DataKind.Float32);
                }
            }

            return DataValue.NullStruct(source.TypeId);
        }

        // Fallback for hidden LET binding references (e.g., __destructure_N produced by named
        // destructuring desugaring). When the schema doesn't carry struct field metadata for the
        // binding, recover field positions by following the chain of ColumnReference aliases in
        // _letBindingExpressions until we reach a StructLiteralExpression whose field names are
        // encoded in the AST. This handles both direct (`LET {a} = {x:1}`) and indirect
        // (`LET s = {x:1}; LET {a} = s`) cases.
        if (indexAccess.Source is ColumnReference bindingRef
            && _letBindingExpressions is not null
            && _letBindingExpressions.TryGetValue(bindingRef.ColumnName, out Expression? bindingExpr))
        {
            // Follow ColumnReference aliases up to a small depth cap to guard against cycles.
            for (int depth = 0; depth < 8 && bindingExpr is ColumnReference chainRef; depth++)
            {
                if (!_letBindingExpressions.TryGetValue(chainRef.ColumnName, out bindingExpr))
                    break;
            }

            if (bindingExpr is StructLiteralExpression bindingLiteral)
            {
                for (int i = 0; i < bindingLiteral.Fields.Count; i++)
                {
                    if (string.Equals(bindingLiteral.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i < fields.Length ? fields[i] : DataValue.Null(DataKind.Float32);
                    }
                }

                return DataValue.NullStruct(source.TypeId);
            }
        }

        throw new InvalidOperationException(
            $"Cannot resolve struct field '{fieldName}': the source expression of kind " +
            $"{indexAccess.Source.GetType().Name} does not carry field name metadata at evaluation time. " +
            "Access struct fields via a column reference or a struct literal.");
    }

    private static IReadOnlyList<ColumnInfo>? FindStructColumnFields(ColumnReference column, Schema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        ColumnInfo? info = null;

        if (column.TableName is not null)
        {
            info = schema.FindColumn($"{column.TableName}.{column.ColumnName}");
        }

        info ??= schema.FindColumn(column.ColumnName);
        return info?.Fields;
    }

    private static DataValue LookupFieldByName(
        DataValue[] fields,
        IReadOnlyList<ColumnInfo> columnFields,
        string fieldName,
        ushort typeId)
    {
        for (int i = 0; i < columnFields.Count; i++)
        {
            if (string.Equals(columnFields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return i < fields.Length ? fields[i] : DataValue.Null(columnFields[i].Kind);
            }
        }

        return DataValue.NullStruct(typeId);
    }

    /// <summary>
    /// Evaluates a case-sensitive LIKE expression. Converts SQL wildcards
    /// (<c>%</c> and <c>_</c>) into a regex pattern.
    /// </summary>
    private bool LikeMatch(string input, string pattern)
    {
        if (!_likeRegexCache.TryGetValue(pattern, out Regex? regex))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            _likeRegexCache[pattern] = regex;
        }

        return regex.IsMatch(input);
    }

    /// <summary>
    /// Case-insensitive ILIKE wildcard match. Same wildcard conversion
    /// as <see cref="LikeMatch"/> but with <see cref="RegexOptions.IgnoreCase"/>.
    /// </summary>
    private bool ILikeMatch(string input, string pattern)
    {
        if (!_iLikeRegexCache.TryGetValue(pattern, out Regex? regex))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _iLikeRegexCache[pattern] = regex;
        }

        return regex.IsMatch(input);
    }

    /// <summary>
    /// REGEXP substring match (unanchored). Uses
    /// <see cref="RegexOptions.NonBacktracking"/> to prevent catastrophic
    /// backtracking on adversarial patterns.
    /// </summary>
    private bool RegexpMatch(string input, string pattern)
    {
        if (!_regexpCache.TryGetValue(pattern, out Regex? regex))
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            _regexpCache[pattern] = regex;
        }

        return regex.IsMatch(input);
    }

    private ValueRef EvaluateLikeValueRef(ValueRef left, ValueRef right)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("LIKE requires string operands.");
        }
        return ValueRef.FromBoolean(LikeMatch(left.AsString(), right.AsString()));
    }

    private ValueRef EvaluateILikeValueRef(ValueRef left, ValueRef right)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("ILIKE requires string operands.");
        }
        return ValueRef.FromBoolean(ILikeMatch(left.AsString(), right.AsString()));
    }

    private ValueRef EvaluateRegexpValueRef(ValueRef left, ValueRef right)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("REGEXP requires string operands.");
        }
        return ValueRef.FromBoolean(RegexpMatch(left.AsString(), right.AsString()));
    }

    /// <summary>
    /// Evaluates a LIKE or ILIKE expression with an explicit ESCAPE character.
    /// The escape character causes the following <c>%</c> or <c>_</c> to be
    /// treated as a literal instead of a wildcard.
    /// </summary>
    private DataValue EvaluateLikeEscape(LikeExpression like, in EvaluationFrame frame)
    {
        DataValue input = Evaluate(like.Expression, frame);
        DataValue pattern = Evaluate(like.Pattern, frame);
        DataValue escapeValue = Evaluate(like.EscapeCharacter, frame);

        if (input.IsNull || pattern.IsNull || escapeValue.IsNull)
        {
            return DataValue.Null(DataKind.Boolean);
        }

        if (input.Kind != DataKind.String || pattern.Kind != DataKind.String || escapeValue.Kind != DataKind.String)
        {
            throw new InvalidOperationException("LIKE ... ESCAPE requires string operands.");
        }

        string escapeString = Str(escapeValue, frame);
        if (escapeString.Length != 1)
        {
            throw new InvalidOperationException("ESCAPE character must be a single character.");
        }

        char escapeChar = escapeString[0];
        string sqlPattern = Str(pattern, frame);
        string cacheKey = $"{sqlPattern}\0{escapeChar}\0{(like.CaseInsensitive ? 'i' : 'c')}";

        if (!_likeRegexCache.TryGetValue(cacheKey, out Regex? regex))
        {
            System.Text.StringBuilder regexBuilder = new();
            regexBuilder.Append('^');

            for (int i = 0; i < sqlPattern.Length; i++)
            {
                char current = sqlPattern[i];

                if (current == escapeChar && i + 1 < sqlPattern.Length)
                {
                    // Escaped character — treat next char as literal.
                    regexBuilder.Append(Regex.Escape(sqlPattern[i + 1].ToString()));
                    i++;
                }
                else if (current == '%')
                {
                    regexBuilder.Append(".*");
                }
                else if (current == '_')
                {
                    regexBuilder.Append('.');
                }
                else
                {
                    regexBuilder.Append(Regex.Escape(current.ToString()));
                }
            }

            regexBuilder.Append('$');

            RegexOptions options = RegexOptions.Compiled | RegexOptions.CultureInvariant;
            if (like.CaseInsensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            regex = new Regex(regexBuilder.ToString(), options);
            _likeRegexCache[cacheKey] = regex;
        }

        bool matches = regex.IsMatch(Str(input, frame));
        return DataValue.FromBoolean(matches);
    }
}
