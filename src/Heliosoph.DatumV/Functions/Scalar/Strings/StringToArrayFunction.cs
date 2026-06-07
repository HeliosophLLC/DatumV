using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>string_to_array(string, delimiter [, null_string]) → text[]</c>.
/// Splits <c>value</c> on the literal <c>delimiter</c> into an Array.
/// A null <c>delimiter</c> splits the string into individual code points;
/// an empty (non-null) <c>delimiter</c> returns a 1-element array containing
/// <c>value</c>. Fields equal to <c>null_string</c> become NULL elements;
/// a null <c>null_string</c> means no null-mapping is applied. A null
/// <c>value</c> propagates to a null array.
/// </summary>
public sealed class StringToArrayFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "string_to_array";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Splits a string by a delimiter into an Array; fields matching null_string become NULL.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",     DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value",       DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("delimiter",   DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("null_string", DataKindMatcher.Exact(DataKind.String)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.String))),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<StringToArrayFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[0].IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.NullArray(DataKind.String));
        }
        string value = args[0].AsString();
        string? nullString = args.Length == 3 && !args[2].IsNull ? args[2].AsString() : null;

        IEnumerable<string> parts;
        if (args[1].IsNull)
        {
            // Split into characters (code points).
            parts = SplitIntoCodePoints(value);
        }
        else
        {
            string delim = args[1].AsString();
            if (delim.Length == 0)
            {
                parts = new[] { value };
            }
            else
            {
                parts = value.Split(delim, StringSplitOptions.None);
            }
        }

        List<ValueRef> elements = [];
        foreach (string part in parts)
        {
            if (nullString is not null && part == nullString)
            {
                elements.Add(ValueRef.Null(DataKind.String));
            }
            else
            {
                elements.Add(ValueRef.FromString(part));
            }
        }
        return new ValueTask<ValueRef>(ValueRef.FromArray(DataKind.String, elements.ToArray()));
    }

    private static IEnumerable<string> SplitIntoCodePoints(string value)
    {
        foreach (Rune rune in value.EnumerateRunes())
        {
            yield return rune.ToString();
        }
    }
}
