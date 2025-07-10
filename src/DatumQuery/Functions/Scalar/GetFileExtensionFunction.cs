using System.IO;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Functions.Scalar;

/// <summary>
/// Returns the file extension (including the dot) from a path string.
/// <c>get_file_extension(path)</c>
/// </summary>
public sealed class GetFileExtensionFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "get_file_extension";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("get_file_extension() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"get_file_extension() argument must be String, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        DataValue input = arguments[0];
        if (input.IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string? extension = Path.GetExtension(input.AsString());
        return DataValue.FromString(extension ?? string.Empty);
    }
}
