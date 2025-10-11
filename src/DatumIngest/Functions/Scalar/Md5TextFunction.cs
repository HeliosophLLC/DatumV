using System.Security.Cryptography;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Computes the MD5 hash of a string and returns the result as a hex string.
/// <c>md5(input)</c> — PostgreSQL compatible. Returns String (lowercase hex).
/// </summary>
public sealed class Md5TextFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "md5";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("md5() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"md5() argument must be String, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        byte[] inputBytes = Encoding.UTF8.GetBytes(arguments[0].AsString());
        byte[] hash = MD5.HashData(inputBytes);
        return DataValue.FromString(Convert.ToHexStringLower(hash));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        ReadOnlySpan<byte> inputBytes = arguments[0].AsUtf8Span(store);
        byte[] hash = MD5.HashData(inputBytes);
        Span<char> hex = stackalloc char[hash.Length * 2];
        Convert.TryToHexStringLower(hash, hex, out _);
        return DataValue.FromCharSpan(hex, store);
    }
}
