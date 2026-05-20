using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.File;

/// <summary>
/// Returns <c>path</c> with its file extension replaced by <c>new_ext</c>. The new extension may be passed with or without a leading
/// dot — both <c>"csv"</c> and <c>".csv"</c> work. Passing an empty string
/// strips the extension entirely (and removes the trailing dot, if any).
/// Hidden Unix files like <c>.bashrc</c> get the new extension appended
/// rather than replacing the leading dot. Null input on either argument
/// propagates to null output.
/// </summary>
public sealed class ChangeFilenameExtFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "change_filename_ext";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Returns the path with its file extension replaced; empty new_ext strips the extension.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("new_ext", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ChangeFilenameExtFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || args[1].IsNull)
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));

        string path = args[0].AsString();
        ReadOnlySpan<char> newExt = args[1].AsString().AsSpan();
        if (newExt.Length > 0 && newExt[0] == '.') newExt = newExt[1..];

        int sep = PathOps.LastSeparatorIndex(path);
        int filenameStart = sep + 1;
        int dot = path.LastIndexOf('.');

        // Determine where the existing extension starts (or where to append).
        // Hidden-file rule mirrors get_filename_ext: a leading-dot-only filename
        // has no extension to replace, so we append.
        int extDotIndex;
        bool hasExt = dot > filenameStart && dot < path.Length - 1;
        bool trailingDot = dot == path.Length - 1 && dot > filenameStart;
        if (hasExt || trailingDot)
            extDotIndex = dot;
        else
            extDotIndex = path.Length;

        ReadOnlySpan<char> baseSpan = path.AsSpan(0, extDotIndex);
        string result = newExt.Length == 0
            ? baseSpan.ToString()
            : string.Concat(baseSpan, ".", newExt);
        return new ValueTask<ValueRef>(ValueRef.FromString(result));
    }
}
