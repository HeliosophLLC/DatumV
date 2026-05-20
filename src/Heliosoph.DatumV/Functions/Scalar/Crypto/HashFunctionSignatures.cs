using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Crypto;

/// <summary>
/// Shared signature shape for hash functions that accept either a string
/// (hashed as UTF-8) or a byte array and return a fresh <see cref="DataKind.UInt8"/>[]
/// digest. Centralised so individual SHA-N implementations declare only their
/// per-algorithm digest length and computation.
/// </summary>
internal static class HashFunctionSignatures
{
    public static IReadOnlyList<FunctionSignatureVariant> Build() =>
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("input", DataKindMatcher.Exact(DataKind.String), IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("input", DataKindMatcher.Exact(DataKind.UInt8), IsArray: ArrayMatch.Array),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(DataKind.UInt8))),
    ];
}
