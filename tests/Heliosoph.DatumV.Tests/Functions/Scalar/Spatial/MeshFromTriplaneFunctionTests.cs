using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Tests.Functions.Scalar.Spatial;

/// <summary>
/// Unit tests for <see cref="MeshFromTriplaneFunction"/>. Covers metadata,
/// signature validation, body-scope enforcement, and the validation errors
/// that fire before session dispatch (alias type / null triplane). The full
/// happy-path — chunked dispatch against a real triplane + nerf ONNX
/// session — lands in the TripoSR end-to-end test (Slice D), which is the
/// shape the codebase uses for every session-requiring scalar (see
/// <c>DecodeSeq2SeqFunction</c>, which is similarly tested via Florence-2
/// E2E rather than in isolation).
/// </summary>
public sealed class MeshFromTriplaneFunctionTests : ServiceTestBase
{
    [Fact]
    public void Metadata_ExposesNameCategoryDescriptionAndBodyScope()
    {
        Assert.Equal("mesh_from_triplane", MeshFromTriplaneFunction.Name);
        Assert.Equal(FunctionCategory.Image, MeshFromTriplaneFunction.Category);
        Assert.False(string.IsNullOrWhiteSpace(MeshFromTriplaneFunction.Description));
        Assert.Equal(BodyScopeRequirement.ModelBody, MeshFromTriplaneFunction.BodyScope);
        Assert.False(new MeshFromTriplaneFunction().IsPure);
    }

    [Fact]
    public void Validate_AcceptsExpectedSignature_ReturnsMesh()
    {
        DataKind kind = new MeshFromTriplaneFunction().ValidateArguments(
        [
            DataKind.String,    // session_name
            DataKind.Float32,   // triplane (array)
            DataKind.Int32,     // triplane_shape (array)
            DataKind.Int32,     // resolution
            DataKind.Float32,   // isolevel
            DataKind.Float32,   // radius
            DataKind.Int32,     // chunk_size
        ]);
        Assert.Equal(DataKind.Mesh, kind);
    }

    [Fact]
    public async Task Execute_CalledOutsideModelBody_Throws()
    {
        // frame.CurrentModel is null on the bare evaluation frame -- mirrors
        // the runtime path when a UDF outside any CREATE MODEL body somehow
        // invokes this. PlanTimeFunctionGate catches it at plan time too,
        // but this is the defense-in-depth at the function boundary.
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            async () => await new MeshFromTriplaneFunction().ExecuteAsync(
                BuildArgs(sessionName: "nerf"),
                CreateEvaluationFrame(), default));
        Assert.Contains("CREATE MODEL body", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────── Helpers ───────────────────────

    /// <summary>
    /// Builds a dummy 7-argument invocation. The values don't pass full
    /// validation, but every test here exits before reaching them — the
    /// values exist only to give the function a well-typed argument list
    /// to start unpacking.
    /// </summary>
    private static ValueRef[] BuildArgs(string sessionName)
    {
        return
        [
            ValueRef.FromString(sessionName),
            ValueRef.FromPrimitiveArray(new float[1], DataKind.Float32),
            ValueRef.FromPrimitiveArray(new int[1] { 1 }, DataKind.Int32),
            ValueRef.FromInt32(8),
            ValueRef.FromFloat32(0f),
            ValueRef.FromFloat32(1f),
            ValueRef.FromInt32(64),
        ];
    }

}
