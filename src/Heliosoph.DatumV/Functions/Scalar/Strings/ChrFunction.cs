using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// PostgreSQL <c>chr(int) → text</c>. Returns the character corresponding
/// to the given Unicode code point. Throws when <c>code</c> is non-positive
/// or outside the valid Unicode range (1 to 0x10FFFF, excluding the
/// surrogate range U+D800–U+DFFF). Null input propagates to null.
/// </summary>
public sealed class ChrFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "chr";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.String;

    /// <inheritdoc />
    public static string Description =>
        "Returns the character corresponding to the given Unicode code point.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters: [new ParameterSpec("code", DataKindMatcher.Family(DataKindFamily.IntegerFamily))],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.String)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<ChrFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ValueRef arg = arguments.Span[0];
        if (arg.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(DataKind.String));
        }
        if (!arg.TryToInt32(out int code))
        {
            throw new FunctionArgumentException(Name, $"argument 'code' of kind {arg.Kind} is out of range for Int32.");
        }
        if (code <= 0 || code > 0x10FFFF)
        {
            throw new FunctionArgumentException(Name, $"Unicode code point {code} is out of range (1..0x10FFFF).");
        }
        if (code >= 0xD800 && code <= 0xDFFF)
        {
            throw new FunctionArgumentException(Name, $"Unicode code point {code:X} is in the surrogate range and cannot stand alone.");
        }
        return new ValueTask<ValueRef>(ValueRef.FromString(new Rune(code).ToString()));
    }
}
