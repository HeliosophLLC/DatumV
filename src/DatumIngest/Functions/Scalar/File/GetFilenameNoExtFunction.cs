using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// Returns the filename portion of a path with its extension stripped
/// (e.g. <c>"data/sales.csv"</c> → <c>"sales"</c>). Hidden Unix files like
/// <c>.bashrc</c> are returned unchanged — the leading dot is treated as
/// part of the name, not the extension. Null input propagates to null output.
/// </summary>
public sealed class GetFilenameNoExtFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "get_filename_no_ext";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Returns the filename portion of a path with its extension stripped.";

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
        FunctionMetadata.Validate<GetFilenameNoExtFunction>(argumentKinds);

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
        int filenameStart = sep + 1;

        int dot = path.LastIndexOf('.');
        // Keep the whole filename when there is no extension, when the path
        // ends in a dot, or when the only dot is the leading hidden-file dot.
        string filename = path[filenameStart..];
        if (dot <= filenameStart || dot == path.Length - 1)
            return new ValueTask<ValueRef>(ValueRef.FromString(filename));

        return new ValueTask<ValueRef>(ValueRef.FromString(path[filenameStart..dot]));
    }
}
