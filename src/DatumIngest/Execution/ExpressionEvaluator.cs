using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using DatumIngest.Functions;
using DatumIngest.Functions.Image;
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
    /// Persistent store used for (1) per-evaluator caches (<see cref="_castTargetCache"/>,
    /// <see cref="_inValueSetCache"/>) whose <see cref="DataValue"/> entries must outlive
    /// any single batch, and (2) the <see cref="Row"/>-only overload of
    /// <see cref="Evaluate(Expression, Row)"/> which constructs a default frame using this
    /// store for both the source and target arenas. Callers that want true two-arena
    /// behaviour should invoke the <see cref="EvaluationFrame"/>-based overloads instead.
    /// </summary>
    private readonly IValueStore? _store;
    private readonly Row? _outerRow;
    private readonly Schema? _sourceSchema;

    /// <summary>
    /// Maps LET binding names to their source expressions. Used by
    /// <see cref="EvaluateStructFieldAccess"/> when the schema doesn't carry struct
    /// field metadata for a hidden <c>__destructure_N</c> binding: if the binding's
    /// original expression is a <see cref="StructLiteralExpression"/>, field names
    /// can be recovered from the AST without schema.
    /// </summary>
    private readonly IReadOnlyDictionary<string, Expression>? _letBindingExpressions;

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
    public ExpressionEvaluator(FunctionRegistry functions, QueryMeter? meter = null, Row? outerRow = null, Schema? sourceSchema = null, IReadOnlyDictionary<string, Expression>? letBindingExpressions = null, IValueStore? store = null)
    {
        _functions = functions;
        _meter = meter;
        _store = store;
        _outerRow = outerRow;
        _sourceSchema = sourceSchema;
        _letBindingExpressions = letBindingExpressions;
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
        return Evaluate(expression, new EvaluationFrame(row, store, store, _outerRow));
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean, using the store
    /// supplied at construction. Convenience overload.
    /// </summary>
    public bool EvaluateAsBoolean(Expression expression, Row row)
    {
        IValueStore store = _store ?? ThrowStoreRequired();
        return EvaluateAsBoolean(expression, new EvaluationFrame(row, store, store, _outerRow));
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
        DataValue result = Evaluate(expression, frame);

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
            DataKind.String => !string.IsNullOrEmpty(Str(result, frame)),
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

    private DataValue EvaluateBinary(BinaryExpression binary, in EvaluationFrame frame)
    {
        // Short-circuit for AND/OR.
        if (binary.Operator == BinaryOperator.And)
        {
            if (!EvaluateAsBoolean(binary.Left, frame))
            {
                return DataValue.FromBoolean(false);
            }

            if (!EvaluateAsBoolean(binary.Right, frame))
            {
                return DataValue.FromBoolean(false);
            }

            return DataValue.FromBoolean(true);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            if (EvaluateAsBoolean(binary.Left, frame))
            {
                return DataValue.FromBoolean(true);
            }

            if (EvaluateAsBoolean(binary.Right, frame))
            {
                return DataValue.FromBoolean(true);
            }

            return DataValue.FromBoolean(false);
        }

        {
            DataValue left = Evaluate(binary.Left, frame);
            DataValue right = Evaluate(binary.Right, frame);

            // NULL propagation: any operation with NULL yields NULL (except IS NULL checks).
            // Comparisons and pattern operators produce Boolean nulls; arithmetic produces
            // Float32 nulls (the engine's default numeric kind).
            if (left.IsNull || right.IsNull)
            {
                return binary.Operator is BinaryOperator.Equal or BinaryOperator.NotEqual
                    or BinaryOperator.LessThan or BinaryOperator.GreaterThan
                    or BinaryOperator.LessThanOrEqual or BinaryOperator.GreaterThanOrEqual
                    or BinaryOperator.Like or BinaryOperator.ILike or BinaryOperator.Regexp
                    ? DataValue.Null(DataKind.Boolean)
                    : DataValue.Null(DataKind.Float32);
            }

            return binary.Operator switch
            {
                BinaryOperator.Add when left.Kind == DataKind.Duration && right.Kind == DataKind.Duration
                    => DataValue.FromDuration(left.AsDuration() + right.AsDuration()),
                BinaryOperator.Subtract when left.Kind == DataKind.Duration && right.Kind == DataKind.Duration
                    => DataValue.FromDuration(left.AsDuration() - right.AsDuration()),
                BinaryOperator.Add => ArithmeticOp(left, right, static (a, b) => a + b),
                BinaryOperator.Subtract => ArithmeticOp(left, right, static (a, b) => a - b),
                BinaryOperator.Multiply => ArithmeticOp(left, right, static (a, b) => a * b),
                BinaryOperator.Divide => ArithmeticOp(left, right, static (a, b) => b != 0f ? a / b : float.NaN),
                BinaryOperator.Modulo => ArithmeticOp(left, right, static (a, b) => b != 0f ? a % b : float.NaN),
                BinaryOperator.Power => ArithmeticOp(left, right, static (a, b) => MathF.Pow(a, b)),
                BinaryOperator.Equal => CompareValues(left, right, 0, frame),
                BinaryOperator.NotEqual => CompareValues(left, right, 0, frame, negate: true),
                BinaryOperator.LessThan => CompareValues(left, right, -1, frame),
                BinaryOperator.GreaterThan => CompareValues(left, right, 1, frame),
                BinaryOperator.LessThanOrEqual => CompareValuesLe(left, right, frame),
                BinaryOperator.GreaterThanOrEqual => CompareValuesGe(left, right, frame),
                BinaryOperator.Like => EvaluateLike(left, right, frame),
                BinaryOperator.ILike => EvaluateILike(left, right, frame),
                BinaryOperator.Regexp => EvaluateRegexp(left, right, frame),
                _ => throw new InvalidOperationException(
                    $"Unsupported binary operator: {binary.Operator}."),
            };
        }
    }

    private DataValue EvaluateUnary(UnaryExpression unary, in EvaluationFrame frame)
    {
        DataValue operand = Evaluate(unary.Operand, frame);

        if (operand.IsNull)
        {
            return unary.Operator == UnaryOperator.Not
                ? DataValue.Null(DataKind.Boolean)
                : DataValue.Null(DataKind.Float32);
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => DataValue.FromBoolean(
                !EvaluateAsBoolean(unary.Operand, frame)),
            UnaryOperator.Negate => DataValue.FromFloat32(-ToFloat(operand)),
            _ => throw new InvalidOperationException(
                $"Unsupported unary operator: {unary.Operator}."),
        };
    }

    private DataValue EvaluateFunction(FunctionCallExpression function, in EvaluationFrame frame)
    {
        IScalarFunction? scalarFunction = _functions.TryGetScalar(function.FunctionName);

        if (scalarFunction is null)
        {
            throw new InvalidOperationException(
                $"Unknown function: '{function.FunctionName}'.");
        }

        // Higher-order function path: detect lambda arguments and route accordingly.
        if (scalarFunction is IHigherOrderFunction higherOrder)
        {
            return EvaluateHigherOrderFunction(higherOrder, function, frame);
        }

        int argumentCount = function.Arguments.Count;
        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(argumentCount);
        try
        {
            for (int index = 0; index < argumentCount; index++)
            {
                arguments[index] = Evaluate(function.Arguments[index], frame);
            }

            DataValue result = scalarFunction.Execute(arguments.AsSpan(0, argumentCount), frame.Target);

            _meter?.Add(scalarFunction.QueryUnitCost);
            if (_meter is not null && scalarFunction is ICostAwareFunction costAware)
            {
                _meter.Add(costAware.ComputeSupplementalCost(arguments.AsSpan(0, argumentCount), result));
            }
            // Dispose intermediate ImageHandle arguments whose bitmaps are no longer needed.
            // Handles still referenced by the source row are kept alive — they may appear
            // as ordinal copies in the projected row (e.g. SELECT *, func(image)).
            DisposeConsumedImageHandles(arguments.AsSpan(0, argumentCount), result, frame.Row);

            return result;
        }
        finally
        {
            // Clear references so the pool doesn't root DataValue objects.
            arguments.AsSpan(0, argumentCount).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Evaluates a higher-order function call by separating lambda arguments from
    /// eagerly-evaluated arguments. Lambda arguments are passed as AST nodes; the
    /// function receives a <see cref="LambdaEvaluator"/> callback to invoke them.
    /// </summary>
    private DataValue EvaluateHigherOrderFunction(
        IHigherOrderFunction higherOrder,
        FunctionCallExpression function,
        in EvaluationFrame frame)
    {
        int argumentCount = function.Arguments.Count;
        IReadOnlySet<int> lambdaIndices = higherOrder.GetLambdaParameterIndices(argumentCount);

        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(argumentCount);
        Dictionary<int, LambdaExpression> lambdaArguments = new(lambdaIndices.Count);
        try
        {
            for (int index = 0; index < argumentCount; index++)
            {
                if (lambdaIndices.Contains(index))
                {
                    if (function.Arguments[index] is not LambdaExpression lambda)
                    {
                        throw new InvalidOperationException(
                            $"Argument {index + 1} of '{function.FunctionName}' must be a lambda expression (x -> expr).");
                    }

                    lambdaArguments[index] = lambda;
                    arguments[index] = DataValue.UnknownNull();
                }
                else
                {
                    arguments[index] = Evaluate(function.Arguments[index], frame);
                }
            }

            // Capture the current frame for closure semantics — lambda body references
            // to columns resolve against this row after lambda parameter bindings.
            EvaluationFrame capturedFrame = frame;
            LambdaEvaluator evaluator = (LambdaExpression lambda, ReadOnlySpan<DataValue> parameterValues) =>
            {
                return EvaluateLambdaBody(lambda, parameterValues, capturedFrame);
            };

            DataValue result = higherOrder.ExecuteHigherOrder(
                arguments.AsSpan(0, argumentCount),
                lambdaArguments,
                evaluator);

            _meter?.Add(higherOrder.QueryUnitCost);
            if (_meter is not null && higherOrder is ICostAwareFunction costAware)
            {
                _meter.Add(costAware.ComputeSupplementalCost(arguments.AsSpan(0, argumentCount), result));
            }

            DisposeConsumedImageHandles(arguments.AsSpan(0, argumentCount), result, frame.Row);

            return result;
        }
        finally
        {
            arguments.AsSpan(0, argumentCount).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Evaluates a lambda body by creating an augmented row where lambda parameter
    /// names shadow any existing column names. Unmatched column references in the
    /// lambda body fall through to the enclosing row (closure semantics).
    /// </summary>
    private DataValue EvaluateLambdaBody(
        LambdaExpression lambda,
        ReadOnlySpan<DataValue> parameterValues,
        in EvaluationFrame enclosingFrame)
    {
        int parameterCount = lambda.Parameters.Count;
        if (parameterValues.Length != parameterCount)
        {
            throw new InvalidOperationException(
                $"Lambda expects {parameterCount} parameter(s) but received {parameterValues.Length}.");
        }

        Row enclosingRow = enclosingFrame.Row;

        // Build an augmented row: original columns + lambda parameter bindings.
        // Lambda parameters shadow columns with the same name.
        int originalFieldCount = enclosingRow.FieldCount;
        int augmentedFieldCount = originalFieldCount + parameterCount;
        string[] augmentedNames = new string[augmentedFieldCount];
        DataValue[] augmentedValues = new DataValue[augmentedFieldCount];

        for (int index = 0; index < originalFieldCount; index++)
        {
            augmentedNames[index] = enclosingRow.ColumnNames[index];
            augmentedValues[index] = enclosingRow[index];
        }

        for (int index = 0; index < parameterCount; index++)
        {
            augmentedNames[originalFieldCount + index] = lambda.Parameters[index];
            augmentedValues[originalFieldCount + index] = parameterValues[index];
        }

        // The Row constructor builds a name-index dictionary where later entries
        // overwrite earlier ones for the same key (case-insensitive), giving lambda
        // parameters priority over enclosing columns — correct closure semantics.
        Row augmentedRow = new(augmentedNames, augmentedValues);

        return Evaluate(lambda.Body, enclosingFrame.WithRow(augmentedRow));
    }

    /// <summary>
    /// Disposes <see cref="ImageHandle"/> payloads in evaluated arguments that are
    /// no longer referenced by the result or the source row. This releases native
    /// <see cref="SkiaSharp.SKBitmap"/> memory from intermediate pipeline stages
    /// (e.g. nested <c>image_to_tensor_chw(resize(image, 64, 64))</c>) while keeping
    /// handles that the source row still owns alive for ordinal copies.
    /// </summary>
    private static void DisposeConsumedImageHandles(
        ReadOnlySpan<DataValue> arguments, DataValue result, Row sourceRow)
    {
        for (int index = 0; index < arguments.Length; index++)
        {
            DataValue argument = arguments[index];

            if (argument.Kind != DataKind.Image || argument.IsNull)
            {
                continue;
            }

            if (argument.TryGetOwnedImageHandle() is not ImageHandle argumentHandle)
            {
                continue;
            }

            // If the result reuses the same handle, don't dispose — it's still alive.
            if (result.Kind == DataKind.Image
                && !result.IsNull
                && ReferenceEquals(argumentHandle, result.TryGetOwnedImageHandle()))
            {
                continue;
            }

            // If the source row still references this handle, don't dispose — it may
            // be needed by ordinal copies in the projected row (SELECT *, func(image)).
            if (IsHandleReferencedByRow(argumentHandle, sourceRow))
            {
                continue;
            }

            argumentHandle.Dispose();
        }
    }

    /// <summary>
    /// Checks whether any column in the row holds the given <see cref="ImageHandle"/>.
    /// </summary>
    private static bool IsHandleReferencedByRow(ImageHandle handle, Row row)
    {
        for (int index = 0; index < row.FieldCount; index++)
        {
            DataValue value = row[index];

            if (value.Kind == DataKind.Image
                && !value.IsNull
                && ReferenceEquals(handle, value.TryGetOwnedImageHandle()))
            {
                return true;
            }
        }

        return false;
    }

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
        EvaluationFrame cacheFrame = new(frame.Row, frame.Source, cacheStore, frame.OuterRow, frame.Sidecar);

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

    private DataValue EvaluateIsNull(IsNullExpression isNull, in EvaluationFrame frame)
    {
        DataValue value = Evaluate(isNull.Expression, frame);
        bool result = value.IsNull;

        if (isNull.Negated)
        {
            result = !result;
        }

        return DataValue.FromBoolean(result);
    }

    /// <summary>
    /// Cached <see cref="DataValue"/> wrappers for CAST target type strings, keyed by
    /// the type name (e.g. "Float32", "UInt8"). Avoids allocating a new DataValue per row.
    /// Entries are written to the persistent <c>_store</c> so they outlive any batch.
    /// </summary>
    private readonly Dictionary<string, DataValue> _castTargetCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TimeZoneInfo> _timeZoneCache = new(StringComparer.OrdinalIgnoreCase);

    private DataValue EvaluateCast(CastExpression cast, in EvaluationFrame frame)
    {
        DataValue value = Evaluate(cast.Expression, frame);

        IScalarFunction? castFunction = _functions.TryGetScalar("cast");
        if (castFunction is null)
        {
            throw new InvalidOperationException("Cast function not registered.");
        }

        if (!_castTargetCache.TryGetValue(cast.TargetType, out DataValue targetTypeValue))
        {
            // Cache entries must outlive any single batch — use the persistent store.
            IValueStore cacheStore = _store ?? frame.Target;
            targetTypeValue = DataValue.FromString(cast.TargetType, cacheStore);
            _castTargetCache[cast.TargetType] = targetTypeValue;
        }

        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(2);
        try
        {
            arguments[0] = value;
            arguments[1] = targetTypeValue;
            DataValue result = castFunction.Execute(arguments.AsSpan(0, 2), frame.Target);
            _meter?.Add(castFunction.QueryUnitCost);
            return result;
        }
        finally
        {
            arguments.AsSpan(0, 2).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
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

    private static DataValue ArithmeticOp(DataValue left, DataValue right, Func<float, float, float> operation)
    {
        float leftValue = ToFloat(left);
        float rightValue = ToFloat(right);
        return DataValue.FromFloat32(operation(leftValue, rightValue));
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

    private static DataValue CompareValues(DataValue left, DataValue right, int expectedSign, in EvaluationFrame frame, bool negate = false)
    {
        int comparison = CompareDataValues(left, right, frame);
        bool result = expectedSign == 0 ? comparison == 0 : (expectedSign < 0 ? comparison < 0 : comparison > 0);
        if (negate)
        {
            result = !result;
        }

        return DataValue.FromBoolean(result);
    }

    private static DataValue CompareValuesLe(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        int comparison = CompareDataValues(left, right, frame);
        return DataValue.FromBoolean(comparison <= 0);
    }

    private static DataValue CompareValuesGe(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        int comparison = CompareDataValues(left, right, frame);
        return DataValue.FromBoolean(comparison >= 0);
    }

    /// <summary>
    /// Compares two <see cref="DataValue"/>s using the arenas carried by <paramref name="frame"/>.
    /// For non-inline String/JsonValue operands the comparer needs arena bytes to resolve
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

    // ───────────────── Struct and index-access evaluation ─────────────────

    private DataValue EvaluateStructLiteral(StructLiteralExpression literal, in EvaluationFrame frame)
    {
        DataValue[] fields = new DataValue[literal.Fields.Count];

        for (int index = 0; index < literal.Fields.Count; index++)
        {
            fields[index] = Evaluate(literal.Fields[index].Value, frame);
        }

        return DataValue.FromStruct((short)literal.Fields.Count, fields);
    }

    private DataValue EvaluateIndexAccess(IndexAccessExpression indexAccess, in EvaluationFrame frame)
    {
        DataValue source = Evaluate(indexAccess.Source, frame);

        if (source.IsNull)
        {
            return source;
        }

        DataValue index = Evaluate(indexAccess.Index, frame);

        if (source.Kind == DataKind.Array)
        {
            if (index.Kind == DataKind.String)
            {
                throw new InvalidOperationException(
                    $"Named field access ('{Str(index, frame)}') is not supported on Array: " +
                    $"use positional destructuring: LET (a, b, ...) = expr.");
            }

            DataValue[] elements = source.AsArray();

            // Use 0-based integer index.
            int position = (int)ToFloat(index);
            if (position < 0 || position >= elements.Length)
            {
                return DataValue.NullArray(source.ArrayElementKind);
            }

            return elements[position];
        }

        if (source.Kind == DataKind.Vector)
        {
            if (index.Kind == DataKind.String)
            {
                throw new InvalidOperationException(
                    $"Named field access ('{Str(index, frame)}') is not supported on Vector — " +
                    $"use positional destructuring: LET (a, b, ...) = expr.");
            }

            float[] vector = source.AsVector();

            // Use 0-based integer index.
            int position = (int)ToFloat(index);
            if (position < 0 || position >= vector.Length)
            {
                return DataValue.Null(DataKind.Float32);
            }

            return DataValue.FromFloat32(vector[position]);
        }

        if (source.Kind == DataKind.Struct)
        {
            // Integer index → positional (ordinal) access by declaration order.
            if (index.Kind is DataKind.Float32 or DataKind.Float64 or DataKind.Int32 or DataKind.Int64)
            {
                DataValue[] fields = source.AsStruct(frame.Source);
                int position = (int)ToFloat(index);
                if (position < 0 || position >= fields.Length)
                {
                    return DataValue.NullStruct(source.StructFieldCount);
                }
                return fields[position];
            }

            // String index → named field access.
            return EvaluateStructFieldAccess(source, index, indexAccess, frame);
        }

        throw new InvalidOperationException(
            $"Index access is not supported on {source.Kind} values.");
    }

    private DataValue EvaluateStructFieldAccess(
        DataValue source, DataValue index, IndexAccessExpression indexAccess, in EvaluationFrame frame)
    {
        DataValue[] fields = source.AsStruct(frame.Source);
        string fieldName = Str(index, frame);

        // Try to resolve field position from schema when source is a column reference.
        if (indexAccess.Source is ColumnReference colRef)
        {
            IReadOnlyList<ColumnInfo>? columnFields = FindStructColumnFields(colRef, _sourceSchema);
            if (columnFields is not null)
            {
                return LookupFieldByName(fields, columnFields, fieldName, source.StructFieldCount);
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

            return DataValue.NullStruct(source.StructFieldCount);
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

                return DataValue.NullStruct(source.StructFieldCount);
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
        short fieldCount)
    {
        for (int i = 0; i < columnFields.Count; i++)
        {
            if (string.Equals(columnFields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return i < fields.Length ? fields[i] : DataValue.Null(columnFields[i].Kind);
            }
        }

        return DataValue.NullStruct(fieldCount);
    }

    /// <summary>
    /// Evaluates a case-sensitive LIKE expression. Converts SQL wildcards
    /// (<c>%</c> and <c>_</c>) into a regex pattern.
    /// </summary>
    private DataValue EvaluateLike(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("LIKE requires string operands.");
        }

        string input = Str(left, frame);
        string pattern = Str(right, frame);

        if (!_likeRegexCache.TryGetValue(pattern, out Regex? regex))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
            _likeRegexCache[pattern] = regex;
        }

        bool matches = regex.IsMatch(input);
        return DataValue.FromBoolean(matches);
    }

    /// <summary>
    /// Evaluates a case-insensitive ILIKE expression. Same wildcard conversion
    /// as LIKE but with <see cref="RegexOptions.IgnoreCase"/>.
    /// </summary>
    private DataValue EvaluateILike(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("ILIKE requires string operands.");
        }

        string input = Str(left, frame);
        string pattern = Str(right, frame);

        if (!_iLikeRegexCache.TryGetValue(pattern, out Regex? regex))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            regex = new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _iLikeRegexCache[pattern] = regex;
        }

        bool matches = regex.IsMatch(input);
        return DataValue.FromBoolean(matches);
    }

    /// <summary>
    /// Evaluates a REGEXP expression. The right operand is a user-supplied regular
    /// expression used for substring matching (unanchored). Uses
    /// <see cref="RegexOptions.NonBacktracking"/> to prevent catastrophic
    /// backtracking on adversarial patterns.
    /// </summary>
    private DataValue EvaluateRegexp(DataValue left, DataValue right, in EvaluationFrame frame)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("REGEXP requires string operands.");
        }

        string input = Str(left, frame);
        string pattern = Str(right, frame);

        if (!_regexpCache.TryGetValue(pattern, out Regex? regex))
        {
            regex = new Regex(pattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            _regexpCache[pattern] = regex;
        }

        bool matches = regex.IsMatch(input);
        return DataValue.FromBoolean(matches);
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
