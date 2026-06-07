using System.Globalization;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

public sealed partial class ExpressionEvaluator
{
    // ──────────────────── Arithmetic helpers ────────────────────

    /// <summary>
    /// Evaluates a binary arithmetic operator (<c>+ - * / % **</c>) with
    /// runtime kind promotion. Picks a target kind from the operand kinds
    /// + operator (so <c>Int64 + Int64 → Int64</c>, <c>Decimal × Float → Float64</c>,
    /// <c>5 / 2 → Int32</c> matching PostgreSQL integer division) and
    /// applies the op in that type. Result is always inline; the only
    /// allocation is the returned <see cref="ValueRef"/> struct.
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
    /// <see cref="PromoteArithmeticKind"/>. The short version: Power
    /// always returns float; Decimal beats float; integer + integer
    /// stays integer at the wider operand's bit width (Int8 + Int8
    /// widens to Int32 in C# style; Int64 stays Int64); strings get
    /// parsed as Float64. Divide and Modulo follow the operand kinds
    /// (PG-style integer division), so <c>5 / 2 → 2</c>; cast an
    /// operand to obtain fractional results (<c>5::float / 2 → 2.5</c>).
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
    /// operator. Mirrors C# numeric promotion: integer arithmetic
    /// widens to Int32 (matching <c>byte + byte → int</c> in C#) so
    /// small literals don't pin everything to Int8. Divide follows
    /// PostgreSQL — <c>int / int → int</c> (truncating); cast an
    /// operand (<c>5::float / 2</c>) to obtain fractional results.
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

        // PG temporal arithmetic (matched first so Duration-mixed timestamp
        // cases don't fall through to the Float32 catch-all below):
        //   timestamp(tz) ± duration → same timestamp kind
        //   timestamp(tz) - timestamp(tz) (same kind) → duration
        if (op is BinaryOperator.Add or BinaryOperator.Subtract)
        {
            DataKind? temporal = TryPromoteTemporalArithmetic(left, right, op);
            if (temporal is not null) return temporal.Value;
        }

        // PG interval × double / interval / double → interval. Routed before
        // the generic numeric promotion so a make_interval(...) * 2 expression
        // doesn't degrade to Float64.
        if (op is BinaryOperator.Multiply or BinaryOperator.Divide)
        {
            if (left == DataKind.Interval && DataValue.IsNumericScalarKind(right)) return DataKind.Interval;
            if (op == BinaryOperator.Multiply && right == DataKind.Interval && DataValue.IsNumericScalarKind(left))
                return DataKind.Interval;
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

    /// <summary>
    /// PG temporal arithmetic promotion table. Returns the result kind for
    /// the supported (timestamp, duration), (timestamp, interval), (date,
    /// interval), (interval, interval), and (timestamp, timestamp) pairs;
    /// returns null when the pair isn't a supported temporal combination so
    /// the caller can fall through to numeric promotion.
    /// </summary>
    private static DataKind? TryPromoteTemporalArithmetic(DataKind left, DataKind right, BinaryOperator op)
    {
        // Timestamp(tz) ± Duration → Timestamp(tz). Commutative on Add.
        if (left == DataKind.TimestampTz && right == DataKind.Duration) return DataKind.TimestampTz;
        if (left == DataKind.Timestamp   && right == DataKind.Duration) return DataKind.Timestamp;
        if (op == BinaryOperator.Add)
        {
            if (right == DataKind.TimestampTz && left == DataKind.Duration) return DataKind.TimestampTz;
            if (right == DataKind.Timestamp   && left == DataKind.Duration) return DataKind.Timestamp;
        }
        // Timestamp(tz) - Timestamp(tz) → Duration (same kind only).
        if (op == BinaryOperator.Subtract)
        {
            if (left == DataKind.TimestampTz && right == DataKind.TimestampTz) return DataKind.Duration;
            if (left == DataKind.Timestamp   && right == DataKind.Timestamp)   return DataKind.Duration;
        }

        // Interval ± Interval → Interval (carry not normalised).
        if (left == DataKind.Interval && right == DataKind.Interval) return DataKind.Interval;

        // Timestamp(tz) ± Interval → Timestamp(tz). Commutative on Add.
        if (left == DataKind.TimestampTz && right == DataKind.Interval) return DataKind.TimestampTz;
        if (left == DataKind.Timestamp   && right == DataKind.Interval) return DataKind.Timestamp;
        if (op == BinaryOperator.Add)
        {
            if (right == DataKind.TimestampTz && left == DataKind.Interval) return DataKind.TimestampTz;
            if (right == DataKind.Timestamp   && left == DataKind.Interval) return DataKind.Timestamp;
        }

        // PG: date + interval = timestamp; date - interval = timestamp.
        if (left == DataKind.Date && right == DataKind.Interval) return DataKind.Timestamp;
        if (op == BinaryOperator.Add && left == DataKind.Interval && right == DataKind.Date)
            return DataKind.Timestamp;

        return null;
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
            BinaryOperator.Divide => b != 0
                ? ValueRef.FromInt32(a / b)
                : throw new ExecutionException("division by zero"),
            BinaryOperator.Modulo => b != 0
                ? ValueRef.FromInt32(a % b)
                : throw new ExecutionException("division by zero"),
            _ => throw new InvalidOperationException(
                $"Internal error: Int32 dispatch hit for {op} (Power should have promoted to float)."),
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
            BinaryOperator.Divide => b != 0
                ? ValueRef.FromInt64(a / b)
                : throw new ExecutionException("division by zero"),
            BinaryOperator.Modulo => b != 0
                ? ValueRef.FromInt64(a % b)
                : throw new ExecutionException("division by zero"),
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
            BinaryOperator.Divide => b != Int128.Zero
                ? ValueRef.FromInt128(a / b)
                : throw new ExecutionException("division by zero"),
            BinaryOperator.Modulo => b != Int128.Zero
                ? ValueRef.FromInt128(a % b)
                : throw new ExecutionException("division by zero"),
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
                : throw new ExecutionException("division by zero"),
            BinaryOperator.Modulo => b != 0m
                ? ValueRef.FromDecimal(a % b)
                : throw new ExecutionException("division by zero"),
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
}
