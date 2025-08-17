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
        if (argumentKinds.Length < 1)
        {
            throw new ArgumentException("bytes() requires at least 1 argument.");
        }

        for (int i = 0; i < argumentKinds.Length; i++)
        {
            if (argumentKinds[i] != DataKind.Float32)
            {
                throw new ArgumentException($"bytes() requires all arguments to be Scalar, but argument {i} is {argumentKinds[i]}.");
            }
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
                result[i] = (byte)arguments[i].AsFloat32();
            }
        }

        return DataValue.FromUInt8Array(result);
    }
}
