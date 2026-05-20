using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions;

/// <summary>
/// PR2: signature dispatch tests for the four-way <see cref="ArrayMatch"/> matrix
/// (Either / Scalar / Array / FlatArray / MultiDimArray) against the array-and-multi-dim
/// shape tuple consumed by <see cref="FunctionMetadata.TryMatchWithShape"/>.
/// </summary>
public sealed class MultiDimDispatchTests : ServiceTestBase
{
    private static FunctionSignatureVariant MakeVariant(ArrayMatch matcher) =>
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("x", DataKindMatcher.Exact(DataKind.Float32), IsArray: matcher),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Float32));

    // (Kind, IsArray, IsMultiDim) inputs.
    private static (DataKind, bool, bool)[] Shape(DataKind k, bool isArray, bool isMultiDim) =>
        [(k, isArray, isMultiDim)];

    // ───────────────────── Either: accepts everything matching kind ─────────────────────

    [Fact]
    public void Either_AcceptsScalar_FlatArray_MultiDimArray()
    {
        var variant = MakeVariant(ArrayMatch.Either);
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, false, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, true)));
    }

    // ───────────────────── Scalar: rejects arrays ─────────────────────

    [Fact]
    public void Scalar_RejectsBothFlatAndMultiDimArrays()
    {
        var variant = MakeVariant(ArrayMatch.Scalar);
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, false, false)));
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, false)));
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, true)));
    }

    // ───────────────────── Array: accepts any array (flat OR multi-dim) ─────────────────────

    [Fact]
    public void Array_AcceptsFlatAndMultiDim_RejectsScalar()
    {
        var variant = MakeVariant(ArrayMatch.Array);
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, false, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, true)));
    }

    // ───────────────────── FlatArray: rejects multi-dim ─────────────────────

    [Fact]
    public void FlatArray_AcceptsFlat_RejectsMultiDimAndScalar()
    {
        var variant = MakeVariant(ArrayMatch.FlatArray);
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, false, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, false)));
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, true)));
    }

    // ───────────────────── MultiDimArray: requires multi-dim ─────────────────────

    [Fact]
    public void MultiDimArray_AcceptsMultiDim_RejectsFlatAndScalar()
    {
        var variant = MakeVariant(ArrayMatch.MultiDimArray);
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, false, false)));
        Assert.False(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, false)));
        Assert.True(FunctionMetadata.TryMatchWithShape(variant, Shape(DataKind.Float32, true, true)));
    }

    // ───────────────────── Overload selection: flat vs multi-dim ─────────────────────

    [Fact]
    public void Overload_FlatAndMultiDim_DispatchesToCorrectVariant()
    {
        var flatVariant = MakeVariant(ArrayMatch.FlatArray) with
        {
            ReturnType = ReturnTypeRule.Constant(DataKind.Float64),
        };
        var multiDimVariant = MakeVariant(ArrayMatch.MultiDimArray) with
        {
            ReturnType = ReturnTypeRule.Constant(DataKind.Int32),
        };
        IReadOnlyList<FunctionSignatureVariant> signatures = [flatVariant, multiDimVariant];

        var flatPick = FunctionMetadata.MatchVariantWithShape(signatures, Shape(DataKind.Float32, true, false));
        Assert.NotNull(flatPick);
        Assert.Equal(DataKind.Float64, flatPick.ReturnType.Resolve([DataKind.Float32]));

        var mdPick = FunctionMetadata.MatchVariantWithShape(signatures, Shape(DataKind.Float32, true, true));
        Assert.NotNull(mdPick);
        Assert.Equal(DataKind.Int32, mdPick.ReturnType.Resolve([DataKind.Float32]));

        // Scalar input matches neither.
        Assert.Null(FunctionMetadata.MatchVariantWithShape(signatures, Shape(DataKind.Float32, false, false)));
    }
}
