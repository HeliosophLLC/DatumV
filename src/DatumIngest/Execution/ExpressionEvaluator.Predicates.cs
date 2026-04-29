using System.Buffers;
using System.Globalization;
using System.Text.RegularExpressions;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

public sealed partial class ExpressionEvaluator
{
    private async ValueTask<DataValue> EvaluateInAsync(
        InExpression inExpr, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue target = await EvaluateAsync(inExpr.Expression, frame, cancellationToken).ConfigureAwait(false);

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
        return await EvaluateInLinearAsync(inExpr, target, frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Attempts to retrieve or build a cached <see cref="HashSet{T}"/> of literal values
    /// for the given <see cref="InExpression"/>. Returns <see langword="false"/> if any
    /// value is not a <see cref="LiteralExpression"/>, indicating the linear path is needed.
    /// The cache uses the evaluator's persistent <c>_store</c> so entries outlive any batch.
    /// </summary>
    private bool TryGetOrBuildLiteralValueSet(
        InExpression inExpr,
        EvaluationFrame frame,
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
        EvaluationFrame cacheFrame = new EvaluationFrame(
            frame.Row, frame.Source, cacheStore, _context, frame.OuterRow);

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
    private async ValueTask<DataValue> EvaluateInLinearAsync(
        InExpression inExpr, DataValue target, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        bool hasNullCandidate = false;

        foreach (Expression valueExpression in inExpr.Values)
        {
            DataValue candidate = await EvaluateAsync(valueExpression, frame, cancellationToken).ConfigureAwait(false);
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

    private async ValueTask<DataValue> EvaluateBetweenAsync(
        BetweenExpression between, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue target = await EvaluateAsync(between.Expression, frame, cancellationToken).ConfigureAwait(false);
        DataValue low = await EvaluateAsync(between.Low, frame, cancellationToken).ConfigureAwait(false);
        DataValue high = await EvaluateAsync(between.High, frame, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// DataValue-returning wrapper around <see cref="EvaluateIsNullAsValueRefAsync"/>.
    /// Sync-fast-path: bypasses state-machine setup when the inner ValueRef path completes synchronously.
    /// </summary>
    private ValueTask<DataValue> EvaluateIsNullAsync(
        IsNullExpression isNull, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueTask<ValueRef> task = EvaluateIsNullAsValueRefAsync(isNull, frame, cancellationToken);
        if (task.IsCompletedSuccessfully)
        {
            return new ValueTask<DataValue>(ToDataValue(task.Result, frame));
        }
        return AwaitToDataValue(task, frame);

        static async ValueTask<DataValue> AwaitToDataValue(ValueTask<ValueRef> pending, EvaluationFrame frame)
        {
            ValueRef result = await pending.ConfigureAwait(false);
            return ToDataValue(result, frame);
        }
    }

    /// <summary>
    /// ValueRef-native IS NULL check. Avoids reading the inner expression's
    /// payload from the arena when the predicate only needs a null/non-null bit.
    /// </summary>
    /// <remarks>
    /// Sync-fast-path: the result is purely a function of the operand's
    /// <see cref="ValueRef.IsNull"/> flag, so when the operand evaluation
    /// completes synchronously the IS NULL test is a single instruction
    /// with no state machine.
    /// </remarks>
    private ValueTask<ValueRef> EvaluateIsNullAsValueRefAsync(
        IsNullExpression isNull, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueTask<ValueRef> task = EvaluateAsValueRefAsync(isNull.Expression, frame, cancellationToken);
        if (task.IsCompletedSuccessfully)
        {
            return new ValueTask<ValueRef>(ToIsNullResult(task.Result, isNull.Negated));
        }
        return AwaitIsNull(task, isNull.Negated);

        static async ValueTask<ValueRef> AwaitIsNull(ValueTask<ValueRef> pending, bool negated)
        {
            ValueRef value = await pending.ConfigureAwait(false);
            return ToIsNullResult(value, negated);
        }

        static ValueRef ToIsNullResult(ValueRef value, bool negated) =>
            ValueRef.FromBoolean(negated ? !value.IsNull : value.IsNull);
    }

    private readonly Dictionary<string, TimeZoneInfo> _timeZoneCache = new(StringComparer.OrdinalIgnoreCase);

    private async ValueTask<DataValue> EvaluateCastAsync(
        CastExpression cast, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        IScalarFunction? castFunction = _functions.TryGetScalar("cast");
        if (castFunction is null)
        {
            throw new InvalidOperationException("Cast function not registered.");
        }

        ValueRef[] arguments = ArrayPool<ValueRef>.Shared.Rent(2);
        try
        {
            arguments[0] = await EvaluateAsValueRefAsync(cast.Expression, frame, cancellationToken).ConfigureAwait(false);
            arguments[1] = ValueRef.FromString(cast.TargetType);
            ValueRef result = await castFunction.ExecuteAsync(arguments.AsMemory(0, 2), frame, cancellationToken).ConfigureAwait(false);
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
    private async ValueTask<DataValue> EvaluateAtTimeZoneAsync(
        AtTimeZoneExpression atz, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue value = await EvaluateAsync(atz.Expression, frame, cancellationToken).ConfigureAwait(false);

        DataValue tzValue = await EvaluateAsync(atz.TimeZone, frame, cancellationToken).ConfigureAwait(false);
        string tzName = Str(tzValue, frame);

        if (!_timeZoneCache.TryGetValue(tzName, out TimeZoneInfo? tz))
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzName);
            _timeZoneCache[tzName] = tz;
        }

        // PG AT TIME ZONE is a kind-shifting operator:
        //   timestamptz AT TIME ZONE 'z' → timestamp (wall clock in z)
        //   timestamp   AT TIME ZONE 'z' → timestamptz (interpret naive value as being in z, store UTC)
        if (value.Kind == DataKind.TimestampTz)
        {
            if (value.IsNull) return DataValue.Null(DataKind.Timestamp);
            DateTimeOffset converted = TimeZoneInfo.ConvertTime(value.AsTimestampTz(), tz);
            return DataValue.FromTimestamp(converted.DateTime);
        }
        if (value.Kind == DataKind.Timestamp)
        {
            if (value.IsNull) return DataValue.Null(DataKind.TimestampTz);
            DateTime naive = value.AsTimestamp();
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(
                DateTime.SpecifyKind(naive, DateTimeKind.Unspecified), tz);
            return DataValue.FromTimestampTz(new DateTimeOffset(utc, TimeSpan.Zero));
        }
        // Date or other temporal: best-effort via the unified DateTimeOffset path.
        if (value.IsNull) return DataValue.Null(DataKind.TimestampTz);
        DateTimeOffset fallback = TimeZoneInfo.ConvertTime(value.ToDateTimeOffset(), tz);
        return DataValue.FromTimestampTz(fallback);
    }

    /// <summary>
    /// Evaluates a CASE expression with short-circuit semantics and implicit
    /// branch type coercion. On first evaluation, resolves the common output type
    /// across all branches; subsequent evaluations reuse the cached result.
    /// Simple CASE compares the operand against each WHEN value for equality.
    /// Searched CASE evaluates each WHEN condition as a boolean predicate.
    /// Only the matching THEN branch is evaluated.
    /// </summary>
    private async ValueTask<DataValue> EvaluateCaseAsync(
        CaseExpression caseExpression, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue result = await EvaluateCaseBranchAsync(caseExpression, frame, cancellationToken).ConfigureAwait(false);

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
    private async ValueTask<DataValue> EvaluateCaseBranchAsync(
        CaseExpression caseExpression, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        if (caseExpression.Operand is not null)
        {
            // Simple CASE: compare operand against each WHEN value.
            DataValue operand = await EvaluateAsync(caseExpression.Operand, frame, cancellationToken).ConfigureAwait(false);

            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                DataValue whenValue = await EvaluateAsync(whenClause.Condition, frame, cancellationToken).ConfigureAwait(false);
                if (!operand.IsNull && !whenValue.IsNull && CompareDataValues(operand, whenValue, frame) == 0)
                {
                    return await EvaluateAsync(whenClause.Result, frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else
        {
            // Searched CASE: evaluate each WHEN condition as boolean.
            foreach (WhenClause whenClause in caseExpression.WhenClauses)
            {
                if (await EvaluateAsBooleanAsync(whenClause.Condition, frame, cancellationToken).ConfigureAwait(false))
                {
                    return await EvaluateAsync(whenClause.Result, frame, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        // No match: return ELSE result or typed null.
        if (caseExpression.ElseResult is not null)
        {
            return await EvaluateAsync(caseExpression.ElseResult, frame, cancellationToken).ConfigureAwait(false);
        }

        return DataValue.Null(DataKind.Float32);
    }

    /// <summary>
    /// Resolves the target DataKind for a CASE expression by building a schema from
    /// the current row and delegating to <see cref="ExpressionTypeResolver"/>.
    /// Falls back to AST-level inference when a row-derived schema cannot be built.
    /// </summary>
    private DataKind? ResolveCaseTargetKind(CaseExpression caseExpression, EvaluationFrame frame)
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
            LiteralExpression { Value: ulong } => DataKind.UInt64,
            LiteralExpression { Value: Int128 } => DataKind.Int128,
            LiteralExpression { Value: UInt128 } => DataKind.UInt128,
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
    private async ValueTask<DataValue> EvaluateLikeEscapeAsync(
        LikeExpression like, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue input = await EvaluateAsync(like.Expression, frame, cancellationToken).ConfigureAwait(false);
        DataValue pattern = await EvaluateAsync(like.Pattern, frame, cancellationToken).ConfigureAwait(false);
        DataValue escapeValue = await EvaluateAsync(like.EscapeCharacter, frame, cancellationToken).ConfigureAwait(false);

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
