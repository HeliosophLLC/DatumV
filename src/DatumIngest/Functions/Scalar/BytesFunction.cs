using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Constructs a byte array from one or more scalar byte values (0–255).
/// <c>bytes(a, b, ...)</c> — each scalar argument is cast to a byte.
/// Null arguments are treated as zero.
/// </summary>
public sealed class BytesFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "bytes";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        FunctionArgumentException.ThrowIfNoArguments(Name, argumentKinds.Length);

        for (int i = 0; i < argumentKinds.Length; i++)
        {
            FunctionArgumentException.ThrowIfArgumentNotIntegerType(Name, i, $"arg{i}", argumentKinds[i]);
        }

        return DataKind.UInt8Array;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        byte[] result = new byte[arguments.Length];
        for (int i = 0; i < arguments.Length; i++)
        {
            if (arguments[i].IsNull)
            {
                result[i] = 0;
            }
            else
            {
                int value = arguments[i].ToInt32();
                if (value < 0 || value > 255)
                {
                    throw new InvalidOperationException(
                        $"bytes() argument {i} value {value} is out of byte range (0–255).");
                }

                result[i] = (byte)value;
            }
        }

        return DataValue.FromUInt8Array(result);
    }
}
