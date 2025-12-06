using DatumIngest.Execution;
using DatumIngest.Functions.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Json;

/// <summary>
/// Re-emits a Json value as JSON text. Used by output writers and explicit
/// <c>CAST(value AS String)</c>. Null input propagates to null output.
/// </summary>
/// <remarks>
/// Round-trip is structurally identical but not byte-identical: the canonical
/// CBOR encoding chosen at parse time may have collapsed equivalent
/// representations (e.g. <c>1.0</c> → <c>1</c> when the value is exactly
/// integral, map key ordering may have been normalised). The re-emitted JSON
/// reflects the canonical form.
/// </remarks>
public sealed class JsonToTextFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "json_to_text";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Json;

    /// <inheritdoc />
    public static string Description =>
        "Re-emits a Json value as JSON text. The output reflects the canonical "
        + "encoding chosen at parse time (number normalisation, sorted map keys).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("doc", DataKindMatcher.Exact(DataKind.Json)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JsonToTextFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef doc = arguments[0];
        if (doc.IsNull)
        {
            return ValueRef.Null(DataKind.String);
        }
        string text = CborJsonCodec.DecodeToJsonText(doc.AsByteSpan());
        return ValueRef.FromString(text);
    }
}
