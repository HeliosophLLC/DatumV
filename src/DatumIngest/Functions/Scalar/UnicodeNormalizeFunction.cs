using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Converts a string to the specified Unicode normalization form.
/// <c>normalize(text)</c> defaults to NFC.
/// <c>normalize(text, form)</c> — form is one of: 'NFC', 'NFD', 'NFKC', 'NFKD'.
/// PostgreSQL compatible.
/// </summary>
public sealed class UnicodeNormalizeFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "normalize";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new ArgumentException("normalize() requires 1 or 2 arguments: text [, form].");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"normalize() first argument must be String, got {argumentKinds[0]}.");
        }

        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new ArgumentException(
                $"normalize() second argument (form) must be String, got {argumentKinds[1]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString();

        NormalizationForm form = NormalizationForm.FormC;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            form = arguments[1].AsString().ToUpperInvariant() switch
            {
                "NFC" => NormalizationForm.FormC,
                "NFD" => NormalizationForm.FormD,
                "NFKC" => NormalizationForm.FormKC,
                "NFKD" => NormalizationForm.FormKD,
                _ => throw new InvalidOperationException(
                    $"normalize(): unrecognized normalization form '{arguments[1].AsString()}'. Expected NFC, NFD, NFKC, or NFKD."),
            };
        }

        return DataValue.FromString(input.Normalize(form));
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string input = arguments[0].AsString(store);

        NormalizationForm form = NormalizationForm.FormC;
        if (arguments.Length == 2 && !arguments[1].IsNull)
        {
            form = arguments[1].AsString(store).ToUpperInvariant() switch
            {
                "NFC" => NormalizationForm.FormC,
                "NFD" => NormalizationForm.FormD,
                "NFKC" => NormalizationForm.FormKC,
                "NFKD" => NormalizationForm.FormKD,
                _ => throw new InvalidOperationException(
                    $"normalize(): unrecognized normalization form '{arguments[1].AsString(store)}'. Expected NFC, NFD, NFKC, or NFKD."),
            };
        }

        return DataValue.FromString(input.Normalize(form), store);
    }
}
