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
/// Forces <see cref="LazyDataValue"/> on access so that WHERE/ON/ORDER BY
/// clauses materialize only the columns they reference.
/// </summary>
public sealed class ExpressionEvaluator
{
    private readonly FunctionRegistry _functions;
    private readonly QueryMeter? _meter;

    /// <summary>
    /// Compiled regex cache for LIKE patterns. Avoids recompiling
    /// the same SQL LIKE pattern on every row comparison.
    /// </summary>
    private readonly Dictionary<string, Regex> _likeRegexCache = new(StringComparer.Ordinal);

    /// <summary>
    /// Cached resolved DataKind for each CASE expression, computed once on first evaluation.
    /// Used to coerce branch results to a consistent type for downstream consumers.
    /// </summary>
    private readonly Dictionary<CaseExpression, DataKind?> _caseResolvedKindCache = new();

    /// <summary>
    /// Creates an evaluator that can resolve function calls.
    /// </summary>
    /// <param name="functions">Registry of available functions.</param>
    /// <param name="meter">Optional meter for accumulating Query Unit costs, or <see langword="null"/> for unmetered execution.</param>
    public ExpressionEvaluator(FunctionRegistry functions, QueryMeter? meter = null)
    {
        _functions = functions;
        _meter = meter;
    }

    /// <summary>
    /// Evaluates an expression tree against the given row and returns the result.
    /// </summary>
    /// <param name="expression">The AST expression to evaluate.</param>
    /// <param name="row">The current data row providing column values.</param>
    /// <returns>The computed result.</returns>
    public DataValue Evaluate(Expression expression, Row row)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteral(literal),
            ColumnReference column => EvaluateColumn(column, row),
            BinaryExpression binary => EvaluateBinary(binary, row),
            UnaryExpression unary => EvaluateUnary(unary, row),
            FunctionCallExpression function => EvaluateFunction(function, row),
            InExpression inExpr => EvaluateIn(inExpr, row),
            BetweenExpression between => EvaluateBetween(between, row),
            IsNullExpression isNull => EvaluateIsNull(isNull, row),
            CastExpression cast => EvaluateCast(cast, row),
            CaseExpression caseExpr => EvaluateCase(caseExpr, row),
            WindowFunctionCallExpression window => throw new InvalidOperationException(
                $"Window function '{window.FunctionName}' was not rewritten by the query planner. " +
                "Window functions must be used with an OVER clause and are only allowed in SELECT and ORDER BY."),
            ParameterExpression parameter => throw new InvalidOperationException(
                $"Unbound parameter '${parameter.Name}'. Parameters must be bound before evaluation."),
            _ => throw new InvalidOperationException(
                $"Unsupported expression type: {expression.GetType().Name}."),
        };
    }

    /// <summary>
    /// Evaluates an expression and interprets the result as a boolean (truthy/falsy).
    /// Null is treated as false. Scalar 0 is false; non-zero is true.
    /// </summary>
    public bool EvaluateAsBoolean(Expression expression, Row row)
    {
        DataValue result = Evaluate(expression, row);

        if (result.IsNull)
        {
            return false;
        }

        return result.Kind switch
        {
            DataKind.Boolean => result.AsBoolean(),
            DataKind.Scalar => result.AsScalar() != 0f,
            DataKind.UInt8 => result.AsUInt8() != 0,
            DataKind.String => !string.IsNullOrEmpty(result.AsString()),
            _ => true,
        };
    }

    private static DataValue EvaluateLiteral(LiteralExpression literal)
    {
        if (literal.Value is null)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return literal.Value switch
        {
            int intValue => DataValue.FromScalar(intValue),
            long longValue => DataValue.FromScalar(longValue),
            float floatValue => DataValue.FromScalar(floatValue),
            double doubleValue => DataValue.FromScalar((float)doubleValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromBoolean(boolValue),
            _ => throw new InvalidOperationException(
                $"Unsupported literal type: {literal.Value.GetType().Name}."),
        };
    }

    private static DataValue EvaluateColumn(ColumnReference column, Row row)
    {
        // For qualified references (table.column), try the full qualified name first,
        // then the unqualified column name.
        if (column.QualifiedName is not null)
        {
            if (row.TryGetValue(column.QualifiedName, out DataValue? qualifiedValue))
            {
                return qualifiedValue!;
            }
        }

        if (row.TryGetValue(column.ColumnName, out DataValue? value))
        {
            return value!;
        }

        throw new InvalidOperationException(
            column.TableName is not null
                ? $"Column '{column.TableName}.{column.ColumnName}' not found in row."
                : $"Column '{column.ColumnName}' not found in row.");
    }

    private DataValue EvaluateBinary(BinaryExpression binary, Row row)
    {
        // Short-circuit for AND/OR.
        if (binary.Operator == BinaryOperator.And)
        {
            DataValue left = Evaluate(binary.Left, row);
            if (left.IsNull || (left.Kind == DataKind.Scalar && left.AsScalar() == 0f))
            {
                return DataValue.FromScalar(0f);
            }

            DataValue right = Evaluate(binary.Right, row);
            if (right.IsNull || (right.Kind == DataKind.Scalar && right.AsScalar() == 0f))
            {
                return DataValue.FromScalar(0f);
            }

            return DataValue.FromScalar(1f);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            DataValue left = Evaluate(binary.Left, row);
            if (!left.IsNull && left.Kind == DataKind.Scalar && left.AsScalar() != 0f)
            {
                return DataValue.FromScalar(1f);
            }

            DataValue right = Evaluate(binary.Right, row);
            if (!right.IsNull && right.Kind == DataKind.Scalar && right.AsScalar() != 0f)
            {
                return DataValue.FromScalar(1f);
            }

            return DataValue.FromScalar(0f);
        }

        {
            DataValue left = Evaluate(binary.Left, row);
            DataValue right = Evaluate(binary.Right, row);

            // NULL propagation: any operation with NULL yields NULL (except IS NULL checks).
            if (left.IsNull || right.IsNull)
            {
                return DataValue.Null(DataKind.Scalar);
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
                BinaryOperator.Equal => CompareValues(left, right, 0),
                BinaryOperator.NotEqual => CompareValues(left, right, 0, negate: true),
                BinaryOperator.LessThan => CompareValues(left, right, -1),
                BinaryOperator.GreaterThan => CompareValues(left, right, 1),
                BinaryOperator.LessThanOrEqual => CompareValuesLe(left, right),
                BinaryOperator.GreaterThanOrEqual => CompareValuesGe(left, right),
                BinaryOperator.Like => EvaluateLike(left, right),
                _ => throw new InvalidOperationException(
                    $"Unsupported binary operator: {binary.Operator}."),
            };
        }
    }

    private DataValue EvaluateUnary(UnaryExpression unary, Row row)
    {
        DataValue operand = Evaluate(unary.Operand, row);

        if (operand.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => DataValue.FromScalar(
                operand.Kind == DataKind.Scalar && operand.AsScalar() == 0f ? 1f : 0f),
            UnaryOperator.Negate => DataValue.FromScalar(-ToFloat(operand)),
            _ => throw new InvalidOperationException(
                $"Unsupported unary operator: {unary.Operator}."),
        };
    }

    private DataValue EvaluateFunction(FunctionCallExpression function, Row row)
    {
        IScalarFunction? scalarFunction = _functions.TryGetScalar(function.FunctionName);

        if (scalarFunction is null)
        {
            throw new InvalidOperationException(
                $"Unknown function: '{function.FunctionName}'.");
        }

        int argumentCount = function.Arguments.Count;
        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(argumentCount);
        try
        {
            for (int index = 0; index < argumentCount; index++)
            {
                arguments[index] = Evaluate(function.Arguments[index], row);
            }

            DataValue result = scalarFunction.Execute(arguments.AsSpan(0, argumentCount));

            _meter?.Add(scalarFunction.QueryUnitCost);
        if (_meter is not null && scalarFunction is ICostAwareFunction costAware)
        {
            _meter.Add(costAware.ComputeSupplementalCost(arguments.AsSpan(0, argumentCount), result));
        }
            // Dispose intermediate ImageHandle arguments whose bitmaps are no longer needed.
            // Handles still referenced by the source row are kept alive — they may appear
            // as ordinal copies in the projected row (e.g. SELECT *, func(image)).
            DisposeConsumedImageHandles(arguments.AsSpan(0, argumentCount), result, row);

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

    private DataValue EvaluateIn(InExpression inExpr, Row row)
    {
        DataValue target = Evaluate(inExpr.Expression, row);

        if (target.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        foreach (Expression valueExpression in inExpr.Values)
        {
            DataValue candidate = Evaluate(valueExpression, row);
            if (!candidate.IsNull && target.Equals(candidate))
            {
                return DataValue.FromScalar(inExpr.Negated ? 0f : 1f);
            }
        }

        return DataValue.FromScalar(inExpr.Negated ? 1f : 0f);
    }

    private DataValue EvaluateBetween(BetweenExpression between, Row row)
    {
        DataValue target = Evaluate(between.Expression, row);
        DataValue low = Evaluate(between.Low, row);
        DataValue high = Evaluate(between.High, row);

        if (target.IsNull || low.IsNull || high.IsNull)
        {
            return DataValue.Null(DataKind.Scalar);
        }

        float targetValue = ToFloat(target);
        float lowValue = ToFloat(low);
        float highValue = ToFloat(high);

        bool inRange = targetValue >= lowValue && targetValue <= highValue;
        if (between.Negated)
        {
            inRange = !inRange;
        }

        return DataValue.FromScalar(inRange ? 1f : 0f);
    }

    private DataValue EvaluateIsNull(IsNullExpression isNull, Row row)
    {
        DataValue value = Evaluate(isNull.Expression, row);
        bool result = value.IsNull;

        if (isNull.Negated)
        {
            result = !result;
        }

        return DataValue.FromScalar(result ? 1f : 0f);
    }

    /// <summary>
    /// Cached <see cref="DataValue"/> wrappers for CAST target type strings, keyed by
    /// the type name (e.g. "Scalar", "UInt8"). Avoids allocating a new DataValue per row.
    /// </summary>
    private readonly Dictionary<string, DataValue> _castTargetCache = new(StringComparer.OrdinalIgnoreCase);

    private DataValue EvaluateCast(CastExpression cast, Row row)
    {
        DataValue value = Evaluate(cast.Expression, row);

        IScalarFunction? castFunction = _functions.TryGetScalar("cast");
        if (castFunction is null)
        {
            throw new InvalidOperationException("Cast function not registered.");
        }

        if (!_castTargetCache.TryGetValue(cast.TargetType, out DataValue? targetTypeValue))
        {
            targetTypeValue = DataValue.FromString(cast.TargetType);
            _castTargetCache[cast.TargetType] = targetTypeValue;
        }

        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(2);
        try
        {
            arguments[0] = value;
            arguments[1] = targetTypeValue;
            return castFunction.Execute(arguments.AsSpan(0, 2));
        }
        finally
        {
            arguments.AsSpan(0, 2).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Evaluates a CASE expression with short-circuit semantics and implicit
    /// branch type coercion. On first evaluation, resolves the common output type
    /// across all branches; subsequent evaluations reuse the cached result.
    /// Simple CASE compares the operand against each WHEN value for equality.
    /// Searched CASE evaluates each WHEN condition as a boolean predicate.
    /// Only the matching THEN branch is evaluated.
    /// </summary>
    private DataValue EvaluateCase(CaseExpression caseExpression, Row row)
    {
        DataValue result = EvaluateCaseBranch(caseExpression, row);

        // Resolve target kind once per CaseExpression and coerce the result.
        if (!_caseResolvedKindCache.TryGetValue(caseExpression, out DataKind? resolvedKind))
        {
            resolvedKind = ResolveCaseTargetKind(caseExpression, row);
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
    private DataValue EvaluateCaseBranch(CaseExpression caseExpression, Row row)
    {
        if (caseExpression.Operand is not null)
        {
            // Simple CASE: compare operand against each WHEN value.
            DataValue operand = Evaluate(caseExpression.Operand, row);

            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                DataValue whenValue = Evaluate(whenClause.Condition, row);
                if (!operand.IsNull && !whenValue.IsNull && operand.Equals(whenValue))
                {
                    return Evaluate(whenClause.Result, row);
                }
            }
        }
        else
        {
            // Searched CASE: evaluate each WHEN condition as boolean.
            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                if (EvaluateAsBoolean(whenClause.Condition, row))
                {
                    return Evaluate(whenClause.Result, row);
                }
            }
        }

        // No match: return ELSE result or typed null.
        if (caseExpression.ElseResult is not null)
        {
            return Evaluate(caseExpression.ElseResult, row);
        }

        return DataValue.Null(DataKind.Scalar);
    }

    /// <summary>
    /// Resolves the target DataKind for a CASE expression by building a schema from
    /// the current row and delegating to <see cref="ExpressionTypeResolver"/>.
    /// Falls back to AST-level inference when a row-derived schema cannot be built.
    /// </summary>
    private DataKind? ResolveCaseTargetKind(CaseExpression caseExpression, Row row)
    {
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
            LiteralExpression { Value: string } => DataKind.String,
            LiteralExpression { Value: int or long or float or double } => DataKind.Scalar,
            LiteralExpression { Value: bool } => DataKind.Boolean,
            LiteralExpression { Value: null } => DataKind.Scalar,
            CastExpression cast => ExpressionTypeResolver.ResolveCastTargetKind(cast.TargetType),
            BinaryExpression => DataKind.Scalar,
            UnaryExpression => DataKind.Scalar,
            InExpression => DataKind.Scalar,
            BetweenExpression => DataKind.Scalar,
            IsNullExpression => DataKind.Scalar,
            _ => null,
        };
    }

    // ──────────────────── Arithmetic helpers ────────────────────

    private static DataValue ArithmeticOp(DataValue left, DataValue right, Func<float, float, float> operation)
    {
        float leftValue = ToFloat(left);
        float rightValue = ToFloat(right);
        return DataValue.FromScalar(operation(leftValue, rightValue));
    }

    private static float ToFloat(DataValue value)
    {
        return value.Kind switch
        {
            DataKind.Scalar => value.AsScalar(),
            DataKind.UInt8 => value.AsUInt8(),
            DataKind.Boolean => value.AsBoolean() ? 1f : 0f,
            DataKind.Duration => (float)value.AsDuration().TotalSeconds,
            DataKind.Time => (float)(value.AsTime().Hour * 3600 + value.AsTime().Minute * 60 + value.AsTime().Second + value.AsTime().Millisecond / 1000.0),
            DataKind.String => float.TryParse(value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : throw new InvalidOperationException($"Cannot convert string '{value.AsString()}' to number."),
            _ => throw new InvalidOperationException($"Cannot use {value.Kind} in arithmetic."),
        };
    }

    // ──────────────────── Comparison helpers ────────────────────

    private static DataValue CompareValues(DataValue left, DataValue right, int expectedSign, bool negate = false)
    {
        int comparison = CompareDataValues(left, right);
        bool result = expectedSign == 0 ? comparison == 0 : (expectedSign < 0 ? comparison < 0 : comparison > 0);
        if (negate)
        {
            result = !result;
        }

        return DataValue.FromScalar(result ? 1f : 0f);
    }

    private static DataValue CompareValuesLe(DataValue left, DataValue right)
    {
        int comparison = CompareDataValues(left, right);
        return DataValue.FromScalar(comparison <= 0 ? 1f : 0f);
    }

    private static DataValue CompareValuesGe(DataValue left, DataValue right)
    {
        int comparison = CompareDataValues(left, right);
        return DataValue.FromScalar(comparison >= 0 ? 1f : 0f);
    }

    private static int CompareDataValues(DataValue left, DataValue right)
    {
        // Compare as strings if both are strings.
        if (left.Kind == DataKind.String && right.Kind == DataKind.String)
        {
            return string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal);
        }

        // Compare dates.
        if (left.Kind == DataKind.Date && right.Kind == DataKind.Date)
        {
            return left.AsDate().CompareTo(right.AsDate());
        }

        if (left.Kind == DataKind.DateTime && right.Kind == DataKind.DateTime)
        {
            return left.AsDateTime().CompareTo(right.AsDateTime());
        }

        // Compare UUIDs.
        if (left.Kind == DataKind.Uuid && right.Kind == DataKind.Uuid)
        {
            return left.AsUuid().CompareTo(right.AsUuid());
        }

        // Compare booleans (false < true).
        if (left.Kind == DataKind.Boolean && right.Kind == DataKind.Boolean)
        {
            return left.AsBoolean().CompareTo(right.AsBoolean());
        }

        // Compare times.
        if (left.Kind == DataKind.Time && right.Kind == DataKind.Time)
        {
            return left.AsTime().CompareTo(right.AsTime());
        }

        // Compare durations.
        if (left.Kind == DataKind.Duration && right.Kind == DataKind.Duration)
        {
            return left.AsDuration().CompareTo(right.AsDuration());
        }

        // Otherwise compare as floats.
        float leftValue = ToFloat(left);
        float rightValue = ToFloat(right);
        return leftValue.CompareTo(rightValue);
    }

    // ──────────────────── LIKE pattern matching ────────────────────

    private DataValue EvaluateLike(DataValue left, DataValue right)
    {
        if (left.Kind != DataKind.String || right.Kind != DataKind.String)
        {
            throw new InvalidOperationException("LIKE requires string operands.");
        }

        string input = left.AsString();
        string pattern = right.AsString();

        if (!_likeRegexCache.TryGetValue(pattern, out Regex? regex))
        {
            string regexPattern = "^" + Regex.Escape(pattern)
                .Replace("%", ".*", StringComparison.Ordinal)
                .Replace("_", ".", StringComparison.Ordinal) + "$";

            regex = new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            _likeRegexCache[pattern] = regex;
        }

        bool matches = regex.IsMatch(input);
        return DataValue.FromScalar(matches ? 1f : 0f);
    }
}
