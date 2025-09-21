using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the runtime <see cref="DataKind"/> of a value as a
/// <see cref="DataKind.Type"/> tag. Enables type-oriented comparisons:
/// <c>typeof(x) == Int32</c> instead of string-based checks.
/// </summary>
internal sealed class TypeofFunction : IScalarFunction
{
    public string Name => "typeof";

    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new ArgumentException("typeof() requires exactly 1 argument.");
        }

        return DataKind.Type;
    }

    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromType(arguments[0].Kind);
    }
}
