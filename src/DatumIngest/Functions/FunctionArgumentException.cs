using System.Text;
using DatumIngest.Execution;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Exception thrown when a function argument does not match the expected kind, count,
/// or variadic minimum. Inherits from <see cref="ExecutionException"/>: messages are
/// safe to surface as query-level errors.
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
    public static void ThrowIfArgumentNotIntegerType(string functionName, int argumentIndex, string argumentName, DataKind actual)
    {
        if (DataValue.IsIntegerKind(actual))
            return;

        throw new FunctionArgumentException(
            functionName,
            $"argument '{argumentName}' at index {argumentIndex} must be an integer type, got {actual}.");
    }

    /// <summary>
    /// Throws when an argument's kind doesn't satisfy a
    /// <see cref="DataKindMatcher"/>. Pairs with the matcher abstraction
    /// used by <see cref="FunctionMetadata.Validate{T}"/>.
    /// </summary>
    public static void ThrowIfNotMatched(
        string functionName,
        int argumentIndex,
        string argumentName,
        DataKindMatcher expected,
        DataKind actual)
    {
        if (expected.Matches(actual))
            return;

        throw new FunctionArgumentException(
            functionName,
            $"argument '{argumentName}' at index {argumentIndex} must be {expected.Describe()}, got {actual}.");
    }

    /// <summary>
    /// Throws when no <see cref="FunctionSignatureVariant"/> matched the
    /// supplied argument kinds. The message enumerates the rejected
    /// variants so the caller can see what shapes were tried.
    /// </summary>
    public static void ThrowNoMatchingVariant(
        string functionName,
        ReadOnlySpan<DataKind> actual,
        IReadOnlyList<FunctionSignatureVariant> variants)
    {
        StringBuilder builder = new();
        builder.Append("no matching signature for argument kinds [");
        for (int i = 0; i < actual.Length; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(actual[i]);
        }
        builder.Append("]. Tried: ");
        for (int i = 0; i < variants.Count; i++)
        {
            if (i > 0) builder.Append(" | ");
            DescribeVariant(builder, variants[i]);
        }
        builder.Append('.');
        throw new FunctionArgumentException(functionName, builder.ToString());
    }

    /// <summary>
    /// Throws when a variadic argument count is below its required minimum.
    /// </summary>
    public static void ThrowIfVariadicCountBelow(
        string functionName,
        string variadicName,
        int minimum,
        int actual)
    {
        if (actual >= minimum)
            return;

        throw new FunctionArgumentException(
            functionName,
            $"variadic '{variadicName}' requires at least {minimum} argument(s), got {actual}.");
    }

    private static void DescribeVariant(StringBuilder builder, FunctionSignatureVariant variant)
    {
        builder.Append('(');
        for (int i = 0; i < variant.Parameters.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            ParameterSpec p = variant.Parameters[i];
            if (p.IsOptional) builder.Append('[');
            builder.Append(p.Name).Append(": ").Append(p.Kind.Describe());
            if (p.IsOptional) builder.Append(']');
        }
        if (variant.VariadicTrailing is not null)
        {
            if (variant.Parameters.Count > 0) builder.Append(", ");
            builder.Append("...").Append(variant.VariadicTrailing.Name)
                   .Append(": ").Append(variant.VariadicTrailing.Kind.Describe());
        }
        builder.Append(") -> ").Append(variant.ReturnType.Describe());
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
