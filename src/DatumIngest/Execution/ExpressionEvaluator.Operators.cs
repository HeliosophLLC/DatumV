using System.Globalization;
using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

public sealed partial class ExpressionEvaluator
{
    private async ValueTask<DataValue> EvaluateBinaryAsync(
        BinaryExpression binary, EvaluationFrame frame, CancellationToken cancellationToken) =>
        ToDataValue(await EvaluateBinaryAsValueRefAsync(binary, frame, cancellationToken).ConfigureAwait(false), frame);

    /// <summary>
    /// ValueRef-native binary expression evaluation. Operands are pulled in as
    /// ValueRef (not DataValue), so predicate-context callers consume the
    /// resulting boolean without any function-result string ever crossing the
    /// arena boundary. Result is always inline (Boolean / Float32 / Duration).
    /// </summary>
    private async ValueTask<ValueRef> EvaluateBinaryAsValueRefAsync(
        BinaryExpression binary, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        // Short-circuit for AND/OR — uses EvaluateAsBooleanAsync which itself routes
        // through EvaluateAsValueRefAsync.
        if (binary.Operator == BinaryOperator.And)
        {
            if (!await EvaluateAsBooleanAsync(binary.Left, frame, cancellationToken).ConfigureAwait(false)) return ValueRef.FromBoolean(false);
            if (!await EvaluateAsBooleanAsync(binary.Right, frame, cancellationToken).ConfigureAwait(false)) return ValueRef.FromBoolean(false);
            return ValueRef.FromBoolean(true);
        }

        if (binary.Operator == BinaryOperator.Or)
        {
            if (await EvaluateAsBooleanAsync(binary.Left, frame, cancellationToken).ConfigureAwait(false)) return ValueRef.FromBoolean(true);
            if (await EvaluateAsBooleanAsync(binary.Right, frame, cancellationToken).ConfigureAwait(false)) return ValueRef.FromBoolean(true);
            return ValueRef.FromBoolean(false);
        }

        ValueRef left = await EvaluateAsValueRefAsync(binary.Left, frame, cancellationToken).ConfigureAwait(false);
        ValueRef right = await EvaluateAsValueRefAsync(binary.Right, frame, cancellationToken).ConfigureAwait(false);

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

            // PG temporal arithmetic. The Timestamp(Tz) ± Duration arms
            // shift the wall-clock or UTC ticks; the Timestamp(Tz) -
            // Timestamp(Tz) arms produce a Duration. Same-kind only — mixing
            // Timestamp and TimestampTz requires an explicit cast (slice 4
            // cast matrix in TypeCoercion / CastFunction).
            BinaryOperator.Add when left.Kind == DataKind.TimestampTz && right.Kind == DataKind.Duration
                => ValueRef.FromTimestampTz(left.AsTimestampTz() + right.AsDuration()),
            BinaryOperator.Add when left.Kind == DataKind.Duration && right.Kind == DataKind.TimestampTz
                => ValueRef.FromTimestampTz(right.AsTimestampTz() + left.AsDuration()),
            BinaryOperator.Subtract when left.Kind == DataKind.TimestampTz && right.Kind == DataKind.Duration
                => ValueRef.FromTimestampTz(left.AsTimestampTz() - right.AsDuration()),
            BinaryOperator.Subtract when left.Kind == DataKind.TimestampTz && right.Kind == DataKind.TimestampTz
                => ValueRef.FromDuration(left.AsTimestampTz() - right.AsTimestampTz()),

            BinaryOperator.Add when left.Kind == DataKind.Timestamp && right.Kind == DataKind.Duration
                => ValueRef.FromTimestamp(left.AsTimestamp() + right.AsDuration()),
            BinaryOperator.Add when left.Kind == DataKind.Duration && right.Kind == DataKind.Timestamp
                => ValueRef.FromTimestamp(right.AsTimestamp() + left.AsDuration()),
            BinaryOperator.Subtract when left.Kind == DataKind.Timestamp && right.Kind == DataKind.Duration
                => ValueRef.FromTimestamp(left.AsTimestamp() - right.AsDuration()),
            BinaryOperator.Subtract when left.Kind == DataKind.Timestamp && right.Kind == DataKind.Timestamp
                => ValueRef.FromDuration(left.AsTimestamp() - right.AsTimestamp()),

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

    private async ValueTask<DataValue> EvaluateUnaryAsync(
        UnaryExpression unary, EvaluationFrame frame, CancellationToken cancellationToken) =>
        ToDataValue(await EvaluateUnaryAsValueRefAsync(unary, frame, cancellationToken).ConfigureAwait(false), frame);

    /// <summary>
    /// ValueRef-native unary expression evaluation. Result is always inline
    /// (Boolean for NOT, Float32 for negate).
    /// </summary>
    private async ValueTask<ValueRef> EvaluateUnaryAsValueRefAsync(
        UnaryExpression unary, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef operand = await EvaluateAsValueRefAsync(unary.Operand, frame, cancellationToken).ConfigureAwait(false);

        if (operand.IsNull)
        {
            return unary.Operator == UnaryOperator.Not
                ? ValueRef.Null(DataKind.Boolean)
                : ValueRef.Null(NegateResultKind(operand.Kind));
        }

        return unary.Operator switch
        {
            UnaryOperator.Not => ValueRef.FromBoolean(!await EvaluateAsBooleanAsync(unary.Operand, frame, cancellationToken).ConfigureAwait(false)),
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
    private static int CompareDataValues(DataValue left, DataValue right, EvaluationFrame frame)
    {
        return DataValueComparer.Compare(left, frame.Source, right, frame.Target);
    }

    /// <summary>
    /// ValueRef counterpart of <see cref="CompareDataValues"/>. Reads accessors
    /// directly off the ValueRefs without arena resolution. Both operands
    /// non-null is the contract — the binary handler short-circuits NULL
    /// before calling.
    /// </summary>
    /// <remarks>
    /// Exposed as <c>internal</c> so the <c>assert_*</c> scalar function
    /// family (<see cref="Functions.Scalar.Assertion"/>) can route through
    /// the same cross-kind comparison rules used by SQL <c>&lt;</c> / <c>&gt;</c>
    /// / <c>=</c> operators.
    /// </remarks>
    internal static int CompareValueRefs(ValueRef left, ValueRef right)
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
                DataKind.Timestamp => left.AsTimestamp().CompareTo(right.AsTimestamp()),
                DataKind.TimestampTz => left.AsTimestampTz().CompareTo(right.AsTimestampTz()),
                DataKind.Time => left.AsTime().CompareTo(right.AsTime()),
                DataKind.Duration => left.AsDuration().CompareTo(right.AsDuration()),
                DataKind.Uuid => left.AsUuid().CompareTo(right.AsUuid()),
                DataKind.Type => ((byte)left.AsType()).CompareTo((byte)right.AsType()),
                _ => throw new InvalidOperationException(
                    $"Cannot compare values of kind {left.Kind}."),
            };
        }

        // Cross-kind: PG-flavored implicit String → target-kind cast at
        // comparison time. `WHERE date_col = '2026-01-02'` arrives here with
        // one Date and one String because the parser doesn't know the
        // target column kind. Try to parse the string as the other side's
        // kind; on success, recurse to compare same-kind values. On
        // parse failure return a non-zero result (unequal) — equality
        // becomes false rather than throwing, mirroring how engines like
        // MySQL handle implicit cast misses.
        if (left.Kind == DataKind.String && TypeCoercion.CanCoerceStringTo(right.Kind))
        {
            if (TryCoerceStringRefTo(left, right.Kind, out ValueRef coerced))
            {
                return CompareValueRefs(coerced, right);
            }
            return 1;
        }
        if (right.Kind == DataKind.String && TypeCoercion.CanCoerceStringTo(left.Kind))
        {
            if (TryCoerceStringRefTo(right, left.Kind, out ValueRef coerced))
            {
                return CompareValueRefs(left, coerced);
            }
            return -1;
        }

        throw new InvalidOperationException(
            $"Cannot compare {left.Kind} with {right.Kind}.");
    }

