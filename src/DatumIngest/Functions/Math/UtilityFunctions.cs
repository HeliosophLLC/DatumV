using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Returns the first non-null argument: coalesce(a, b, ...).
/// Accepts any number of arguments of the same kind.
/// </summary>
public sealed class CoalesceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "coalesce";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1)
            throw new ArgumentException("coalesce() requires at least 1 argument.");
        return argumentKinds[0];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        for (int i = 0; i < arguments.Length; i++)
        {
            if (!arguments[i].IsNull) return arguments[i];
        }
        return arguments[0]; // all null — return first null
    }
}

/// <summary>
/// Returns the greatest (maximum) of a set of scalar values: greatest(a, b, ...).
/// </summary>
public sealed class GreatestFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "greatest";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("greatest() requires at least 2 arguments.");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] is not (DataKind.Scalar or DataKind.UInt8))
                throw new ArgumentException($"greatest() argument {i + 1} must be Scalar or UInt8.");
        }
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float max = float.NegativeInfinity;
        bool allNull = true;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) continue;
            allNull = false;
            float value = arguments[i].Kind is DataKind.UInt8 ? arguments[i].AsUInt8() : arguments[i].AsScalar();
            if (value > max) max = value;
        }
        return allNull ? DataValue.Null(DataKind.Scalar) : DataValue.FromScalar(max);
    }
}

/// <summary>
/// Returns the least (minimum) of a set of scalar values: least(a, b, ...).
/// </summary>
public sealed class LeastFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "least";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 2)
            throw new ArgumentException("least() requires at least 2 arguments.");
        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] is not (DataKind.Scalar or DataKind.UInt8))
                throw new ArgumentException($"least() argument {i + 1} must be Scalar or UInt8.");
        }
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        float min = float.PositiveInfinity;
        bool allNull = true;
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull) continue;
            allNull = false;
            float value = arguments[i].Kind is DataKind.UInt8 ? arguments[i].AsUInt8() : arguments[i].AsScalar();
            if (value < min) min = value;
        }
        return allNull ? DataValue.Null(DataKind.Scalar) : DataValue.FromScalar(min);
    }
}

/// <summary>
/// Returns 1 if the value is NaN, 0 otherwise: is_nan(x).
/// </summary>
public sealed class IsNanFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_nan";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("is_nan() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException("is_nan() requires a Scalar or UInt8 argument.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float value = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsScalar();
        return DataValue.FromScalar(float.IsNaN(value) ? 1f : 0f);
    }
}

/// <summary>
/// Returns 1 if the value is finite (not NaN or infinity), 0 otherwise: is_finite(x).
/// </summary>
public sealed class IsFiniteFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_finite";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("is_finite() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException("is_finite() requires a Scalar or UInt8 argument.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float value = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsScalar();
        return DataValue.FromScalar(float.IsFinite(value) ? 1f : 0f);
    }
}

/// <summary>
/// Returns 1 if the value is an even integer, 0 otherwise: is_even(x).
/// Non-integer values always return 0.
/// </summary>
public sealed class IsEvenFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_even";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("is_even() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException("is_even() requires a Scalar or UInt8 argument.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float value = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsScalar();
        return DataValue.FromScalar(value == MathF.Truncate(value) && value % 2 == 0 ? 1f : 0f);
    }
}

/// <summary>
/// Returns 1 if the value is an odd integer, 0 otherwise: is_odd(x).
/// Non-integer values always return 0.
/// </summary>
public sealed class IsOddFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "is_odd";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
            throw new ArgumentException("is_odd() requires exactly 1 argument.");
        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.UInt8))
            throw new ArgumentException("is_odd() requires a Scalar or UInt8 argument.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull) return DataValue.Null(DataKind.Scalar);
        float value = arguments[0].Kind is DataKind.UInt8 ? arguments[0].AsUInt8() : arguments[0].AsScalar();
        return DataValue.FromScalar(value == MathF.Truncate(value) && value % 2 != 0 ? 1f : 0f);
    }
}

/// <summary>
/// Returns the first argument if it is not null, otherwise the second: if_null(x, default).
/// </summary>
public sealed class IfNullFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "if_null";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("if_null() requires exactly 2 arguments.");
        return argumentKinds[0];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return arguments[0].IsNull ? arguments[1] : arguments[0];
    }
}

/// <summary>
/// Inline conditional: iif(condition, then_value, else_value).
/// Returns then_value when condition is truthy, else_value otherwise.
/// Truthiness follows AND/OR semantics: null and Scalar 0 are falsy, everything else is truthy.
/// </summary>
public sealed class IifFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "iif";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 3)
            throw new ArgumentException("iif() requires exactly 3 arguments (condition, then, else).");
        if (argumentKinds[0] is not (DataKind.Scalar or DataKind.Boolean))
            throw new ArgumentException("iif() condition (argument 1) must be Scalar or Boolean.");
        if (argumentKinds[1] != argumentKinds[2])
            throw new ArgumentException($"iif() then/else arguments must be the same kind, got {argumentKinds[1]} and {argumentKinds[2]}.");
        return argumentKinds[1];
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue condition = arguments[0];
        bool truthy = !condition.IsNull &&
            (condition.Kind == DataKind.Boolean ? condition.AsBoolean() : condition.AsScalar() != 0f);
        return truthy ? arguments[1] : arguments[2];
    }
}

/// <summary>
/// Returns a pseudo-random float in [0, 1): random().
/// Uses a thread-safe random number generator.
/// </summary>
public sealed class RandomFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "random";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
            throw new ArgumentException("random() takes no arguments.");
        return DataKind.Scalar;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromScalar((float)Random.Shared.NextDouble());
    }
}
