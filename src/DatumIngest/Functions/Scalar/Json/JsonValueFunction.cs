using DatumIngest.Execution;
using DatumIngest.Functions.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Json;

/// <summary>
/// Extracts a scalar value at the given <c>path</c> from a Json value.
/// Returns the typed scalar (String / Int64 / Float64 / Boolean / typed Null
/// for JSON's <c>null</c>); returns SQL NULL when the path doesn't resolve
/// to a value or resolves to an object/array (use <c>json_query</c> for those).
/// </summary>
/// <remarks>
/// Path syntax supports the JSONPath subset <c>$</c>, <c>$.field</c>,
/// <c>$.foo.bar</c>, <c>$.arr[N]</c>, and combinations. Because <c>json_value</c>
/// returns a typed scalar (not a string), <c>typeof(json_value(doc, '$.x'))</c>
/// works naturally — Int64 for an integer field, Float64 for a float, etc.
/// </remarks>
public sealed class JsonValueFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "json_value";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Json;

    /// <inheritdoc />
    public static string Description =>
        "Extracts a scalar value at a JSONPath. Returns the typed value (Int64, Float64, "
        + "String, Boolean, or NULL). Returns NULL when the path is missing or the value "
        + "is an object/array — use json_query to extract subdocuments.";

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
            // Result kind is data-dependent — only known once the path resolves.
            // String is the placeholder used elsewhere (cf. CastFunction); the
            // emitted DataValue carries the runtime kind regardless.
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<JsonValueFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef doc = arguments[0];
        ValueRef path = arguments[1];

        if (doc.IsNull || path.IsNull)
        {
            return ValueRef.Null(DataKind.Unknown);
        }

        CborJsonCodec.CborWalkResult result =
            CborJsonCodec.WalkPath(doc.AsByteSpan(), path.AsString());

        return result.Kind switch
        {
            // Missing path → SQL NULL (mirrors PG's json_value semantics).
            CborJsonCodec.CborWalkResultKind.NotFound => ValueRef.Null(DataKind.Unknown),
            // Scalar leaf → return the typed value as-is.
            CborJsonCodec.CborWalkResultKind.Scalar => result.Scalar,
            // Subdocument (object/array) → NULL. json_query is the right function for those.
            CborJsonCodec.CborWalkResultKind.Subdocument => ValueRef.Null(DataKind.Unknown),
            _ => ValueRef.Null(DataKind.Unknown),
        };
    }
}
