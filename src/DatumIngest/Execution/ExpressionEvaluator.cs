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

            // Dispose consumed ImageHandle arguments whose bitmaps are no longer needed.
            // The result carries its own ImageHandle (if any), so we only dispose handles
            // that are not the same instance as the result's handle.
            DisposeConsumedImageHandles(arguments.AsSpan(0, argumentCount), result);

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
    /// no longer referenced by the result. This releases native <see cref="SkiaSharp.SKBitmap"/>
    /// memory from intermediate pipeline stages.
    /// </summary>
    private static void DisposeConsumedImageHandles(ReadOnlySpan<DataValue> arguments, DataValue result)
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

            argumentHandle.Dispose();
        }
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
