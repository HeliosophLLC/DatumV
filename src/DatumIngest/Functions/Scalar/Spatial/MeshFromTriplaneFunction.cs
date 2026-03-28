using DatumIngest.Execution;
using DatumIngest.Functions.Scalar.Activation;
using DatumIngest.Inference;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Scalar.Spatial;

/// <summary>
/// <c>mesh_from_triplane(session_name String, triplane Float32[], triplane_shape Int32[], resolution Int32, isolevel Float32, radius Float32, chunk_size Int32) → Mesh</c>.
/// Orchestrates iso-surface mesh extraction from a triplane neural field —
/// the canonical TripoSR / SF3D / InstantMesh inference shape. Calls a bound
/// "NeRF MLP" sub-session in chunks across a <c>resolution³</c> voxel grid
/// to build a density field, runs Marching Cubes, and re-samples colors at
/// the extracted vertex positions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Session contract.</strong> The session named by the
/// <c>session_name</c> argument (an alias bound via the model's
/// <c>USING 'file.onnx' AS alias</c> clause) MUST expose:
/// <list type="bullet">
///   <item>Input <c>triplane</c> Float32 — the upstream triplane feature
///         tensor, passed every call.</item>
///   <item>Input <c>xyz</c> Float32 of shape <c>[K, 3]</c> — a chunk of
///         query points in world space; <c>K</c> varies per call.</item>
///   <item>Output <c>density</c> (or <c>density_act</c>) Float32 — the
///         activated density at each <c>xyz</c>, taken as the iso-surface
///         field for Marching Cubes.</item>
///   <item>Output <c>color</c> (or <c>rgb</c>) Float32 of shape
///         <c>[K, 3]</c> — the RGB value at each <c>xyz</c> in
///         <c>[0, 1]</c>, sampled at extracted vertex positions in a
///         second pass and quantized to RGBA8 on the mesh.</item>
/// </list>
/// TripoSR's <c>nerf.onnx</c> (the export emitted by
/// <c>scripts/export-triposr.ps1</c>) matches this contract directly.
/// </para>
/// <para>
/// <strong>Spatial mapping.</strong> The query grid is the cube
/// <c>[-radius, +radius]³</c> sampled at <c>resolution</c> points per axis,
/// indexed with X varying fastest (then Y, then Z). Matches
/// <c>mesh_from_density_grid</c>'s convention, so its iso-surface result is
/// the same as if you called the model yourself, packed the density into a
/// flat array, and invoked <c>mesh_from_density_grid</c> on it — this
/// function exists to spare the SQL body from spelling out the chunked
/// dispatch loop.
/// </para>
/// <para>
/// <strong>Output mesh.</strong> Position + per-vertex RGBA8 color (no
/// normals; compose with <c>mesh_compute_normals</c> for smooth shading).
/// Coordinate frame is <see cref="PointCloudCoordinateFrame.CameraOpenGl"/>
/// (right-handed +Y up). When the iso-surface has no crossings the result
/// is a zero-vertex zero-triangle position-only Mesh — degenerate but
/// well-formed, so downstream exporters never see a null.
/// </para>
/// </remarks>
public sealed class MeshFromTriplaneFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "mesh_from_triplane";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Image;

    /// <inheritdoc />
    public static string Description =>
        "Orchestrates iso-surface mesh extraction from a triplane neural field. "
        + "Calls the named NeRF-MLP sub-session in chunks across a resolution³ voxel "
        + "grid spanning [-radius, +radius]³, accumulates densities, runs Marching "
        + "Cubes at the supplied isolevel, then re-samples colors at the extracted "
        + "vertex positions. Designed for TripoSR-shape architectures (triplane + small "
        + "MLP); the session must declare 'triplane' + 'xyz' inputs and 'density' "
        + "(or 'density_act') + 'color' (or 'rgb') outputs.";

    /// <inheritdoc />
    public static BodyScopeRequirement BodyScope => BodyScopeRequirement.ModelBody;

    /// <inheritdoc />
    public bool IsPure => false;

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("session_name",   DataKindMatcher.Exact(DataKind.String),  IsArray: ArrayMatch.Scalar),
                new ParameterSpec("triplane",       DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("triplane_shape", DataKindMatcher.Family(DataKindFamily.IntegerFamily), IsArray: ArrayMatch.Array),
                new ParameterSpec("resolution",     DataKindMatcher.Exact(DataKind.Int32),    IsArray: ArrayMatch.Scalar),
                new ParameterSpec("isolevel",       DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("radius",         DataKindMatcher.Family(DataKindFamily.NumericScalar)),
                new ParameterSpec("chunk_size",     DataKindMatcher.Exact(DataKind.Int32),    IsArray: ArrayMatch.Scalar),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Mesh)),
    ];

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<MeshFromTriplaneFunction>(argumentKinds);

    /// <inheritdoc />
    public async ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        if (frame.CurrentModel is not { } model)
        {
            throw new FunctionArgumentException(Name,
                "is only callable from inside a CREATE MODEL body. Outside a model "
                + "frame there is no bound session to dispatch to.");
        }

        // Phase 1: alias extraction + session resolution. Spans can't cross
        // awaits, so we peek the alias here, resolve the session, then
        // re-acquire the span in phase 2 for the typed unpacking.
        string sessionAlias;
        {
            ReadOnlySpan<ValueRef> probe = arguments.Span;
            if (probe[0].IsNull || probe[0].Kind != DataKind.String)
            {
                throw new FunctionArgumentException(Name,
                    "session_name must be a non-NULL String alias bound by the model's USING clause.");
            }
            sessionAlias = probe[0].AsString();
        }

        if (!model.BoundSessions.ContainsKey(sessionAlias))
        {
            throw new FunctionArgumentException(Name,
                $"session alias '{sessionAlias}' is not bound. Available aliases: "
                + $"[{string.Join(", ", model.BoundSessions.Keys)}]. Aliases come from "
                + "the CREATE MODEL's USING clause (`USING 'path' AS alias`).");
        }
        IInferenceSession session = await model.BoundSessions
            .ResolveAsync(sessionAlias, cancellationToken).ConfigureAwait(false);

        // Resolve the input / output names. 'triplane' and 'xyz' inputs are
        // strict; output is more lenient — TripoSR's export uses
        // 'density'/'color', other triplane models may use 'density_act'/'rgb'.
        TensorSpec? triplaneInput = FindInput(session, "triplane");
        TensorSpec? xyzInput = FindInput(session, "xyz");
        if (triplaneInput is null || xyzInput is null)
        {
            string have = string.Join(", ", session.Inputs.Select(s => s.Name));
            throw new FunctionArgumentException(Name,
                $"session '{sessionAlias}' must declare 'triplane' and 'xyz' inputs; "
                + $"found [{have}]. Re-export the model with these input names, or use "
                + "infer() directly if your model uses different names.");
        }

        TensorSpec? densityOutput = FindOutput(session, "density")
            ?? FindOutput(session, "density_act");
        TensorSpec? colorOutput = FindOutput(session, "color")
            ?? FindOutput(session, "rgb");
        if (densityOutput is null || colorOutput is null)
        {
            string have = string.Join(", ", session.Outputs.Select(s => s.Name));
            throw new FunctionArgumentException(Name,
                $"session '{sessionAlias}' must declare a density output ('density' or "
                + $"'density_act') and a color output ('color' or 'rgb'); found [{have}].");
        }

        // Phase 2: typed unpacking of the rest of the arguments.
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args[1].IsNull)
        {
            return ValueRef.Null(DataKind.Mesh);
        }
        float[] triplane = ActivationOps.ReadFloat32Array(args[1]);
        int[] triplaneShape = ReadInt32ShapeArray(args[2]);
        int resolution = args[3].AsInt32();
        float isolevel = args[4].AsFloat32();
        float radius = args[5].AsFloat32();
        int chunkSize = args[6].AsInt32();

        if (resolution < 2)
        {
            throw new FunctionArgumentException(Name,
                "resolution must be >= 2 (need at least a 2×2×2 grid to form one Marching "
                + $"Cubes cube); got {resolution}.");
        }
        if (!float.IsFinite(isolevel))
        {
            throw new FunctionArgumentException(Name, $"isolevel must be finite; got {isolevel}.");
        }
        if (!float.IsFinite(radius) || radius <= 0f)
        {
            throw new FunctionArgumentException(Name,
                $"radius must be a positive finite value; got {radius}.");
        }
        if (chunkSize <= 0)
        {
            throw new FunctionArgumentException(Name,
                $"chunk_size must be positive; got {chunkSize}.");
        }

        // Validate the triplane shape product against the supplied flat array
        // length. Catches the easy misalignment of "wrong shape passed for
        // this triplane tensor" up front instead of letting it surface as a
        // confusing ORT error inside the first dispatch.
        long triplaneProduct = 1;
        for (int i = 0; i < triplaneShape.Length; i++)
        {
            if (triplaneShape[i] <= 0)
            {
                throw new FunctionArgumentException(Name,
                    "triplane_shape dimensions must all be positive; got "
                    + $"[{string.Join(", ", triplaneShape)}].");
            }
            triplaneProduct *= triplaneShape[i];
        }
        if (triplaneProduct != triplane.Length)
        {
            throw new FunctionArgumentException(Name,
                $"triplane_shape product {triplaneProduct} (from [{string.Join(", ", triplaneShape)}]) "
                + $"does not match the triplane array length {triplane.Length}.");
        }

        // ---- Pass 1: build the density field over the res³ grid. ----
        long totalPoints = (long)resolution * resolution * resolution;
        if (totalPoints > int.MaxValue)
        {
            throw new FunctionArgumentException(Name,
                $"resolution³ ({totalPoints}) exceeds Int32.MaxValue; pick a smaller resolution.");
        }
        int total = (int)totalPoints;
        float[] density = new float[total];
        float step = (2f * radius) / (resolution - 1);

        for (int offset = 0; offset < total; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int k = System.Math.Min(chunkSize, total - offset);
            float[] chunkXyz = BuildGridChunk(offset, k, resolution, radius, step);
            await DispatchAndCopyAsync(
                session, triplaneInput, xyzInput, triplane, triplaneShape,
                chunkXyz, k,
                outputName: densityOutput.Name,
                destination: density.AsMemory(offset, k),
                channelsPerPoint: 1,
                cancellationToken).ConfigureAwait(false);
        }

        // ---- Pass 2: Marching Cubes on the density grid. ----
        MarchingCubesResult mc = MarchingCubesExtractor.Extract(density, resolution, isolevel, radius);

        if (mc.VertexCount == 0)
        {
            // Iso-surface didn't cross any cube — return a well-formed empty
            // mesh rather than null, so exporters / viewers see a 0-vertex
            // Mesh instead of a missing value.
            byte[] empty = MeshBlobBuilder.PositionOnly(mc, PointCloudCoordinateFrame.CameraOpenGl);
            return ValueRef.FromMesh(empty);
        }

        // ---- Pass 3: sample colors at the extracted vertex positions. ----
        int vertexCount = mc.VertexCount;
        float[] colors = new float[vertexCount * 3];
        for (int offset = 0; offset < vertexCount; offset += chunkSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int k = System.Math.Min(chunkSize, vertexCount - offset);
            float[] chunkXyz = new float[k * 3];
            Array.Copy(mc.Positions, offset * 3, chunkXyz, 0, k * 3);
            await DispatchAndCopyAsync(
                session, triplaneInput, xyzInput, triplane, triplaneShape,
                chunkXyz, k,
                outputName: colorOutput.Name,
                destination: colors.AsMemory(offset * 3, k * 3),
                channelsPerPoint: 3,
                cancellationToken).ConfigureAwait(false);
        }

        byte[] blob = MeshBlobBuilder.PositionPlusColor(
            mc.Positions, colors, mc.Indices, mc.BboxMin, mc.BboxMax,
            PointCloudCoordinateFrame.CameraOpenGl);
        return ValueRef.FromMesh(blob);
    }

    /// <summary>
    /// Builds one chunk of the xyz grid in row-major X-fastest order. Linear
    /// index <c>i</c> maps to world position
    /// <c>(-radius + step·(i mod res), -radius + step·((i/res) mod res), -radius + step·(i/res²))</c>,
    /// matching <see cref="MarchingCubesExtractor"/>'s density-array indexing
    /// so the per-cell density read in Pass 2 lines up with the per-cell
    /// dispatch in Pass 1.
    /// </summary>
    private static float[] BuildGridChunk(int offset, int count, int resolution, float radius, float step)
    {
        float[] chunk = new float[count * 3];
        int res = resolution;
        int resSq = res * res;
        for (int local = 0; local < count; local++)
        {
            int linear = offset + local;
            int xi = linear % res;
            int yi = (linear / res) % res;
            int zi = linear / resSq;
            chunk[local * 3 + 0] = -radius + step * xi;
            chunk[local * 3 + 1] = -radius + step * yi;
            chunk[local * 3 + 2] = -radius + step * zi;
        }
        return chunk;
    }

    /// <summary>
    /// One session dispatch: wires the triplane (re-uploaded every call —
    /// IO-binding pin is a future optimization) and the xyz chunk as inputs,
    /// runs the session, copies <paramref name="channelsPerPoint"/>·k floats
    /// from the named output tensor into <paramref name="destination"/>, and
    /// disposes both bags.
    /// </summary>
    private static async ValueTask DispatchAndCopyAsync(
        IInferenceSession session,
        TensorSpec triplaneInput,
        TensorSpec xyzInput,
        float[] triplane,
        int[] triplaneShape,
        float[] chunkXyz,
        int k,
        string outputName,
        Memory<float> destination,
        int channelsPerPoint,
        CancellationToken cancellationToken)
    {
        int[] xyzShape = [k, 3];
        TensorBag inputBag = session.CreateInputBag();
        TensorBag? outputBag = null;
        try
        {
            inputBag.Add<float>(triplaneInput.Name, DataKind.Float32, triplaneShape, triplane);
            inputBag.Add<float>(xyzInput.Name, DataKind.Float32, xyzShape, chunkXyz);
            outputBag = await session.RunAsync(inputBag, cancellationToken).ConfigureAwait(false);

            if (!outputBag.TryGet(outputName, out IInferenceTensor tensor))
            {
                throw new FunctionArgumentException(Name,
                    $"session did not produce expected output '{outputName}' during dispatch "
                    + $"of a {k}-point chunk.");
            }
            ReadOnlySpan<float> tensorSpan = tensor.AsSpan<float>();
            int expected = k * channelsPerPoint;
            if (tensorSpan.Length < expected)
            {
                throw new FunctionArgumentException(Name,
                    $"session output '{outputName}' returned {tensorSpan.Length} floats; "
                    + $"expected at least {expected} ({channelsPerPoint} channel(s) × {k} points). "
                    + "Check that the output shape matches the documented [K, *] convention.");
            }
            tensorSpan[..expected].CopyTo(destination.Span);
        }
        finally
        {
            inputBag.Dispose();
            outputBag?.Dispose();
        }
    }

    private static TensorSpec? FindInput(IInferenceSession session, string name)
    {
        foreach (TensorSpec s in session.Inputs)
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
        }
        return null;
    }

    private static TensorSpec? FindOutput(IInferenceSession session, string name)
    {
        foreach (TensorSpec s in session.Outputs)
        {
            if (string.Equals(s.Name, name, StringComparison.Ordinal)) return s;
        }
        return null;
    }

    /// <summary>
    /// Pulls an Int32 / Int64 shape array out of <paramref name="arg"/> and
    /// returns it as <c>int[]</c>. Mirrors <c>InferFunction.ReadShapeArray</c>;
    /// kept local here so this function's dispatch path doesn't reach into
    /// <c>InferFunction</c>'s privates.
    /// </summary>
    private static int[] ReadInt32ShapeArray(ValueRef arg)
    {
        if (arg.IsNull)
        {
            throw new FunctionArgumentException(Name, "triplane_shape must not be NULL.");
        }
        if (!arg.IsArray)
        {
            throw new FunctionArgumentException(Name,
                $"triplane_shape must be an integer array; got {arg.Kind}.");
        }
        if (arg.Materialized is int[] direct) return direct;
        if (arg.Materialized is long[] longs)
        {
            int[] copied = new int[longs.Length];
            for (int i = 0; i < longs.Length; i++) copied[i] = checked((int)longs[i]);
            return copied;
        }
        ReadOnlySpan<ValueRef> elements = arg.GetArrayElements();
        int[] result = new int[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            if (!elements[i].TryToInt32(out int v))
            {
                throw new FunctionArgumentException(Name,
                    $"triplane_shape element [{i}] ({elements[i].Kind}) is not coercible to Int32.");
            }
            result[i] = v;
        }
        return result;
    }
}
