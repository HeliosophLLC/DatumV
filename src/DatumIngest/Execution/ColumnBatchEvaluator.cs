using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Evaluates AST <see cref="Expression"/> nodes against an entire <see cref="ColumnBatch"/>
/// in a column-at-a-time fashion, eliminating per-row expression dispatch overhead.
/// </summary>
/// <remarks>
/// <para>
/// Where <see cref="ExpressionEvaluator"/> evaluates one expression per row (requiring
/// N × depth virtual dispatch calls per batch), this evaluator dispatches once per
/// expression node and executes tight inner loops over the full column.  This enables
/// better branch prediction, SIMD auto-vectorization, and cache locality.
/// </para>
/// <para>
/// Intermediate result columns are rented from <see cref="ArrayPool{T}.Shared"/> and
/// tracked for return on <see cref="Dispose"/>.  Column references return the batch's
/// own buffer (zero-copy).  Callers must dispose the evaluator after consuming results.
/// </para>
/// </remarks>
public sealed class ColumnBatchEvaluator : IDisposable
{
    private readonly FunctionRegistry _functions;
    private readonly List<DataValue[]> _rentedBuffers = new();
    private readonly Dictionary<string, Regex> _likeRegexCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _iLikeRegexCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Regex> _regexpCache = new(StringComparer.Ordinal);
    private readonly Dictionary<InExpression, (HashSet<DataValue> NonNullValues, bool HasNull)> _inValueSetCache = new();
    private bool _disposed;

    /// <summary>
    /// Creates a column-batch evaluator that can resolve function calls.
    /// </summary>
    /// <param name="functions">Registry of available scalar functions.</param>
    public ColumnBatchEvaluator(FunctionRegistry functions)
    {
        _functions = functions;
    }

    /// <summary>
    /// Evaluates an expression tree against every row in the batch, returning a
    /// result column with <see cref="ColumnBatch.RowCount"/> valid elements.
    /// </summary>
    /// <remarks>
    /// For <see cref="ColumnReference"/> expressions the batch's own internal buffer
    /// is returned (zero-copy, not owned by this evaluator).  For all other expression
    /// types a buffer is rented from <see cref="ArrayPool{T}.Shared"/> and will be
    /// returned when this evaluator is disposed.
    /// </remarks>
    /// <param name="expression">The AST expression to evaluate.</param>
    /// <param name="batch">The column batch providing column data.</param>
    /// <returns>
    /// A <see cref="DataValue"/> array with at least <see cref="ColumnBatch.RowCount"/>
    /// valid elements containing the per-row results.
    /// </returns>
    public DataValue[] EvaluateColumn(Expression expression, ColumnBatch batch)
    {
        return expression switch
        {
            LiteralExpression literal => EvaluateLiteralColumn(literal, batch.RowCount),
            ColumnReference column => EvaluateColumnReference(column, batch),
            BinaryExpression binary => EvaluateBinaryColumn(binary, batch),
            UnaryExpression unary => EvaluateUnaryColumn(unary, batch),
            FunctionCallExpression function => EvaluateFunctionColumn(function, batch),
            InExpression inExpression => EvaluateInColumn(inExpression, batch),
            BetweenExpression between => EvaluateBetweenColumn(between, batch),
            IsNullExpression isNull => EvaluateIsNullColumn(isNull, batch),
            CastExpression cast => EvaluateCastColumn(cast, batch),
            AtTimeZoneExpression atz => EvaluateAtTimeZoneColumn(atz, batch),
            CaseExpression caseExpression => EvaluateCaseColumn(caseExpression, batch),
            LikeExpression like => EvaluateLikeColumn(like, batch),
            WindowFunctionCallExpression window => throw new InvalidOperationException(
                $"Window function '{window.FunctionName}' was not rewritten by the query planner."),
            SubqueryExpression => throw new InvalidOperationException(
                "Subquery expression was not rewritten by the query planner."),
            InSubqueryExpression => throw new InvalidOperationException(
                "IN (SELECT ...) was not rewritten by the query planner into a semi-join."),
            ExistsExpression => throw new InvalidOperationException(
                "[NOT] EXISTS (SELECT ...) was not rewritten by the query planner into a semi-join."),
            ParameterExpression parameter => throw new InvalidOperationException(
                $"Unbound parameter '${parameter.Name}'."),
            TypeLiteralExpression typeLiteral => EvaluateTypeLiteralColumn(typeLiteral, batch.RowCount),
            _ => throw new InvalidOperationException(
                $"Unsupported expression type: {expression.GetType().Name}."),
        };
    }

