using System.Buffers.Binary;
using System.Numerics;

using DatumIngest.Functions.Scalar.Spatial;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Model.Spatial;

namespace DatumIngest.Functions.Aggregates;

/// <summary>
/// <c>pc_fuse_tsdf(depth Float32[], pose Float32[16], intrinsics Float32[9], cell_size Float32, truncation Float32) → Mesh</c>.
/// Truncated Signed-Distance-Function fusion — the principled multi-view
/// reconstruction algorithm pioneered by KinectFusion. Per-frame depth +
/// pose contributions are accumulated into a sparse 3D voxel grid storing
/// signed-distance values and weights; the final surface is extracted at
/// <c>SDF = 0</c> via Marching Cubes.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why TSDF beats voxel consensus for multi-view fusion.</strong>
/// <c>pc_voxel_consensus</c> counts votes per cell — but small pose / depth
/// noise spreads contributions across many adjacent cells, requiring
/// careful <c>min_votes</c> tuning that trades near-surface duplication
/// against far-surface preservation. TSDF stores a CONTINUOUS signed
/// distance instead: noisy observations average toward the true surface
/// position, and free-space carving (negative-SDF votes from frames whose
/// rays passed through a cell) automatically suppresses ghost surfaces.
/// No vote threshold to tune; near and far surfaces are treated uniformly.
/// </para>
/// <para>
/// <strong>Sparse storage.</strong> Voxel cells are held in a
/// <c>Dictionary&lt;long, TsdfVoxel&gt;</c> keyed on packed (i, j, k) cell
/// indices. Only cells touched by at least one frame's depth observation
/// are allocated. State grows with the observed surface area × truncation
/// thickness, not with scene volume — much smaller than a dense grid for
/// typical workloads.
/// </para>
/// <para>
/// <strong>Per-frame cost.</strong> ~<c>(pixels × truncation / cell_size)</c>
/// voxel updates per frame. For 230K pixels at <c>cell_size=0.015</c>,
/// <c>truncation=0.05</c>: ~3M updates per frame. Each is a Dictionary
/// lookup + arithmetic — order of seconds per 100 frames.
/// </para>
/// <para>
/// <strong>Finalize cost.</strong> Converts the sparse grid to a dense
/// <c>resolution³</c> array (default 256, capped at 512), then runs
/// <see cref="MarchingCubesExtractor.Extract"/>. Output mesh is
/// position-only — per-vertex color is a v1 follow-up. Compose with
/// <c>mesh_compute_normals</c> for shading.
/// </para>
/// <para>
/// <strong>Parameters.</strong> <c>cell_size</c> sets voxel resolution
/// (1-3cm typical for indoor handheld); <c>truncation</c> sets how far
/// around each surface to update SDF (3-5× cell_size is a typical
/// rule-of-thumb). Both are read from the first row's value and assumed
/// constant per group.
/// </para>
/// </remarks>
public sealed class PcFuseTsdfFunction : IAggregateFunction
{
    /// <inheritdoc/>
    public string Name => "pc_fuse_tsdf";

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 5)
        {
            throw new ArgumentException(
                "pc_fuse_tsdf() requires exactly 5 arguments: "
                + "(depth Float32[], pose Float32[16], intrinsics Float32[9], "
                + "cell_size Float32, truncation Float32).");
        }
        for (int i = 0; i < 5; i++)
        {
            if (argumentKinds[i] != DataKind.Float32)
            {
                throw new ArgumentException(
                    $"pc_fuse_tsdf() argument {i} must be Float32 (or Float32[]); got {argumentKinds[i]}.");
            }
        }
        return DataKind.Mesh;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Mesh);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Per-voxel state. SDF + weight is the classical TSDF carrier; we
    /// hold them as floats for accuracy across many averaging steps.
    /// 8 bytes per voxel — plus ~24 bytes Dictionary entry overhead.
    /// </summary>
    private struct TsdfVoxel
    {
        public float Sdf;
        public float Weight;
    }

    private sealed class Accumulator : IAggregateAccumulator
    {
        // Sparse SDF grid. Cells allocated lazily as depth observations
        // touch them. Survives in managed memory across the aggregate's
        // lifetime — no arena involvement.
        private readonly Dictionary<long, TsdfVoxel> _voxels = new();

        // Constants from the first row; validated to stay constant on
        // subsequent rows. NaN sentinel means "not yet read".
        private float _cellSize = float.NaN;
        private float _truncation = float.NaN;

        // Coordinate frame of the first non-trivial pose. Output mesh
        // inherits this frame tag so downstream exporters apply the
        // right basis-swap automatically.
        private PointCloudCoordinateFrame _frame = PointCloudCoordinateFrame.CameraOpenGl;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            // Resolve constants from this row. ToArray copies into managed
            // memory so subsequent arena operations can't invalidate the
            // span (the same hazard that bit the confidence variant).
            float[] depthMeters = arguments[0].AsArraySpan<float>(frame.Source, frame.SidecarRegistry).ToArray();
            ReadOnlySpan<int> depthShape = arguments[0].GetShape(frame.Source, frame.SidecarRegistry);
            int height, width;
            if (depthShape.Length >= 2 && AllLeadingOnes(depthShape))
            {
                height = depthShape[^2];
                width = depthShape[^1];
            }
            else
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    "depth must be a shape-aware Float32 array (h, w) or batched "
                    + "(..., h, w); got rank "
                    + $"{depthShape.Length} shape [{string.Join(", ", depthShape.ToArray())}].");
            }
            if (depthMeters.Length != height * width)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"depth array length {depthMeters.Length} doesn't match shape "
                    + $"{height}×{width} = {height * width}.");
            }

            float[] pose = arguments[1].AsArraySpan<float>(frame.Source, frame.SidecarRegistry).ToArray();
            if (pose.Length != 16)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"pose must be 16 Float32 values (4x4 row-major); got {pose.Length}.");
            }

            float[] K = arguments[2].AsArraySpan<float>(frame.Source, frame.SidecarRegistry).ToArray();
            if (K.Length < 9)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"intrinsics must be at least 9 Float32 values (3x3 K matrix); got {K.Length}.");
            }
            int kBase = K.Length - 9;
            float fx = K[kBase + 0], cx = K[kBase + 2];
            float fy = K[kBase + 4], cy = K[kBase + 5];
            if (!(fx > 0f) || !(fy > 0f))
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"intrinsics has non-positive focal lengths: fx={fx}, fy={fy}.");
            }

            float cellSize = ReadFloatScalar(arguments[3], "cell_size");
            float truncation = ReadFloatScalar(arguments[4], "truncation");
            if (!(cellSize > 0f && float.IsFinite(cellSize)))
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf", $"cell_size must be positive finite; got {cellSize}.");
            }
            if (!(truncation > 0f && float.IsFinite(truncation)))
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf", $"truncation must be positive finite; got {truncation}.");
            }
            if (truncation < cellSize)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"truncation ({truncation}) must be at least cell_size ({cellSize}); "
                    + "smaller truncation produces gaps because rays can't reach voxels.");
            }

            // Latch constants on first call; reject inconsistent values on
            // subsequent calls to surface user errors early.
            if (float.IsNaN(_cellSize))
            {
                _cellSize = cellSize;
                _truncation = truncation;
            }
            else if (_cellSize != cellSize || _truncation != truncation)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"cell_size and truncation must be constant across all rows; "
                    + $"saw ({_cellSize}, {_truncation}) then ({cellSize}, {truncation}).");
            }

            IntegrateFrame(
                depthMeters, height, width,
                pose, fx, fy, cx, cy,
                cellSize, truncation);
        }

        /// <summary>
        /// Pixel-centric ray-walk SDF integration. For each pixel with a
        /// valid depth, walk along the camera-to-surface ray from
        /// <c>−truncation</c> to <c>+truncation</c> around the surface
        /// point. At each step, update the corresponding voxel's SDF via
        /// weighted average.
        /// </summary>
        private void IntegrateFrame(
            float[] depth, int height, int width,
            float[] pose, float fx, float fy, float cx, float cy,
            float cellSize, float truncation)
        {
            // Step size = cellSize / 2 — fine enough that adjacent steps
            // land in different voxels (sometimes the same one) but no
            // smaller than necessary. Cell size itself would risk skipping
            // voxels at oblique ray angles.
            float step = cellSize * 0.5f;
            int stepsPerSide = (int)MathF.Ceiling(truncation / step);

            // Camera origin in world frame = pose's translation column.
            Vector3 camOrigin = new(pose[3], pose[7], pose[11]);

            // Pose's rotation block, used to transform camera-frame
            // direction vectors into world frame.
            float m00 = pose[0], m01 = pose[1], m02 = pose[2];
            float m10 = pose[4], m11 = pose[5], m12 = pose[6];
            float m20 = pose[8], m21 = pose[9], m22 = pose[10];

            for (int v = 0; v < height; v++)
            {
                int rowBase = v * width;
                for (int u = 0; u < width; u++)
                {
                    float d = depth[rowBase + u];
                    if (!(d > 0f) || float.IsNaN(d) || float.IsInfinity(d))
                    {
                        continue;
                    }

                    // Camera-frame ray direction through pixel (u, v).
                    // OpenGL convention: +X right, +Y up, −Z forward.
                    // Pixel coords' V axis points down in image but up in
                    // camera-frame Y after the negation, matching the
                    // existing point_cloud_from_depth_* constructors.
                    float dx = (u + 0.5f - cx) / fx;
                    float dy = -(v + 0.5f - cy) / fy;
                    float dz = -1f;

                    // Transform camera-frame direction → world-frame.
                    float wdx = m00 * dx + m01 * dy + m02 * dz;
                    float wdy = m10 * dx + m11 * dy + m12 * dz;
                    float wdz = m20 * dx + m21 * dy + m22 * dz;
                    float dirLen = MathF.Sqrt(wdx * wdx + wdy * wdy + wdz * wdz);
                    if (!(dirLen > 0f)) continue;
                    wdx /= dirLen; wdy /= dirLen; wdz /= dirLen;

                    // Surface world position = camera origin + ray_dir × d.
                    // (Depth d is along camera's −Z, which we've baked into
                    // ray direction's sign, so the surface is along +ray_dir
                    // at distance d.)
                    float surfX = camOrigin.X + wdx * d;
                    float surfY = camOrigin.Y + wdy * d;
                    float surfZ = camOrigin.Z + wdz * d;

                    // Walk ±truncation around the surface. Step `s` is the
                    // signed offset from the surface along the ray. SDF at
                    // position (surface + s × dir) is `−s` — positive in
                    // front of surface (toward camera = free space),
                    // negative behind surface.
                    for (int n = -stepsPerSide; n <= stepsPerSide; n++)
                    {
                        float s = n * step;
                        float px = surfX + wdx * s;
                        float py = surfY + wdy * s;
                        float pz = surfZ + wdz * s;

                        int ci = (int)MathF.Floor(px / cellSize);
                        int cj = (int)MathF.Floor(py / cellSize);
                        int ck = (int)MathF.Floor(pz / cellSize);
                        long key = PackCellKey(ci, cj, ck);

                        float sdf = -s;
                        // Clamp explicitly even though step magnitude ≤ truncation,
                        // because rounding can nudge it slightly outside.
                        if (sdf > truncation) sdf = truncation;
                        if (sdf < -truncation) sdf = -truncation;

                        ref TsdfVoxel cell =
                            ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_voxels, key, out _);
                        // Uniform weight 1 per pixel observation. Weighted
                        // running average preserves accuracy across many
                        // frames seeing the same voxel.
                        float newWeight = cell.Weight + 1f;
                        cell.Sdf = (cell.Weight * cell.Sdf + sdf) / newWeight;
                        cell.Weight = newWeight;
                    }
                }
            }
        }

        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            if (float.IsNaN(_cellSize) && !float.IsNaN(o._cellSize))
            {
                _cellSize = o._cellSize;
                _truncation = o._truncation;
            }
            else if (!float.IsNaN(_cellSize) && !float.IsNaN(o._cellSize))
            {
                if (_cellSize != o._cellSize || _truncation != o._truncation)
                {
                    throw new FunctionArgumentException(
                        "pc_fuse_tsdf",
                        $"cell_size/truncation disagree across merge: "
                        + $"({_cellSize}, {_truncation}) vs ({o._cellSize}, {o._truncation}).");
                }
            }

            foreach (KeyValuePair<long, TsdfVoxel> kv in o._voxels)
            {
                ref TsdfVoxel cell =
                    ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(_voxels, kv.Key, out _);
                float newWeight = cell.Weight + kv.Value.Weight;
                if (newWeight > 0f)
                {
                    cell.Sdf = (cell.Weight * cell.Sdf + kv.Value.Weight * kv.Value.Sdf) / newWeight;
                    cell.Weight = newWeight;
                }
            }
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            // Empty aggregation → emit an empty Mesh.
            if (_voxels.Count == 0)
            {
                MeshHeader empty = new(
                    Version: MeshHeader.CurrentVersion,
                    Flags: MeshFlags.None,
                    CoordinateFrame: _frame,
                    VertexCount: 0,
                    TriangleCount: 0,
                    BboxMin: Vector3.Zero,
                    BboxMax: Vector3.Zero,
                    TextureOffset: 0,
                    TextureLength: 0);
                byte[] emptyBlob = new byte[MeshHeader.SizeBytes];
                empty.Write(emptyBlob);
                return new ValueTask<DataValue>(DataValue.FromMesh(emptyBlob, frame.Target));
            }

            // Bounding box over occupied voxels' cell centers, in world
            // coords. Add a buffer of truncation/cellSize cells so the
            // dense grid contains all voxels even at extents.
            int iMin = int.MaxValue, jMin = int.MaxValue, kMin = int.MaxValue;
            int iMax = int.MinValue, jMax = int.MinValue, kMax = int.MinValue;
            foreach (long key in _voxels.Keys)
            {
                UnpackCellKey(key, out int ci, out int cj, out int ck);
                if (ci < iMin) iMin = ci;
                if (cj < jMin) jMin = cj;
                if (ck < kMin) kMin = ck;
                if (ci > iMax) iMax = ci;
                if (cj > jMax) jMax = cj;
                if (ck > kMax) kMax = ck;
            }

            // Dense-grid layout that fits the bbox. Make the grid CUBIC
            // (MC requires this) by padding to the largest axis. Cap at
            // 512 to bound memory; raising the cell_size is the right
            // response to "scene too big".
            int extentI = iMax - iMin + 1;
            int extentJ = jMax - jMin + 1;
            int extentK = kMax - kMin + 1;
            int resolution = Math.Max(extentI, Math.Max(extentJ, extentK)) + 4;   // small buffer
            const int MaxResolution = 512;
            if (resolution > MaxResolution)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"required resolution {resolution} exceeds cap of {MaxResolution}. "
                    + $"Bounding-box span is {extentI}×{extentJ}×{extentK} cells. "
                    + $"Increase cell_size (currently {_cellSize}) to reduce resolution.");
            }

            // Centered offset so the actual content sits inside the padded
            // cubic grid with the buffer split between min and max sides.
            int padI = (resolution - extentI) / 2;
            int padJ = (resolution - extentJ) / 2;
            int padK = (resolution - extentK) / 2;
            int gridOriginI = iMin - padI;
            int gridOriginJ = jMin - padJ;
            int gridOriginK = kMin - padK;

            // Allocate the dense density grid. Initialize to +truncation
            // (free space), so unobserved cells don't trigger MC zero-
            // crossings. NB: MC convention is "density > isolevel = inside",
            // so we feed −SDF (the negation flips the inequality so SDF<0
            // ⇒ −SDF>0 ⇒ inside).
            long totalCells = (long)resolution * resolution * resolution;
            if (totalCells > int.MaxValue)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"dense grid {resolution}³ = {totalCells} cells exceeds Int32 cap.");
            }
            float[] density = new float[totalCells];
            // Free space = positive SDF → negative density. Use the negative-
            // truncation value so MC treats it as solidly outside.
            float emptyValue = -_truncation;
            Array.Fill(density, emptyValue);

            // Fill in observed voxels. Skip cells with weight = 0 (treated
            // as unobserved — these shouldn't occur with GetValueRefOrAddDefault
            // but defensive.)
            foreach (KeyValuePair<long, TsdfVoxel> kv in _voxels)
            {
                if (kv.Value.Weight <= 0f) continue;
                UnpackCellKey(kv.Key, out int ci, out int cj, out int ck);
                int gi = ci - gridOriginI;
                int gj = cj - gridOriginJ;
                int gk = ck - gridOriginK;
                if ((uint)gi >= (uint)resolution || (uint)gj >= (uint)resolution || (uint)gk >= (uint)resolution)
                {
                    // Shouldn't happen with correct bbox calculation; skip
                    // defensively rather than throwing during finalize.
                    continue;
                }
                int idx = gi + gj * resolution + gk * resolution * resolution;
                density[idx] = -kv.Value.Sdf;
            }

            // Extract the iso-surface. radius = (resolution−1) × cellSize / 2
            // so MC's [-radius, +radius] cubic span maps one grid cell to
            // one cellSize-unit step in world coords.
            float radius = (resolution - 1) * _cellSize * 0.5f;
            MarchingCubesResult mc = MarchingCubesExtractor.Extract(
                density, resolution, isolevel: 0f, radius: radius);

            // MC vertices are in grid-local coords centered on the origin.
            // Translate back to world coords: add the world-position of the
            // grid's center.
            float gridCenterX = (gridOriginI + (resolution - 1) * 0.5f) * _cellSize;
            float gridCenterY = (gridOriginJ + (resolution - 1) * 0.5f) * _cellSize;
            float gridCenterZ = (gridOriginK + (resolution - 1) * 0.5f) * _cellSize;
            Vector3 gridCenter = new(gridCenterX, gridCenterY, gridCenterZ);

            float[] worldPositions = new float[mc.Positions.Length];
            Vector3 bboxMin = new(float.PositiveInfinity);
            Vector3 bboxMax = new(float.NegativeInfinity);
            for (int v = 0; v < mc.VertexCount; v++)
            {
                float wx = mc.Positions[v * 3 + 0] + gridCenter.X;
                float wy = mc.Positions[v * 3 + 1] + gridCenter.Y;
                float wz = mc.Positions[v * 3 + 2] + gridCenter.Z;
                worldPositions[v * 3 + 0] = wx;
                worldPositions[v * 3 + 1] = wy;
                worldPositions[v * 3 + 2] = wz;
                Vector3 pp = new(wx, wy, wz);
                bboxMin = Vector3.Min(bboxMin, pp);
                bboxMax = Vector3.Max(bboxMax, pp);
            }

            // Emit position-only mesh blob via the shared helper.
            MarchingCubesResult worldMc = new(worldPositions, mc.Indices, bboxMin, bboxMax);
            byte[] meshBlob = MeshBlobBuilder.PositionOnly(worldMc, _frame);
            return new ValueTask<DataValue>(DataValue.FromMesh(meshBlob, frame.Target));
        }

        public void Reset()
        {
            _voxels.Clear();
            _cellSize = float.NaN;
            _truncation = float.NaN;
        }

        // ─────────── Cell-key packing (same as pc_voxel_consensus) ───────────

        private static long PackCellKey(int i, int j, int k)
        {
            const int MaxComponent = 1 << 20;
            if (i <= -MaxComponent || i >= MaxComponent ||
                j <= -MaxComponent || j >= MaxComponent ||
                k <= -MaxComponent || k >= MaxComponent)
            {
                throw new FunctionArgumentException(
                    "pc_fuse_tsdf",
                    $"voxel cell index out of range: ({i}, {j}, {k}). "
                    + "Increase cell_size or constrain the scene to a smaller volume.");
            }
            long ui = (long)(uint)(i + MaxComponent) & 0x1F_FFFFL;
            long uj = (long)(uint)(j + MaxComponent) & 0x1F_FFFFL;
            long uk = (long)(uint)(k + MaxComponent) & 0x1F_FFFFL;
            return (ui << 42) | (uj << 21) | uk;
        }

        private static void UnpackCellKey(long key, out int i, out int j, out int k)
        {
            const int MaxComponent = 1 << 20;
            long mask = 0x1F_FFFFL;
            int ui = (int)((key >> 42) & mask);
            int uj = (int)((key >> 21) & mask);
            int uk = (int)(key & mask);
            i = ui - MaxComponent;
            j = uj - MaxComponent;
            k = uk - MaxComponent;
        }

        // ─────────── Helpers ───────────

        private static bool AllLeadingOnes(ReadOnlySpan<int> shape)
        {
            for (int i = 0; i < shape.Length - 2; i++)
            {
                if (shape[i] != 1) return false;
            }
            return true;
        }

        private static float ReadFloatScalar(DataValue value, string name)
        {
            if (value.TryToFloat(out float result)) return result;
            throw new FunctionArgumentException(
                "pc_fuse_tsdf",
                $"{name} could not be widened to Float32 (kind {value.Kind}).");
        }
    }
}
