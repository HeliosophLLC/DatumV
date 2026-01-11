using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// Returns the filename portion of a path (everything after the last <c>/</c>
/// or <c>\</c> separator), including the extension. Returns the entire input
/// when no separator is present. Null input propagates to null output.
/// </summary>
/// <remarks>
/// Recognizes both <c>/</c> and <c>\</c> as separators on every platform —
/// SQL queries shouldn't behave differently between Windows and Linux just
/// because of platform path conventions.
/// </remarks>
public sealed class GetFilenameFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "get_filename";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Returns the filename portion of a path (after the last / or \\ separator).";

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
        FunctionMetadata.Validate<GetFilenameFunction>(argumentKinds);

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
        string filename = sep < 0 ? path : path[(sep + 1)..];
        return new ValueTask<ValueRef>(ValueRef.FromString(filename));
    }
}
