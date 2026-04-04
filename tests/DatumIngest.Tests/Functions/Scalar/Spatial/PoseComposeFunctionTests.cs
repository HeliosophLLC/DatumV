using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Tests for <see cref="PoseIdentityFunction"/> and
/// <see cref="PoseComposeFunction"/>. Verifies the algebraic properties
/// callers rely on for chained reconstruction: identity is a no-op,
/// composition matches translation chaining, composition with the matrix
/// from <see cref="PoseTranslateFunction"/> stacks correctly, and the
/// pose_compose order matches the contract in PoseFromRgbdFunction
/// (cumulative on the left, single-step on the right).
/// </summary>
public sealed class PoseComposeFunctionTests : ServiceTestBase
{
    [Fact]
    public void IdentityMetadata_Exposes()
    {
        Assert.Equal("pose_identity", PoseIdentityFunction.Name);
        Assert.Equal(FunctionCategory.Spatial, PoseIdentityFunction.Category);
    }

    [Fact]
    public void ComposeMetadata_Exposes()
    {
        Assert.Equal("pose_compose", PoseComposeFunction.Name);
        Assert.Equal(FunctionCategory.Spatial, PoseComposeFunction.Category);
    }

    [Fact]
    public async Task Identity_ReturnsExpectedMatrix()
    {
        PoseIdentityFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            ReadOnlyMemory<ValueRef>.Empty,
            MakeFrame(),
            default);

        float[] expected =
        [
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        AssertPoseEquals(expected, result, tol: 0f);
    }

    [Fact]
    public async Task Compose_IdentityWithTranslation_IsTranslation()
    {
        ValueRef identity = await Identity();
        ValueRef translate = await Translate(1f, 2f, 3f);

        ValueRef result = await Compose(identity, translate);

        float[] expected =
        [
            1, 0, 0, 1,
            0, 1, 0, 2,
            0, 0, 1, 3,
            0, 0, 0, 1,
        ];
        AssertPoseEquals(expected, result, tol: 1e-6f);
    }

    [Fact]
    public async Task Compose_TranslationWithIdentity_IsTranslation()
    {
        ValueRef translate = await Translate(1f, 2f, 3f);
        ValueRef identity = await Identity();

        ValueRef result = await Compose(translate, identity);

        float[] expected =
        [
            1, 0, 0, 1,
            0, 1, 0, 2,
            0, 0, 1, 3,
            0, 0, 0, 1,
        ];
        AssertPoseEquals(expected, result, tol: 1e-6f);
    }

    [Fact]
    public async Task Compose_TwoTranslations_StacksAdditively()
    {
        // Pure translations commute and add componentwise — this is the
        // simplest sanity check that composition order isn't flipping signs
        // somewhere in the inner product.
        ValueRef a = await Translate(1f, 2f, 3f);
        ValueRef b = await Translate(10f, 20f, 30f);

        ValueRef result = await Compose(a, b);

        float[] expected =
        [
            1, 0, 0, 11,
            0, 1, 0, 22,
            0, 0, 1, 33,
            0, 0, 0,  1,
        ];
        AssertPoseEquals(expected, result, tol: 1e-6f);
    }

    [Fact]
    public async Task Compose_RotationThenTranslation_OrderMatters()
    {
        // R = 90° rotation about +Y (in row-major affine):
        //   [ 0  0  1  0 ]
        //   [ 0  1  0  0 ]
        //   [-1  0  0  0 ]
        //   [ 0  0  0  1 ]
        // T = translation by (1, 0, 0).
        //
        // (T * R) applied to (0, 0, 0) → (1, 0, 0)
        //                    applied to (1, 0, 0) → rotates (1,0,0) to (0,0,-1) then adds (1,0,0) → (1, 0, -1)
        //
        // (R * T) applied to (0, 0, 0) → T puts it at (1,0,0); R rotates to (0, 0, -1).
        //
        // Different outputs → composition is non-commutative when rotation is involved.
        ValueRef rot = MakeRot90Y();
        ValueRef trans = await Translate(1f, 0f, 0f);

        ValueRef tThenR = await Compose(trans, rot);
        ValueRef rThenT = await Compose(rot, trans);

        // (T * R) — apply R first, then T. Translation column is just (1,0,0).
        float[] tThenRExpected =
        [
             0, 0, 1, 1,
             0, 1, 0, 0,
            -1, 0, 0, 0,
             0, 0, 0, 1,
        ];

        // (R * T) — apply T first, then R. Translation column = R * (1,0,0) = (0, 0, -1).
        float[] rThenTExpected =
        [
             0, 0, 1,  0,
             0, 1, 0,  0,
            -1, 0, 0, -1,
             0, 0, 0,  1,
        ];

        AssertPoseEquals(tThenRExpected, tThenR, tol: 1e-6f);
        AssertPoseEquals(rThenTExpected, rThenT, tol: 1e-6f);
    }