    /// <summary>
    /// Evaluates a boolean predicate against every row in the batch and writes the
    /// indices of matching (truthy) rows into <paramref name="selectedIndices"/>.
    /// </summary>
    /// <param name="predicate">The filter predicate expression.</param>
    /// <param name="batch">The column batch to filter.</param>
    /// <param name="selectedIndices">
    /// Caller-provided buffer that must have at least <see cref="ColumnBatch.RowCount"/>
    /// elements.  Matching row indices are written starting at position 0.
    /// </param>
    /// <returns>The number of selected rows written into <paramref name="selectedIndices"/>.</returns>
    public int EvaluateFilter(Expression predicate, ColumnBatch batch, int[] selectedIndices)
    {
        DataValue[] results = EvaluateColumn(predicate, batch);
        int rowCount = batch.RowCount;
        int selectedCount = 0;

        for (int row = 0; row < rowCount; row++)
        {
            if (IsTruthy(results[row]))
            {
                selectedIndices[selectedCount++] = row;
            }
        }

        return selectedCount;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        for (int index = 0; index < _rentedBuffers.Count; index++)
        {
            DataValue[] buffer = _rentedBuffers[index];
            buffer.AsSpan().Clear();
            ArrayPool<DataValue>.Shared.Return(buffer);
        }

        _rentedBuffers.Clear();
    }

    // ───────────────────────── Buffer management ─────────────────────────

    private DataValue[] RentBuffer(int rowCount)
    {
        DataValue[] buffer = ArrayPool<DataValue>.Shared.Rent(rowCount);
        _rentedBuffers.Add(buffer);
        return buffer;
    }

    // ───────────────────────── Literal ─────────────────────────

    private DataValue[] EvaluateLiteralColumn(LiteralExpression literal, int rowCount)
    {
        DataValue value = ResolveLiteral(literal);
        DataValue[] result = RentBuffer(rowCount);
        result.AsSpan(0, rowCount).Fill(value);
        return result;
    }

    private DataValue[] EvaluateTypeLiteralColumn(TypeLiteralExpression typeLiteral, int rowCount)
    {
        if (!Enum.TryParse<DataKind>(typeLiteral.TypeName, ignoreCase: true, out DataKind kind))
        {
            throw new InvalidOperationException(
                $"Unknown type name: '{typeLiteral.TypeName}'.");
        }

        DataValue value = DataValue.FromType(kind);
        DataValue[] result = RentBuffer(rowCount);
        result.AsSpan(0, rowCount).Fill(value);
        return result;
    }

    private static DataValue ResolveLiteral(LiteralExpression literal)
    {
        if (literal.Value is null)
        {
            return DataValue.UnknownNull();
        }

        return literal.Value switch
        {
            DataValue dataValue => dataValue,
            int intValue => DataValue.FromInt32(intValue),
            long longValue => DataValue.FromInt64(longValue),
            float floatValue => DataValue.FromFloat32(floatValue),
            double doubleValue => DataValue.FromFloat64(doubleValue),
            string stringValue => DataValue.FromString(stringValue),
            bool boolValue => DataValue.FromBoolean(boolValue),
            _ => throw new InvalidOperationException(
                $"Unsupported literal type: {literal.Value.GetType().Name}."),
        };
    }

    // ───────────────────────── Column reference ─────────────────────────

