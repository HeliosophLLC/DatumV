using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Exception thrown when a function argument does not match the expected kind or count.
/// Inherits from <see cref="ExecutionException"/>: the message is safe to surface to the
/// caller as a query-level error.
/// </summary>
/// <param name="functionName">The name of the function.</param>
/// <param name="message">The error message.</param>
public sealed class FunctionArgumentException(string functionName, string message)
    : ExecutionException($"{FormatFunctionName(functionName)}: {message}")
{
    /// <summary>
    /// Throws if no arguments are provided, but at least one is expected.
    /// </summary>
    public static void ThrowIfNoArguments(string functionName, int argumentCount = 0)
    {
        if (argumentCount > 0)
            return;
            
        throw new FunctionArgumentException(functionName, "requires at least 1 argument.");
    }

    /// <summary>
    /// Throws if the actual argument is not a string.
    /// </summary>
    public static void ThrowIfNotStringArgument(string functionName, int argumentIndex, string argumentName, DataKind actual)
    {
        if (actual == DataKind.String)
            return;

        throw new FunctionArgumentException(
            functionName,
            $"argument '{argumentName}' at index {argumentIndex} must be String, got {actual}.");
    }

    /// <summary>
    /// Throws if the actual argument kind does not match the expected kind.
    /// </summary>
    public static void ThrowIfArgumentKindMismatch(string functionName, int argumentIndex, string argumentName, DataKind expected, DataKind actual)
    {
        if (expected == actual)
            return;

        throw new FunctionArgumentException(
            functionName,
            $"argument '{argumentName}' at index {argumentIndex} must be {expected}, got {actual}.");
    }

    /// <summary>
    /// Throws if the actual argument count does not match the expected count.
    /// </summary>
    public static void ThrowIfArgumentCountMismatch(string functionName, int actual, Span<string> expected)
    {
        if (expected.Length == actual)
            return;

        throw new FunctionArgumentException(
            functionName,
            $"expects {string.Join(", ", expected)} arguments, got {actual}.");
    }

    /// <summary>
    /// Throws if the actual argument is not an integer type.
    /// </summary>
    public static void ThrowIfArgumentNotIntegerType(string functionName, int argumentIndex,string argumentName, DataKind actual)
    {
        if (DataValue.IsIntegerKind(actual))
            return;

        throw new FunctionArgumentException(
            functionName,
            $"argument '{argumentName}' at index {argumentIndex} must be an integer type, got {actual}.");
    }

    private static string FormatFunctionName(string functionName)
    {
        if (functionName.EndsWith("()"))
        {
            return functionName;
        }
        else
        {
            return $"{functionName}()";
        }
    }
}