    [Fact]
    public async Task Compose_RecursiveCteShape_AccumulatesAsExpected()
    {
        // Simulates the recursive-CTE fold a caller would write:
        //   accumulated_0 = identity
        //   accumulated_N = pose_compose(accumulated_(N-1), step_N)
        // with each step a +1 metre translation along +X. After 5 steps the
        // accumulated pose should be a +5m X translation.
        ValueRef acc = await Identity();
        ValueRef step = await Translate(1f, 0f, 0f);
        for (int i = 0; i < 5; i++)
        {
            acc = await Compose(acc, step);
        }

        float[] expected =
        [
            1, 0, 0, 5,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        ];
        AssertPoseEquals(expected, acc, tol: 1e-5f);
    }

    [Fact]
    public async Task Compose_NullArg_ReturnsNullArray()
    {
        PoseComposeFunction fn = new();
        ValueRef identity = await Identity();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.NullArray(DataKind.Float32), identity },
            MakeFrame(),
            default);

        Assert.True(result.IsNull);
        Assert.Equal(DataKind.Float32, result.Kind);
    }

    [Fact]
    public async Task Compose_WrongArrayLength_Throws()
    {
        PoseComposeFunction fn = new();
        ValueRef good = await Identity();
        ValueRef bad = ValueRef.FromPrimitiveArray(new float[] { 1, 0, 0, 0 }, DataKind.Float32);

        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(async () =>
        {
            await fn.ExecuteAsync(new ValueRef[] { good, bad }, MakeFrame(), default);
        });
        Assert.Contains("16 Float32 values", ex.Message);
    }

    // ─────────────────────── Helpers ───────────────────────

    private async Task<ValueRef> Identity()
    {
        PoseIdentityFunction fn = new();
        return await fn.ExecuteAsync(ReadOnlyMemory<ValueRef>.Empty, MakeFrame(), default);
    }

    private async Task<ValueRef> Translate(float dx, float dy, float dz)
    {
        PoseTranslateFunction fn = new();
        return await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromFloat32(dx), ValueRef.FromFloat32(dy), ValueRef.FromFloat32(dz) },
            MakeFrame(),
            default);
    }

    private async Task<ValueRef> Compose(ValueRef a, ValueRef b)
    {
        PoseComposeFunction fn = new();
        return await fn.ExecuteAsync(new ValueRef[] { a, b }, MakeFrame(), default);
    }

    private static ValueRef MakeRot90Y()
    {
        float[] m =
        [
             0, 0, 1, 0,
             0, 1, 0, 0,
            -1, 0, 0, 0,
             0, 0, 0, 1,
        ];
        return ValueRef.FromPrimitiveArray(m, DataKind.Float32);
    }

    private void AssertPoseEquals(float[] expected, ValueRef actual, float tol)
    {
        Assert.False(actual.IsNull);
        Assert.Equal(DataKind.Float32, actual.Kind);
        Assert.True(actual.IsArray);

        EvaluationFrame f = MakeFrame();
        ReadOnlySpan<float> got = actual.ToDataValue(f.Source).AsArraySpan<float>(f.Source, f.SidecarRegistry);
        Assert.Equal(16, got.Length);
        for (int i = 0; i < 16; i++)
        {
            Assert.Equal(expected[i], got[i], tol);
        }
    }

    private EvaluationFrame MakeFrame()
    {
        Pool pool = GetService<Pool>();
        Arena arena = pool.Backing.RentArena();
        return new EvaluationFrame(Row.Empty, arena, arena, new MemoryAccountant(), types: new TypeRegistry());
    }
}
