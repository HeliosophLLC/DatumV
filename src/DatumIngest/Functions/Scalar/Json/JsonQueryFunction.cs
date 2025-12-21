using DatumIngest.Execution;
using DatumIngest.Functions.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Json;

/// <summary>
/// Extracts a subdocument (object or array) at the given <c>path</c> from
/// a Json value as a new Json value. Returns SQL NULL when the path is
/// missing or resolves to a scalar (use <c>json_value</c> for scalars).
/// </summary>
/// <remarks>
/// Subdocument extraction returns a <see cref="ValueRef.FromJsonSlice"/>
/// view over the original CBOR bytes — no copy in the function-chain path.
/// At the arena boundary, only the slice's bytes copy into the target arena
/// (not the whole source document).
/// </remarks>
public sealed class JsonQueryFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "json_query";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Json;

    /// <inheritdoc />
    public static string Description =>
        "Extracts an object or array subdocument at a JSONPath as a new Json value. "
        + "Returns NULL when the path is missing or the value is a scalar — use "
        + "json_value for scalars.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("doc", DataKindMatcher.Exact(DataKind.Json)),
                new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Json)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JsonQueryFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef doc = args[0];
        ValueRef path = args[1];

        if (doc.IsNull || path.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Json));
        }

        // Take the source's underlying segment (zero-copy). WalkPath consumes
        // its own internal buffer for the CborReader, but its returned offsets
        // are byte-positions within the input span — which is byte-equivalent
        // to the source segment, so we can rebuild a view over the original
        // backing array.
        ArraySegment<byte> sourceSegment = doc.AsByteSegment();
        CborJsonCodec.CborWalkResult result = CborJsonCodec.WalkPath(sourceSegment, path.AsString());

        if (result.Kind != CborJsonCodec.CborWalkResultKind.Subdocument)
        {
            // Missing path or scalar leaf → SQL NULL.
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Json));
        }

        // Compose a slice over the source's backing array — no copy. The
        // arena boundary (ValueRef.ToDataValue) copies only the slice bytes
        // into the target store when the value finally materialises.
        ArraySegment<byte> resultSlice = new(
            sourceSegment.Array!,
            sourceSegment.Offset + result.SubdocOffset,
            result.SubdocLength);
        return new ValueTask<ValueRef>(ValueRef.FromJsonSlice(resultSlice));
    }
}
