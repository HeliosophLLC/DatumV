using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Attempts to cast a value to a target type, returning NULL on failure instead
/// of throwing. <c>try_cast(value, TargetType)</c> where TargetType is a
/// <see cref="DataKind.Type"/> literal (e.g. <c>try_cast(x, Int32)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Behaves identically to <c>CAST(value AS Type)</c> when the conversion succeeds,
/// including truncation and saturation for numeric narrowing. Returns a typed NULL
/// when the conversion would throw (unsupported pair, parse failure, etc.).
/// </para>
/// <para>
/// Use <c>can_cast()</c> when you need a stricter lossless check.
/// Use <c>try_cast()</c> when you want the converted value or NULL.
/// </para>
/// </remarks>
internal sealed class TryCastFunction : IScalarFunction
{
    /// <summary>
    /// Shared <see cref="CastFunction"/> instance used to delegate the actual conversion.
    /// </summary>
    private static readonly CastFunction CastImpl = new();

    public string Name => "try_cast";

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException("try_cast() requires exactly 2 arguments: value and target type.");
        }

        if (argumentKinds[1] != DataKind.Type)
        {
            throw new ArgumentException("try_cast() second argument must be a type literal (e.g. Int32, Float64).");
        }

        // Cannot determine the output kind statically — it depends on the
        // runtime Type value. Return String as a placeholder (same as CastFunction).
        return DataKind.String;
    }

    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        DataKind targetKind = arguments[1].AsType();

        // Null can adopt any type.
        if (input.IsNull)
        {
            return DataValue.Null(targetKind);
        }

        // Same kind — return as-is.
        if (input.Kind == targetKind)
        {
            return input;
        }

        // Delegate to CAST, converting the Type argument to the string form
        // that CastFunction expects. Catch any exception and return typed NULL.
        try
        {
            string targetName = targetKind.ToString();
            DataValue targetNameValue = DataValue.FromString(targetName);
            ReadOnlySpan<DataValue> castArgs = [input, targetNameValue];
            return CastImpl.Execute(castArgs);
        }
        catch
        {
            return DataValue.Null(targetKind);
        }
    }
}
