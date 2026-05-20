using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.File;

/// <summary>
/// Returns the file extension of a path, without the leading dot (e.g. <c>"csv"</c>
/// for <c>"data/sales.csv"</c>). Returns an empty string when no extension is
/// present, when the path ends in a dot, or when the dot precedes the last
/// separator. Null input propagates to null output.
/// </summary>
/// <remarks>
/// The dot is stripped because <c>WHERE get_filename_ext(path) = 'csv'</c>
/// reads more naturally than <c>= '.csv'</c>. Hidden Unix files like
/// <c>.bashrc</c> have an empty extension — the leading dot is treated as part
/// of the name, not the extension.
/// </remarks>
public sealed class GetFilenameExtFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "get_filename_ext";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Returns the file extension of a path without the leading dot, or empty when there is none.";

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
        FunctionMetadata.Validate<GetFilenameExtFunction>(argumentKinds);

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

        // Hidden-file rule: if the only dot is the very first character of the
        // filename portion, treat it as part of the name (".bashrc" → no ext).
        int dot = path.LastIndexOf('.');
        if (dot <= filenameStart || dot == path.Length - 1)
            return new ValueTask<ValueRef>(ValueRef.FromString(string.Empty));

        return new ValueTask<ValueRef>(ValueRef.FromString(path[(dot + 1)..]));
    }
}