    /// <summary>
    /// Parses a <see cref="DataKind.String"/> <see cref="ValueRef"/> into
    /// the requested target kind. Mirrors <c>TypeCoercion.TryCoerceString</c>
    /// but returns a <see cref="ValueRef"/> so callers don't need to round-trip
    /// through a <see cref="DataValue"/> + arena. Used by <see cref="CompareValueRefs"/>
    /// for cross-kind comparisons against string literals.
    /// </summary>
    private static bool TryCoerceStringRefTo(ValueRef value, DataKind targetKind, out ValueRef result)
    {
        string text = value.AsString();
        switch (targetKind)
        {
            case DataKind.Float32 when float.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float f32):
                result = ValueRef.FromFloat32(f32); return true;
            case DataKind.Float64 when double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out double f64):
                result = ValueRef.FromFloat64(f64); return true;
            case DataKind.UInt8 when byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte u8):
                result = ValueRef.FromUInt8(u8); return true;
            case DataKind.Int8 when sbyte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out sbyte i8):
                result = ValueRef.FromInt8(i8); return true;
            case DataKind.Int16 when short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out short i16):
                result = ValueRef.FromInt16(i16); return true;
            case DataKind.UInt16 when ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort u16):
                result = ValueRef.FromUInt16(u16); return true;
            case DataKind.Int32 when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i32):
                result = ValueRef.FromInt32(i32); return true;
            case DataKind.UInt32 when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint u32):
                result = ValueRef.FromUInt32(u32); return true;
            case DataKind.Int64 when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i64):
                result = ValueRef.FromInt64(i64); return true;
            case DataKind.UInt64 when ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong u64):
                result = ValueRef.FromUInt64(u64); return true;
            case DataKind.Boolean:
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1")
                { result = ValueRef.FromBoolean(true); return true; }
                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase) || text == "0")
                { result = ValueRef.FromBoolean(false); return true; }
                result = default; return false;
            case DataKind.Date when DateOnly.TryParse(text, CultureInfo.InvariantCulture, out DateOnly date):
                result = ValueRef.FromDate(date); return true;
            case DataKind.TimestampTz when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset dtOffset):
                result = ValueRef.FromTimestampTz(dtOffset); return true;
            case DataKind.Timestamp when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime dt):
                result = ValueRef.FromTimestamp(dt); return true;
            case DataKind.Time when TimeOnly.TryParse(text, CultureInfo.InvariantCulture, out TimeOnly time):
                result = ValueRef.FromTime(time); return true;
            case DataKind.Duration when TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out TimeSpan duration):
                result = ValueRef.FromDuration(duration); return true;
            case DataKind.Uuid when Guid.TryParse(text, out Guid uuid):
                result = ValueRef.FromUuid(uuid); return true;
            default:
                result = default;
                return false;
        }
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
}