    /// <summary>
    /// Resolves a column reference to the batch's internal buffer.  The returned
    /// array is NOT owned by this evaluator — it belongs to the batch.
    /// </summary>
    private static DataValue[] EvaluateColumnReference(ColumnReference column, ColumnBatch batch)
    {
        if (column.QualifiedName is not null &&
            batch.TryGetColumnOrdinal(column.QualifiedName, out int qualifiedOrdinal))
        {
            return batch.GetColumnBuffer(qualifiedOrdinal);
        }

        if (batch.TryGetColumnOrdinal(column.ColumnName, out int ordinal))
        {
            return batch.GetColumnBuffer(ordinal);
        }

        throw new InvalidOperationException(
            column.TableName is not null
                ? $"Column '{column.TableName}.{column.ColumnName}' not found in column batch."
                : $"Column '{column.ColumnName}' not found in column batch.");
    }

    // ───────────────────────── Binary ─────────────────────────

    private DataValue[] EvaluateBinaryColumn(BinaryExpression binary, ColumnBatch batch)
    {
        int rowCount = batch.RowCount;

        // AND: evaluate both sides, combine element-wise.
        if (binary.Operator == BinaryOperator.And)
        {
            DataValue[] left = EvaluateColumn(binary.Left, batch);
            DataValue[] right = EvaluateColumn(binary.Right, batch);
            DataValue[] result = RentBuffer(rowCount);

            DataValue trueValue = DataValue.FromBoolean(true);
            DataValue falseValue = DataValue.FromBoolean(false);

            for (int row = 0; row < rowCount; row++)
            {
                result[row] = IsTruthy(left[row]) && IsTruthy(right[row])
                    ? trueValue
                    : falseValue;
            }

            return result;
        }

        // OR: evaluate both sides, combine element-wise.
        if (binary.Operator == BinaryOperator.Or)
        {
            DataValue[] left = EvaluateColumn(binary.Left, batch);
            DataValue[] right = EvaluateColumn(binary.Right, batch);
            DataValue[] result = RentBuffer(rowCount);

            DataValue trueValue = DataValue.FromBoolean(true);
            DataValue falseValue = DataValue.FromBoolean(false);

            for (int row = 0; row < rowCount; row++)
            {
                result[row] = IsTruthy(left[row]) || IsTruthy(right[row])
                    ? trueValue
                    : falseValue;
            }

            return result;
        }

        {
            DataValue[] left = EvaluateColumn(binary.Left, batch);
            DataValue[] right = EvaluateColumn(binary.Right, batch);
            DataValue[] result = RentBuffer(rowCount);

            DataValue nullResult = DataValue.Null(DataKind.Float32);

            switch (binary.Operator)
            {
                case BinaryOperator.Add:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => a + b);
                    break;

                case BinaryOperator.Subtract:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => a - b);
                    break;

                case BinaryOperator.Multiply:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => a * b);
                    break;

