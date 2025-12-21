using DatumIngest.Execution;
using DatumIngest.Functions.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Json;

/// <summary>
/// Parses a JSON text string into a <see cref="DataKind.Json"/> value backed
/// by canonical CBOR bytes. Null input propagates to null output. Throws
/// when the input is not valid JSON or contains numbers that cannot be
/// represented as int64 or finite float64.
/// </summary>
public sealed class JsonParseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "json_parse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Json;

    /// <inheritdoc />
    public static string Description =>
        "Parses a JSON text string into a Json value (canonical CBOR-backed). "
        + "The result can be queried with json_value / json_query without re-parsing.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("text", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Json)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JsonParseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.Json));
        }
        byte[] cbor = CborJsonCodec.EncodeFromJsonText(input.AsString());
        return new ValueTask<ValueRef>(ValueRef.FromBytes(DataKind.Json, cbor));
    }
}
