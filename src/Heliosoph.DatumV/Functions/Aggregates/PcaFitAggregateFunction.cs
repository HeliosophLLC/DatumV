using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>pca_fit_agg(vec FLOAT32[], k) → Struct{mean FLOAT32[d], components FLOAT32[k,d],
/// variance_ratio FLOAT32[k]}</c> — fits a k-component PCA model over the group's
/// vectors. The result travels as a single struct value so the centering mean is
/// never separated from the basis; feed it to <c>pca_project(model, vec)</c> to
/// map vectors into the fitted k-dimensional space.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Memory shape.</strong> The accumulator is O(d²), not O(n·d): it keeps a
/// running element sum and a d×d outer-product sum (upper triangle, doubles) and
/// never retains the input vectors. Dimensions are capped at
/// <see cref="MaxDimensions"/> so a mistyped column can't silently allocate
/// gigabytes of covariance state.
/// </para>
/// <para>
/// <strong>Merge.</strong> Element sums and outer-product sums combine exactly, so
/// parallel hash-aggregate merging is supported (unlike order-sensitive
/// blob-concatenating aggregates).
/// </para>
/// <para>
/// <strong>Determinism.</strong> Eigenvectors are computed by power iteration with
/// deflation from a deterministic start vector, and each component's sign is
/// pinned so its largest-magnitude entry (first on ties) is positive — the same
/// group always yields the same model, which downstream rendering relies on.
/// </para>
/// <para>
/// <strong>Semantics.</strong> Null vectors are skipped. The first non-null vector
/// pins d; a later mismatch throws. <c>k</c> is captured from the first row and
/// must satisfy 1 ≤ k ≤ d. Groups with zero non-null vectors return NULL; a group
/// with exactly one vector throws (covariance is undefined). When k exceeds the
/// covariance rank the trailing components complete an orthonormal basis with
/// ~zero variance_ratio. variance_ratio is each eigenvalue's share of total
/// variance; for a zero-variance group (all vectors identical) the components fall
/// back to the leading canonical axes with all ratios zero.
/// </para>
/// </remarks>
public sealed class PcaFitAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    /// <summary>
    /// Upper bound on input dimensionality. The accumulator's outer-product state
    /// is d(d+1)/2 doubles — 4096 dims is ~67 MB per group, the point past which
    /// the allocation should be a deliberate choice rather than a side effect.
    /// </summary>
    internal const int MaxDimensions = 4096;

    private const int MaxPowerIterations = 1000;

    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "pca_fit_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Fits a k-component PCA model over the group's Float32 vectors: "
        + "pca_fit_agg(vec FLOAT32[], k) → Struct{mean FLOAT32[d], components FLOAT32[k,d], variance_ratio FLOAT32[k]}. "
        + "Project vectors with pca_project(model, vec).";

    /// <inheritdoc/>
    public static IReadOnlyList<FunctionSignatureVariant> Signatures { get; } =
    [
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("vec", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("k", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
        {
            throw new ArgumentException(
                "pca_fit_agg() requires exactly two arguments: vector and component count k.");
        }
        if (argumentKinds[0] != DataKind.Float32)
        {
            throw new ArgumentException(
                $"pca_fit_agg() first argument must be FLOAT32[], got {argumentKinds[0]}.");
        }
        if (!DataKindFamily.IntegerFamily.Contains(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"pca_fit_agg() second argument (k) must be an integer, got {argumentKinds[1]}.");
        }
        return DataKind.Struct;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Struct);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Streaming moment accumulator: element sums plus the upper triangle of the
    /// d×d outer-product sum, all in doubles. Covariance, eigenvectors, and the
    /// result struct are built once at <see cref="ResultAsync"/>.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        private int _d = -1;
        private int _k = -1;
        private long _count;
        private double[] _sum = [];
        // Upper triangle of Σ vᵢvⱼ, row-major: entry (i, j≥i) at TriangleIndex(i, j).
        private double[] _outer = [];

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (!arguments[1].IsNull)
            {
                CaptureK(arguments[1]);
            }

            if (arguments[0].IsNull)
            {
                return;
            }

            ReadOnlySpan<float> vec = arguments[0].AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
            if (_d < 0)
            {
                InitializeDimensions(vec.Length);
            }
            else if (vec.Length != _d)
            {
                throw new FunctionArgumentException(Name,
                    $"expected a {_d}-dimensional vector (pinned by the group's first vector), got {vec.Length} dimensions.");
            }

            _count++;
            for (int i = 0; i < _d; i++)
            {
                double vi = vec[i];
                _sum[i] += vi;
                int rowBase = TriangleIndex(i, i);
                for (int j = i; j < _d; j++)
                {
                    _outer[rowBase + (j - i)] += vi * vec[j];
                }
            }
        }

        private void CaptureK(in DataValue kArg)
        {
            if (!kArg.TryToFloat(out float kFloat))
            {
                throw new FunctionArgumentException(Name,
                    $"could not read component count k from a {kArg.Kind} value.");
            }
            int k = (int)kFloat;
            if (k < 1)
            {
                throw new FunctionArgumentException(Name,
                    $"component count k must be at least 1, got {k}.");
            }
            if (_k < 0)
            {
                _k = k;
                ValidateKAgainstD();
            }
            else if (k != _k)
            {
                throw new FunctionArgumentException(Name,
                    $"component count k must be constant across the group; saw {_k} and {k}.");
            }
        }

        private void InitializeDimensions(int d)
        {
            if (d < 1)
            {
                throw new FunctionArgumentException(Name, "input vectors must have at least one element.");
            }
            if (d > MaxDimensions)
            {
                throw new FunctionArgumentException(Name,
                    $"input vectors have {d} dimensions; pca_fit_agg supports at most {MaxDimensions}.");
            }
            _d = d;
            _sum = new double[d];
            _outer = new double[d * (d + 1) / 2];
            ValidateKAgainstD();
        }

        private void ValidateKAgainstD()
        {
            if (_k > 0 && _d > 0 && _k > _d)
            {
                throw new FunctionArgumentException(Name,
                    $"component count k = {_k} exceeds the input dimensionality d = {_d}.");
            }
        }

        /// <summary>Index of (i, j≥i) in the packed upper triangle.</summary>
        private int TriangleIndex(int i, int j) => i * _d - i * (i - 1) / 2 + (j - i);

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            if (o._k >= 0)
            {
                if (_k >= 0 && _k != o._k)
                {
                    throw new FunctionArgumentException(Name,
                        $"component count k must be constant across the group; saw {_k} and {o._k}.");
                }
                _k = o._k;
            }
            if (o._d < 0)
            {
                return ValueTask.CompletedTask;
            }
            if (_d < 0)
            {
                _d = o._d;
                _sum = o._sum;
                _outer = o._outer;
                _count = o._count;
                ValidateKAgainstD();
                return ValueTask.CompletedTask;
            }
            if (_d != o._d)
            {
                throw new FunctionArgumentException(Name,
                    $"cannot merge groups with different vector dimensionality: {_d} vs {o._d}.");
            }
            _count += o._count;
            for (int i = 0; i < _sum.Length; i++) _sum[i] += o._sum[i];
            for (int i = 0; i < _outer.Length; i++) _outer[i] += o._outer[i];
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_count == 0)
            {
                return new(DataValue.NullStruct(TypeRegistry.NoType));
            }
            if (_count == 1)
            {
                throw new FunctionArgumentException(Name,
                    "requires at least 2 non-null vectors per group; covariance of a single vector is undefined.");
            }
            int k = _k > 0 ? _k : 1;
            int d = _d;
            long n = _count;

            double[] mean = new double[d];
            for (int i = 0; i < d; i++) mean[i] = _sum[i] / n;

            // Sample covariance from the accumulated moments: Σvvᵀ/(n−1) − n·μμᵀ/(n−1).
            double[] cov = new double[d * d];
            double denominator = n - 1;
            for (int i = 0; i < d; i++)
            {
                for (int j = i; j < d; j++)
                {
                    double c = (_outer[TriangleIndex(i, j)] - n * mean[i] * mean[j]) / denominator;
                    cov[i * d + j] = c;
                    cov[j * d + i] = c;
                }
            }

            double totalVariance = 0;
            for (int i = 0; i < d; i++) totalVariance += cov[i * d + i];

            var (components, eigenvalues) = TopKEigen(cov, d, k);

            float[] meanOut = new float[d];
            for (int i = 0; i < d; i++) meanOut[i] = (float)mean[i];

            float[] componentsOut = new float[k * d];
            for (int i = 0; i < componentsOut.Length; i++) componentsOut[i] = (float)components[i];

            float[] ratioOut = new float[k];
            for (int c = 0; c < k; c++)
            {
                ratioOut[c] = totalVariance > 0
                    ? (float)(System.Math.Max(0, eigenvalues[c]) / totalVariance)
                    : 0f;
            }

            ushort typeId = TypeRegistry.NoType;
            if (frame.Types is { } types)
            {
                int float32ArrayTypeId = types.InternArrayType(DataKind.Float32);
                typeId = (ushort)types.InternStructType(
                [
                    new StructFieldDescriptor("mean", float32ArrayTypeId),
                    new StructFieldDescriptor("components", float32ArrayTypeId),
                    new StructFieldDescriptor("variance_ratio", float32ArrayTypeId),
                ]);
            }

            DataValue[] fields =
            [
                DataValue.FromArenaArray<float>(meanOut, DataKind.Float32, frame.Target),
                DataValue.FromArenaMultiDimArray<float>(componentsOut, [k, d], DataKind.Float32, frame.Target),
                DataValue.FromArenaArray<float>(ratioOut, DataKind.Float32, frame.Target),
            ];
            return new(DataValue.FromStruct(fields, frame.Target, typeId));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _d = -1;
            _k = -1;
            _count = 0;
            _sum = [];
            _outer = [];
        }

        /// <summary>
        /// Top-k eigenpairs of a symmetric PSD matrix by power iteration with
        /// deflation. Deterministic: each component starts from the canonical
        /// axis with the largest deflated diagonal, and the returned vector's
        /// largest-magnitude entry (first on ties) is made positive. When the
        /// deflated matrix has no energy left (k exceeds rank), remaining
        /// components complete an orthonormal basis from canonical axes.
        /// </summary>
        private static (double[] Components, double[] Eigenvalues) TopKEigen(double[] matrix, int d, int k)
        {
            double[] a = (double[])matrix.Clone();
            double[] components = new double[k * d];
            double[] eigenvalues = new double[k];
            double[] v = new double[d];
            double[] w = new double[d];

            for (int c = 0; c < k; c++)
            {
                Span<double> component = components.AsSpan(c * d, d);

                // Deterministic start: axis with the largest remaining diagonal.
                int start = 0;
                double bestDiag = double.NegativeInfinity;
                for (int i = 0; i < d; i++)
                {
                    double diag = a[i * d + i];
                    if (diag > bestDiag)
                    {
                        bestDiag = diag;
                        start = i;
                    }
                }

                if (bestDiag <= 1e-12)
                {
                    // Matrix exhausted — complete the basis with a canonical axis
                    // orthogonalized against the components found so far.
                    FillOrthonormalCompletion(components, c, d, component);
                    eigenvalues[c] = 0;
                    continue;
                }

                Array.Clear(v);
                v[start] = 1.0;
                double eigenvalue = 0;
                for (int iteration = 0; iteration < MaxPowerIterations; iteration++)
                {
                    MultiplySymmetric(a, d, v, w);
                    double norm = Norm(w);
                    if (norm <= 1e-300)
                    {
                        break;
                    }
                    double drift = 0;
                    for (int i = 0; i < d; i++)
                    {
                        double next = w[i] / norm;
                        drift += System.Math.Abs(next - v[i]);
                        v[i] = next;
                    }
                    eigenvalue = norm;
                    if (drift < 1e-12)
                    {
                        break;
                    }
                }

                FixSign(v);
                v.CopyTo(component);
                eigenvalues[c] = eigenvalue;

                // Deflate: A ← A − λ vvᵀ.
                for (int i = 0; i < d; i++)
                {
                    double scaled = eigenvalue * v[i];
                    for (int j = 0; j < d; j++)
                    {
                        a[i * d + j] -= scaled * v[j];
                    }
                }
            }

            return (components, eigenvalues);
        }

        private static void MultiplySymmetric(double[] a, int d, double[] v, double[] result)
        {
            for (int i = 0; i < d; i++)
            {
                double acc = 0;
                int row = i * d;
                for (int j = 0; j < d; j++)
                {
                    acc += a[row + j] * v[j];
                }
                result[i] = acc;
            }
        }

        private static double Norm(double[] v)
        {
            double sumSq = 0;
            for (int i = 0; i < v.Length; i++) sumSq += v[i] * v[i];
            return System.Math.Sqrt(sumSq);
        }

        /// <summary>Pins the sign so the largest-magnitude entry (first on ties) is positive.</summary>
        private static void FixSign(double[] v)
        {
            int maxIdx = 0;
            double maxAbs = 0;
            for (int i = 0; i < v.Length; i++)
            {
                double abs = System.Math.Abs(v[i]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                    maxIdx = i;
                }
            }
            if (v[maxIdx] < 0)
            {
                for (int i = 0; i < v.Length; i++) v[i] = -v[i];
            }
        }

        /// <summary>
        /// Writes into <paramref name="component"/> the first canonical axis that
        /// survives Gram-Schmidt orthogonalization against components 0..c-1.
        /// </summary>
        private static void FillOrthonormalCompletion(double[] components, int c, int d, Span<double> component)
        {
            double[] work = new double[d];
            for (int axis = 0; axis < d; axis++)
            {
                Array.Clear(work);
                work[axis] = 1.0;
                for (int prev = 0; prev < c; prev++)
                {
                    ReadOnlySpan<double> p = components.AsSpan(prev * d, d);
                    double dot = 0;
                    for (int i = 0; i < d; i++) dot += work[i] * p[i];
                    for (int i = 0; i < d; i++) work[i] -= dot * p[i];
                }
                double norm = Norm(work);
                if (norm > 1e-6)
                {
                    for (int i = 0; i < d; i++) component[i] = work[i] / norm;
                    FixSignSpan(component);
                    return;
                }
            }
            // Unreachable for c < d, but keep the component zeroed as a safe fallback.
            component.Clear();
        }

        private static void FixSignSpan(Span<double> v)
        {
            int maxIdx = 0;
            double maxAbs = 0;
            for (int i = 0; i < v.Length; i++)
            {
                double abs = System.Math.Abs(v[i]);
                if (abs > maxAbs)
                {
                    maxAbs = abs;
                    maxIdx = i;
                }
            }
            if (v[maxIdx] < 0)
            {
                for (int i = 0; i < v.Length; i++) v[i] = -v[i];
            }
        }
    }
}
