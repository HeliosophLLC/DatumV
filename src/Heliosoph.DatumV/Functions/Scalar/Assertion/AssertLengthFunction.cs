using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Assertion;

/// <summary>
/// Returns the input verbatim when its length matches the expected value;
/// throws otherwise. Accepts strings (character count) and arrays (element
/// count). Null input passes through unchecked.
/// </summary>
public sealed class AssertLengthFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "assert_length";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Assertion;

    /// <inheritdoc />
    public static string Description =>
        "Returns the input when its length (string characters or array elements) " +
        "matches the expected value; throws otherwise. Null inputs pass through unchecked.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("expected", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),

        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("value", DataKindMatcher.Any, IsArray: ArrayMatch.Array),
                new ParameterSpec("expected", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Scalar),
                new ParameterSpec("message", DataKindMatcher.Exact(DataKind.String), IsOptional: true, IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.SameAs(0)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<AssertLengthFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef value = args[0];
        ValueRef expected = args[1];
        if (value.IsNull || expected.IsNull) return new ValueTask<ValueRef>(value);

        long expectedLength = ToInt64(expected);
        long actualLength = value.IsArray ? value.GetArrayLength() : value.AsString().Length;
        if (actualLength == expectedLength) return new ValueTask<ValueRef>(value);

        AssertHelpers.Throw(
            AssertHelpers.UserMessage(args, 2),
            $"value {AssertHelpers.Display(value)} had length {actualLength}, expected {expectedLength}");
        return default;
    }

    private static long ToInt64(ValueRef value) => value.Kind switch
    {
        DataKind.Int8 => value.AsInt8(),
        DataKind.UInt8 => value.AsUInt8(),
        DataKind.Int16 => value.AsInt16(),
        DataKind.UInt16 => value.AsUInt16(),
        DataKind.Int32 => value.AsInt32(),
        DataKind.UInt32 => value.AsUInt32(),
        DataKind.Int64 => value.AsInt64(),
        DataKind.UInt64 => (long)value.AsUInt64(),
        _ => throw new InvalidOperationException(
            $"assert_length: expected length is not an integer kind ({value.Kind})."),
    };

}
