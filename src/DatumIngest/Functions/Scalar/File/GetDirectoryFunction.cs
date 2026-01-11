using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// Returns the directory portion of a path (everything up to but not
/// including the last <c>/</c> or <c>\</c> separator). Returns an empty
/// string when the path contains no separator. Null input propagates to
/// null output. The trailing separator itself is stripped.
/// </summary>
public sealed class GetDirectoryFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "get_directory";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Returns the directory portion of a path (before the last / or \\ separator).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<GetDirectoryFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef input = arguments.Span[0];
        if (input.IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));

        string path = input.AsString();
        int sep = PathOps.LastSeparatorIndex(path);
        string dir = sep < 0 ? string.Empty : path[..sep];
        return new ValueTask<ValueRef>(ValueRef.FromString(dir));
    }
}
