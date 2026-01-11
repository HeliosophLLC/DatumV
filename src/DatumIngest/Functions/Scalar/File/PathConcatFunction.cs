using System.Text;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.File;

/// <summary>
/// Joins two or more path segments using <c>/</c> as the separator. Trailing
/// separators on each segment (and leading separators on segments after the
/// first) are stripped before joining, so <c>path_concat('a/', '/b')</c>
/// yields <c>'a/b'</c>. Any null argument null-propagates to a null result.
/// </summary>
/// <remarks>
/// <para>
/// Always emits <c>/</c> regardless of platform — SQL is meant to be
/// deterministic across hosts. Existing backslashes inside segments are
/// preserved verbatim; only separators at the segment boundaries are
/// normalised.
/// </para>
/// </remarks>
public sealed class PathConcatFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "path_concat";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.File;

    /// <inheritdoc />
    public static string Description =>
        "Joins two or more path segments with /, trimming separators at boundaries.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [],
            VariadicTrailing: new VariadicSpec(
                "segments",
                DataKindMatcher.Exact(DataKind.String),
                MinOccurrences: 2),
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<PathConcatFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].IsNull)
                return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        StringBuilder builder = new();
        for (int i = 0; i < args.Length; i++)
        {
            ReadOnlySpan<char> segment = args[i].AsString().AsSpan();
            // Strip leading separators on every segment after the first.
            if (i > 0)
            {
                while (segment.Length > 0 && PathOps.IsSeparator(segment[0]))
                    segment = segment[1..];
            }
            // Strip trailing separators on every segment except the last.
            if (i < args.Length - 1)
            {
                while (segment.Length > 0 && PathOps.IsSeparator(segment[^1]))
                    segment = segment[..^1];
            }

            if (segment.Length == 0) continue;
            if (builder.Length > 0) builder.Append('/');
            builder.Append(segment);
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(builder.ToString()));
    }
}
