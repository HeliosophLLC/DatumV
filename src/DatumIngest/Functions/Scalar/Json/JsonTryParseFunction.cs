using DatumIngest.Execution;
using DatumIngest.Functions.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Json;

/// <summary>
/// Like <see cref="JsonParseFunction"/>, but returns SQL NULL on parse failure
/// (invalid JSON, number out of range) instead of throwing. The null-on-failure
/// counterpart used when an LLM might hand back free-form text that isn't
/// actually JSON, and the caller wants to filter or fall back rather than
/// abort the query.
/// </summary>
public sealed class JsonTryParseFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "json_try_parse";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Json;

    /// <inheritdoc />
    public static string Description =>
        "Parses a JSON text string into a Json value, or returns NULL when the input "
        + "isn't valid JSON. Use this when the source text may not be well-formed (e.g. "
        + "free-form LLM output) and you want to filter or fall back rather than abort.";

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
        FunctionMetadata.Validate<JsonTryParseFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        if (input.IsNull)
        {
            return ValueRef.Null(DataKind.Json);
        }

        try
        {
            byte[] cbor = CborJsonCodec.EncodeFromJsonText(input.AsString());
            return ValueRef.FromBytes(DataKind.Json, cbor);
        }
        catch (System.Text.Json.JsonException)
        {
            return ValueRef.Null(DataKind.Json);
        }
        catch (OverflowException)
        {
            return ValueRef.Null(DataKind.Json);
        }
    }
}
