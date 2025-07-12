using System.IO;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the file name and extension from a path string.
/// <c>get_filename(path)</c>
/// </summary>
public sealed class GetFilenameFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "get_filename";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("get_filename() requires exactly 1 argument.");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException($"get_filename() argument must be String, got {argumentKinds[0]}.");
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

        string? fileName = Path.GetFileName(input.AsString());
        return DataValue.FromString(fileName ?? string.Empty);
    }
}