                case BinaryOperator.Divide:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => b != 0f ? a / b : float.NaN);
                    break;

                case BinaryOperator.Modulo:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => b != 0f ? a % b : float.NaN);
                    break;

                case BinaryOperator.Power:
                    EvaluateArithmeticColumn(left, right, result, rowCount, nullResult,
                        static (a, b) => MathF.Pow(a, b));
                    break;

                case BinaryOperator.Equal:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison == 0);
                    break;

                case BinaryOperator.NotEqual:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison != 0);
                    break;

                case BinaryOperator.LessThan:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison < 0);
                    break;

                case BinaryOperator.GreaterThan:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison > 0);
                    break;

                case BinaryOperator.LessThanOrEqual:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison <= 0);
                    break;

                case BinaryOperator.GreaterThanOrEqual:
                    EvaluateComparisonColumn(left, right, result, rowCount, batch,
                        static comparison => comparison >= 0);
                    break;

                case BinaryOperator.Like:
                    EvaluatePatternColumn(left, right, result, rowCount, batch, _likeRegexCache,
                        RegexOptions.CultureInvariant);
                    break;

                case BinaryOperator.ILike:
                    EvaluatePatternColumn(left, right, result, rowCount, batch, _iLikeRegexCache,
                        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                    break;

                case BinaryOperator.Regexp:
                    EvaluateRegexpColumn(left, right, result, rowCount, batch);
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unsupported binary operator: {binary.Operator}.");
            }

            return result;
        }
    }

    private void EvaluateArithmeticColumn(
        DataValue[] left, DataValue[] right, DataValue[] result, int rowCount,
        DataValue nullResult, Func<float, float, float> operation)
    {
        for (int row = 0; row < rowCount; row++)
        {
            DataValue leftValue = left[row];
            DataValue rightValue = right[row];

            if (leftValue.IsNull || rightValue.IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            // Duration + Duration preserves TimeSpan.
            if (leftValue.Kind == DataKind.Duration && rightValue.Kind == DataKind.Duration)
            {
                TimeSpan leftDuration = leftValue.AsDuration();
                TimeSpan rightDuration = rightValue.AsDuration();
                float ticksResult = operation((float)leftDuration.Ticks, (float)rightDuration.Ticks);
                result[row] = DataValue.FromDuration(
                    new TimeSpan(float.IsFinite(ticksResult) ? (long)ticksResult : 0));
                continue;
            }

            result[row] = DataValue.FromFloat32(
                operation(ToFloat(leftValue), ToFloat(rightValue)));
        }
    }

    private static void EvaluateComparisonColumn(
        DataValue[] left, DataValue[] right, DataValue[] result, int rowCount,
        ColumnBatch batch, Func<int, bool> predicate)
    {
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        for (int row = 0; row < rowCount; row++)
        {
            DataValue leftValue = left[row];
            DataValue rightValue = right[row];

            if (leftValue.IsNull || rightValue.IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            int comparison = CompareDataValues(leftValue, rightValue, batch);
            result[row] = predicate(comparison) ? trueValue : falseValue;
        }
    }

    private void EvaluatePatternColumn(
        DataValue[] left, DataValue[] right, DataValue[] result, int rowCount,
        ColumnBatch batch, Dictionary<string, Regex> cache, RegexOptions options)
    {
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        for (int row = 0; row < rowCount; row++)
        {
            DataValue leftValue = left[row];
            DataValue rightValue = right[row];

            if (leftValue.IsNull || rightValue.IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            string input = ResolveString(leftValue, batch);
            string pattern = ResolveString(rightValue, batch);

            if (!cache.TryGetValue(pattern, out Regex? regex))
            {
                string regexPattern = "^" + Regex.Escape(pattern)
                    .Replace("%", ".*", StringComparison.Ordinal)
                    .Replace("_", ".", StringComparison.Ordinal) + "$";
                regex = new Regex(regexPattern, RegexOptions.Compiled | options);
                cache[pattern] = regex;
            }

            result[row] = regex.IsMatch(input) ? trueValue : falseValue;
        }
    }

    private void EvaluateRegexpColumn(
        DataValue[] left, DataValue[] right, DataValue[] result, int rowCount,
        ColumnBatch batch)
    {
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        for (int row = 0; row < rowCount; row++)
        {
            DataValue leftValue = left[row];
            DataValue rightValue = right[row];

            if (leftValue.IsNull || rightValue.IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            string input = ResolveString(leftValue, batch);
            string pattern = ResolveString(rightValue, batch);

            if (!_regexpCache.TryGetValue(pattern, out Regex? regex))
            {
                regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                _regexpCache[pattern] = regex;
            }

            result[row] = regex.IsMatch(input) ? trueValue : falseValue;
        }
    }

    // ───────────────────────── Unary ─────────────────────────

    private DataValue[] EvaluateUnaryColumn(UnaryExpression unary, ColumnBatch batch)
    {
        DataValue[] operand = EvaluateColumn(unary.Operand, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);

        switch (unary.Operator)
        {
            case UnaryOperator.Not:
                DataValue notNullResult = DataValue.Null(DataKind.Boolean);
                DataValue trueValue = DataValue.FromBoolean(true);
                DataValue falseValue = DataValue.FromBoolean(false);
                for (int row = 0; row < rowCount; row++)
                {
                    if (operand[row].IsNull)
                    {
                        result[row] = notNullResult;
                        continue;
                    }

                    result[row] = IsTruthy(operand[row]) ? falseValue : trueValue;
                }
                break;

            case UnaryOperator.Negate:
                DataValue negateNullResult = DataValue.Null(DataKind.Float32);
                for (int row = 0; row < rowCount; row++)
                {
                    if (operand[row].IsNull)
                    {
                        result[row] = negateNullResult;
                        continue;
                    }

                    result[row] = DataValue.FromFloat32(-ToFloat(operand[row]));
                }
                break;

            default:
                throw new InvalidOperationException(
                    $"Unsupported unary operator: {unary.Operator}.");
        }

        return result;
    }

    // ───────────────────────── IS NULL ─────────────────────────

    private DataValue[] EvaluateIsNullColumn(IsNullExpression isNull, ColumnBatch batch)
    {
        DataValue[] operand = EvaluateColumn(isNull.Expression, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);

        if (isNull.Negated)
        {
            for (int row = 0; row < rowCount; row++)
            {
                result[row] = operand[row].IsNull ? falseValue : trueValue;
            }
        }
        else
        {
            for (int row = 0; row < rowCount; row++)
            {
                result[row] = operand[row].IsNull ? trueValue : falseValue;
            }
        }

        return result;
    }

    // ───────────────────────── BETWEEN ─────────────────────────

    private DataValue[] EvaluateBetweenColumn(BetweenExpression between, ColumnBatch batch)
    {
        DataValue[] target = EvaluateColumn(between.Expression, batch);
        DataValue[] low = EvaluateColumn(between.Low, batch);
        DataValue[] high = EvaluateColumn(between.High, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        for (int row = 0; row < rowCount; row++)
        {
            if (target[row].IsNull || low[row].IsNull || high[row].IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            float targetValue = ToFloat(target[row]);
            float lowValue = ToFloat(low[row]);
            float highValue = ToFloat(high[row]);

            bool inRange = targetValue >= lowValue && targetValue <= highValue;
            if (between.Negated)
            {
                inRange = !inRange;
            }

            result[row] = inRange ? trueValue : falseValue;
        }

        return result;
    }

    // ───────────────────────── IN ─────────────────────────

    private DataValue[] EvaluateInColumn(InExpression inExpression, ColumnBatch batch)
    {
        DataValue[] target = EvaluateColumn(inExpression.Expression, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);
        DataValue trueValue = DataValue.FromBoolean(!inExpression.Negated);
        DataValue falseValue = DataValue.FromBoolean(inExpression.Negated);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        // Fast path: all values are literals → build a HashSet once.
        if (TryGetOrBuildLiteralValueSet(inExpression, out HashSet<DataValue> valueSet, out bool hasNullCandidate))
        {
            for (int row = 0; row < rowCount; row++)
            {
                DataValue targetValue = target[row];

                if (targetValue.IsNull)
                {
                    result[row] = nullResult;
                    continue;
                }

                bool found = valueSet.Contains(targetValue);

                // Cross-type fallback: numeric comparison when kinds differ.
                if (!found)
                {
                    foreach (DataValue candidate in valueSet)
                    {
                        if (CompareDataValues(targetValue, candidate, batch) == 0)
                        {
                            found = true;
                            break;
                        }
                    }
                }

                if (found)
                {
                    result[row] = trueValue;
                }
                else if (hasNullCandidate)
                {
                    result[row] = nullResult;
                }
                else
                {
                    result[row] = falseValue;
                }
            }

            return result;
        }

        // Slow path: evaluate list values per row.
        DataValue[][] valueColumns = new DataValue[inExpression.Values.Count][];
        for (int index = 0; index < inExpression.Values.Count; index++)
        {
            valueColumns[index] = EvaluateColumn(inExpression.Values[index], batch);
        }

        for (int row = 0; row < rowCount; row++)
        {
            DataValue targetValue = target[row];

            if (targetValue.IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            bool found = false;
            bool hasNull = false;

            for (int index = 0; index < valueColumns.Length; index++)
            {
                DataValue candidate = valueColumns[index][row];
                if (candidate.IsNull)
                {
                    hasNull = true;
                    continue;
                }

                if (CompareDataValues(targetValue, candidate, batch) == 0)
                {
                    found = true;
                    break;
                }
            }

            if (found)
            {
                result[row] = trueValue;
            }
            else if (hasNull)
            {
                result[row] = nullResult;
            }
            else
            {
                result[row] = falseValue;
            }
        }

        return result;
    }

    // ───────────────────────── CASE ─────────────────────────

    private DataValue[] EvaluateCaseColumn(CaseExpression caseExpression, ColumnBatch batch)
    {
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);

        // Track which rows have already been resolved so later branches skip them.
        bool[] resolved = ArrayPool<bool>.Shared.Rent(rowCount);
        Array.Clear(resolved, 0, rowCount);

        try
        {
            if (caseExpression.Operand is not null)
            {
                // Simple CASE: compare operand against each WHEN value.
                DataValue[] operand = EvaluateColumn(caseExpression.Operand, batch);

                foreach (WhenClause whenClause in caseExpression.WhenClauses)
                {
                    DataValue[] whenValue = EvaluateColumn(whenClause.Condition, batch);
                    DataValue[] thenValue = EvaluateColumn(whenClause.Result, batch);

                    for (int row = 0; row < rowCount; row++)
                    {
                        if (resolved[row]) continue;

                        if (!operand[row].IsNull && !whenValue[row].IsNull &&
                            CompareDataValues(operand[row], whenValue[row], batch) == 0)
                        {
                            result[row] = thenValue[row];
                            resolved[row] = true;
                        }
                    }
                }
            }
            else
            {
                // Searched CASE: evaluate each WHEN condition as boolean.
                foreach (WhenClause whenClause in caseExpression.WhenClauses)
                {
                    DataValue[] condition = EvaluateColumn(whenClause.Condition, batch);
                    DataValue[] thenValue = EvaluateColumn(whenClause.Result, batch);

                    for (int row = 0; row < rowCount; row++)
                    {
                        if (resolved[row]) continue;

                        if (IsTruthy(condition[row]))
                        {
                            result[row] = thenValue[row];
                            resolved[row] = true;
                        }
                    }
                }
            }

            // Fill unresolved rows with ELSE or typed null.
            if (caseExpression.ElseResult is not null)
            {
                DataValue[] elseValues = EvaluateColumn(caseExpression.ElseResult, batch);

                for (int row = 0; row < rowCount; row++)
                {
                    if (!resolved[row])
                    {
                        result[row] = elseValues[row];
                    }
                }
            }
            else
            {
                // Determine the null kind from the first resolved (non-null) result
                // so unresolved rows have a consistent type with resolved ones.
                DataKind nullKind = DataKind.Float32;
                for (int row = 0; row < rowCount; row++)
                {
                    if (resolved[row] && !result[row].IsNull)
                    {
                        nullKind = result[row].Kind;
                        break;
                    }
                }

                DataValue nullValue = DataValue.Null(nullKind);
                for (int row = 0; row < rowCount; row++)
                {
                    if (!resolved[row])
                    {
                        result[row] = nullValue;
                    }
                }
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(resolved);
        }

        return result;
    }

    // ───────────────────────── CAST ─────────────────────────

    private DataValue[] EvaluateCastColumn(CastExpression cast, ColumnBatch batch)
    {
        DataValue[] source = EvaluateColumn(cast.Expression, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);

        IScalarFunction? castFunction = _functions.TryGetScalar("cast");
        if (castFunction is null)
        {
            throw new InvalidOperationException("Cast function not registered.");
        }

        DataValue targetTypeValue = DataValue.FromString(cast.TargetType);
        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(2);
        try
        {
            arguments[1] = targetTypeValue;

            for (int row = 0; row < rowCount; row++)
            {
                arguments[0] = source[row];
                result[row] = castFunction.Execute(arguments.AsSpan(0, 2));
            }
        }
        finally
        {
            arguments.AsSpan(0, 2).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
        }

        return result;
    }

    // ───────────────────────── AT TIME ZONE ─────────────────────────

    private readonly Dictionary<string, TimeZoneInfo> _timeZoneCache = new(StringComparer.OrdinalIgnoreCase);

    private DataValue[] EvaluateAtTimeZoneColumn(AtTimeZoneExpression atz, ColumnBatch batch)
    {
        DataValue[] source = EvaluateColumn(atz.Expression, batch);
        DataValue[] tzValues = EvaluateColumn(atz.TimeZone, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);

        for (int row = 0; row < rowCount; row++)
        {
            DataValue value = source[row];
            if (value.IsNull)
            {
                result[row] = DataValue.Null(DataKind.DateTime);
                continue;
            }

            string tzName = tzValues[row].AsString();
            if (!_timeZoneCache.TryGetValue(tzName, out TimeZoneInfo? tz))
            {
                tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
                _timeZoneCache[tzName] = tz;
            }

            DateTimeOffset converted = TimeZoneInfo.ConvertTime(value.ToDateTimeOffset(), tz);
            result[row] = DataValue.FromDateTime(converted);
        }

        return result;
    }

    // ───────────────────────── LIKE with ESCAPE ─────────────────────────

    private DataValue[] EvaluateLikeColumn(LikeExpression like, ColumnBatch batch)
    {
        DataValue[] input = EvaluateColumn(like.Expression, batch);
        DataValue[] pattern = EvaluateColumn(like.Pattern, batch);
        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);
        DataValue trueValue = DataValue.FromBoolean(true);
        DataValue falseValue = DataValue.FromBoolean(false);
        DataValue nullResult = DataValue.Null(DataKind.Boolean);

        RegexOptions options = like.CaseInsensitive
            ? RegexOptions.CultureInvariant | RegexOptions.IgnoreCase
            : RegexOptions.CultureInvariant;
        Dictionary<string, Regex> cache = like.CaseInsensitive ? _iLikeRegexCache : _likeRegexCache;

        for (int row = 0; row < rowCount; row++)
        {
            if (input[row].IsNull || pattern[row].IsNull)
            {
                result[row] = nullResult;
                continue;
            }

            string inputString = ResolveString(input[row], batch);
            string patternString = ResolveString(pattern[row], batch);

            if (!cache.TryGetValue(patternString, out Regex? regex))
            {
                string escaped = Regex.Escape(patternString);
                if (like.EscapeCharacter is LiteralExpression { Value: string escapeValue })
                {
                    string escapeStr = Regex.Escape(escapeValue);
                    escaped = escaped
                        .Replace(escapeStr + "%", "%", StringComparison.Ordinal)
                        .Replace(escapeStr + "_", "_", StringComparison.Ordinal);
                }

                string regexPattern = "^" + escaped
                    .Replace("%", ".*", StringComparison.Ordinal)
                    .Replace("_", ".", StringComparison.Ordinal) + "$";
                regex = new Regex(regexPattern, RegexOptions.Compiled | options);
                cache[patternString] = regex;
            }

            result[row] = regex.IsMatch(inputString) ? trueValue : falseValue;
        }

        return result;
    }

    // ───────────────────────── Function calls ─────────────────────────

    /// <summary>
    /// Evaluates a function call column-at-a-time by calling
    /// <see cref="IScalarFunction.Execute"/> per row.  Vectorized function
    /// variants are planned for Phase 6.
    /// </summary>
    private DataValue[] EvaluateFunctionColumn(FunctionCallExpression function, ColumnBatch batch)
    {
        IScalarFunction? scalarFunction = _functions.TryGetScalar(function.FunctionName);
        if (scalarFunction is null)
        {
            throw new InvalidOperationException(
                $"Unknown function: '{function.FunctionName}'.");
        }

        int argumentCount = function.Arguments.Count;
        DataValue[][] argumentColumns = new DataValue[argumentCount][];
        for (int index = 0; index < argumentCount; index++)
        {
            argumentColumns[index] = EvaluateColumn(function.Arguments[index], batch);
        }

        int rowCount = batch.RowCount;
        DataValue[] result = RentBuffer(rowCount);
        DataValue[] arguments = ArrayPool<DataValue>.Shared.Rent(argumentCount);

        try
        {
            for (int row = 0; row < rowCount; row++)
            {
                for (int argument = 0; argument < argumentCount; argument++)
                {
                    arguments[argument] = argumentColumns[argument][row];
                }

                result[row] = scalarFunction.Execute(arguments.AsSpan(0, argumentCount));
            }
        }
        finally
        {
            arguments.AsSpan(0, argumentCount).Clear();
            ArrayPool<DataValue>.Shared.Return(arguments);
        }

        return result;
    }

    // ───────────────────────── Helpers ─────────────────────────

    /// <summary>
    /// Interprets a <see cref="DataValue"/> as a boolean for filter/predicate evaluation.
    /// Null is falsy. Zero/empty-string is falsy. Everything else is truthy.
    /// </summary>
    private static bool IsTruthy(DataValue value)
    {
        if (value.IsNull) return false;

        return value.Kind switch
        {
            DataKind.Boolean => value.AsBoolean(),
            DataKind.Float32 => value.AsFloat32() != 0f,
            DataKind.Float64 => value.AsFloat64() != 0.0,
            DataKind.UInt8 => value.AsUInt8() != 0,
            DataKind.Int8 => value.AsInt8() != 0,
            DataKind.Int16 => value.AsInt16() != 0,
            DataKind.UInt16 => value.AsUInt16() != 0,
            DataKind.Int32 => value.AsInt32() != 0,
            DataKind.UInt32 => value.AsUInt32() != 0,
            DataKind.Int64 => value.AsInt64() != 0,
            DataKind.UInt64 => value.AsUInt64() != 0,
            DataKind.String => !string.IsNullOrEmpty(value.AsString()),
            _ => true,
        };
    }

    /// <summary>
    /// Converts a <see cref="DataValue"/> to <see langword="float"/> for arithmetic.
    /// Matches <see cref="ExpressionEvaluator"/> coercion semantics.
    /// </summary>
    private static float ToFloat(DataValue value)
    {
        if (value.TryToFloat(out float f)) return f;
        return value.Kind switch
        {
            DataKind.Duration => (float)value.AsDuration().TotalSeconds,
            DataKind.Time => (float)(value.AsTime().Hour * 3600 + value.AsTime().Minute * 60 +
                value.AsTime().Second + value.AsTime().Millisecond / 1000.0),
            DataKind.String => float.TryParse(
                value.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed)
                ? parsed
                : throw new InvalidOperationException(
                    $"Cannot convert string '{value.AsString()}' to number."),
            _ => throw new InvalidOperationException($"Cannot use {value.Kind} in arithmetic."),
        };
    }

    /// <summary>
    /// Resolves a string value, handling both arena-backed and regular strings.
    /// </summary>
    private static string ResolveString(DataValue value, ColumnBatch batch)
    {
        if (value.IsArenaBacked)
        {
            return value.AsString(batch.StringArena);
        }

        return value.AsString();
    }

    /// <summary>
    /// Compares two <see cref="DataValue"/> instances using the same semantics as
    /// <see cref="ExpressionEvaluator"/>.  Handles arena-backed strings.
    /// </summary>
    private static int CompareDataValues(DataValue left, DataValue right, ColumnBatch batch)
    {
        return DataValueComparer.Compare(left, right, batch.StringArena);
    }

    private bool TryGetOrBuildLiteralValueSet(
        InExpression inExpression,
        out HashSet<DataValue> valueSet,
        out bool hasNull)
    {
        if (_inValueSetCache.TryGetValue(inExpression, out (HashSet<DataValue> NonNullValues, bool HasNull) cached))
        {
            valueSet = cached.NonNullValues;
            hasNull = cached.HasNull;
            return true;
        }

        HashSet<DataValue> set = new();
        bool anyNull = false;

        foreach (Expression valueExpression in inExpression.Values)
        {
            if (valueExpression is not LiteralExpression literal)
            {
                valueSet = null!;
                hasNull = false;
                return false;
            }

            DataValue value = ResolveLiteral(literal);
            if (value.IsNull)
            {
                anyNull = true;
            }
            else
            {
                set.Add(value);
            }
        }

        _inValueSetCache[inExpression] = (set, anyNull);
        valueSet = set;
        hasNull = anyNull;
        return true;
    }
}
