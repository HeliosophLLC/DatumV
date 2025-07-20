using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Returns the current UTC timestamp as a DateTime value.
/// <c>now()</c> takes no arguments and returns <see cref="DateTimeOffset.UtcNow"/>.
/// </summary>
public sealed class NowFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "now";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 0)
        {
            throw new ArgumentException("now() takes no arguments.");
        }

        return DataKind.DateTime;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        return DataValue.FromDateTime(DateTimeOffset.UtcNow);
    }
}
