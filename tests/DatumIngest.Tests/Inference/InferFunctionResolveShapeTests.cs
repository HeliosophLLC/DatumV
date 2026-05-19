using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Inference;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Inference;

/// <summary>
/// Unit tests for <see cref="InferFunction.ResolveShape"/> — derives the
/// concrete tensor shape from a (possibly partially-dynamic) session spec
/// plus the supplied element count. Lives at the heart of the 1-arg
/// <c>infer(value)</c> form and the multi-input fall-through path
/// (missing shape entry against a 0- or 1-dynamic-dim spec).
/// </summary>
public sealed class InferFunctionResolveShapeTests
{
    private static TensorSpec Spec(params int?[] shape) =>
        new("test", DataKind.Float32, shape);

    [Fact]
    public void ResolveShape_FullyConcreteSpec_ReturnsSpecVerbatim()
    {
        // Shape product (1 * 3 * 4 = 12) is irrelevant when no dim is
        // dynamic — the spec is the answer regardless of elementCount.
        int[] resolved = InferFunction.ResolveShape(Spec(1, 3, 4), elementCount: 12);
        Assert.Equal(new[] { 1, 3, 4 }, resolved);

        // SDXL timestep case: rank-1, single fixed dim, scalar input.
        int[] timestep = InferFunction.ResolveShape(Spec(1), elementCount: 1);
        Assert.Equal(new[] { 1 }, timestep);
    }

    [Fact]
    public void ResolveShape_Rank0Spec_ReturnsEmpty()
    {
        // Rank-0 == scalar. The marshaler is supposed to feed exactly one
        // element regardless of elementCount; ResolveShape doesn't enforce
        // that (caller responsibility), just returns the empty shape.
        int[] resolved = InferFunction.ResolveShape(Spec(), elementCount: 1);
        Assert.Empty(resolved);
    }

    [Fact]
    public void ResolveShape_OneDynamicDim_AbsorbsRemainder()
    {
        // SDXL-Turbo UNet timestep spec [?] + 1-element scalar → [1].
        int[] timestepDyn = InferFunction.ResolveShape(Spec((int?)null), elementCount: 1);
        Assert.Equal(new[] { 1 }, timestepDyn);

        // BERT-class encoder one-input dispatch: spec [-1, 768] + 768
        // elements → [1, 768]; + 1536 elements → [2, 768].
        int[] one = InferFunction.ResolveShape(Spec((int?)null, 768), elementCount: 768);
        Assert.Equal(new[] { 1, 768 }, one);

        int[] two = InferFunction.ResolveShape(Spec((int?)null, 768), elementCount: 1536);
        Assert.Equal(new[] { 2, 768 }, two);

        // Trailing dynamic dim with fixed leading dims: PP-OCR-det
        // detector segmentation output [1, 1, ?, ?] is a hypothetical
        // single-dynamic case; we exercise the equivalent with
        // [1, 4, ?] + 12 → [1, 4, 3].
        int[] trailing = InferFunction.ResolveShape(Spec(1, 4, null), elementCount: 12);
        Assert.Equal(new[] { 1, 4, 3 }, trailing);
    }

    [Fact]
    public void ResolveShape_OneDynamicDim_NonDivisibleRemainderThrows()
    {
        // 13 doesn't divide cleanly into the [4, ?] fixed-dim product (4),
        // so the dynamic dim cannot be resolved. This catches body-side
        // mistakes (passing the wrong-shaped array into infer) at a point
        // where the error can still name the spec and the buffer length.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InferFunction.ResolveShape(Spec(4, null), elementCount: 13));
        Assert.Contains("13", ex.Message);
        Assert.Contains("fixed-dim product", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShape_TwoDynamicDims_ThrowsNotSupported()
    {
        // BERT-style [batch, seq_len] specs need the caller to supply an
        // explicit shape — ResolveShape can't disambiguate without more
        // hints. We document this as NotSupportedException so the body
        // author gets steered to the 2-arg infer() shape literal.
        NotSupportedException ex = Assert.Throws<NotSupportedException>(() =>
            InferFunction.ResolveShape(Spec((int?)null, null), elementCount: 768));
        Assert.Contains("multiple dynamic dimensions", ex.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveShape_ZeroElementCountIntoOneDynamicDim_ReturnsZeroDim()
    {
        // 0 elements cleanly divide any fixed-dim product, so the dynamic
        // dim resolves to 0. The downstream session will reject the
        // 0-shaped tensor on its own terms; ResolveShape is just doing
        // shape arithmetic. Pinned here so a future "is 0 divisible by
        // product" refactor doesn't silently flip semantics.
        int[] resolved = InferFunction.ResolveShape(Spec(4, null), elementCount: 0);
        Assert.Equal(new[] { 4, 0 }, resolved);
    }

    [Fact]
    public void ResolveShape_ZeroProductSpec_Throws()
    {
        // The other way to trip the divide check: a fixed-dim spec whose
        // product is 0 (any zero-sized dim). 0 / 0 is undefined; the
        // resolver rejects this explicitly rather than silently emitting
        // a degenerate shape.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            InferFunction.ResolveShape(Spec(0, null), elementCount: 5));
        Assert.Contains("doesn't divide", ex.Message);
    }
}
