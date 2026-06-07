using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>normalize(text [, form]) → text</c>. Returns <c>value</c>
/// in the requested Unicode normalization form (<c>NFC</c> default,
/// <c>NFD</c>, <c>NFKC</c>, <c>NFKD</c>). Form is case-insensitive. Null
/// input propagates to null.
/// </summary>
public sealed class NormalizeFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "normalize";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the string in the requested Unicode normalization form (NFC default, NFD, NFKC, NFKD).";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("form",  DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<NormalizeFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull || (args.Length == 2 && args[1].IsNull))
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }

        string value = args[0].AsString();
        string formText = args.Length == 2 ? args[1].AsString() : "NFC";

        NormalizationForm form = formText.ToUpperInvariant() switch
        {
            "NFC"  => NormalizationForm.FormC,
            "NFD"  => NormalizationForm.FormD,
            "NFKC" => NormalizationForm.FormKC,
            "NFKD" => NormalizationForm.FormKD,
            _ => throw new FunctionArgumentException(Name, $"unknown normalization form '{formText}' (expected NFC, NFD, NFKC, or NFKD)."),
        };

        return new ValueTask<ValueRef>(ValueRef.FromString(value.Normalize(form)));
    }
}
