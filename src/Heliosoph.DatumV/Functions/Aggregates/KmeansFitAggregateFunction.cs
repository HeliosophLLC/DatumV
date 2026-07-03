using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Aggregates;

/// <summary>
/// <c>kmeans_fit_agg(vec FLOAT32[], k [, options STRUCT]) → Struct{centroids FLOAT32[k,d],
/// inertia FLOAT32, iterations INT32}</c> — fits a k-means clustering over the
/// group's vectors via k-means++ seeding and Lloyd's algorithm. Pair with
/// <c>nearest_centroid(model.centroids, vec)</c> to label rows by cluster.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Options.</strong> The optional third argument is a struct literal with
/// any of: <c>seed</c> (int, default 42), <c>max_iter</c> (int, default 100),
/// <c>tol</c> (float, default 1e-4 — convergence threshold on the largest
/// centroid shift per iteration). Unrecognized fields are ignored. Options are
/// captured from the first row and must be constant across the group.
/// </para>
/// <para>
/// <strong>Determinism.</strong> Seeding uses a PRNG with a fixed default seed, so
/// the same group in the same order always yields the same model. Centroid order
/// is an artifact of seeding, not significance — treat cluster ids as opaque
/// labels.
/// </para>
/// <para>
/// <strong>Memory.</strong> The accumulator retains the group's vectors in managed
/// memory (O(n·d) floats) and runs Lloyd's at finalize; nothing is written to the
/// arena until the result struct is built. Merge concatenates vector lists, so
/// parallel hash-aggregate merging is supported.
/// </para>
/// <para>
/// <strong>Semantics.</strong> Null vectors are skipped. The first non-null vector
/// pins d; later mismatches throw. Groups with zero vectors return NULL; fewer
/// vectors than k throws. Empty clusters are re-seeded deterministically with the
/// point farthest from its assigned centroid.
/// </para>
/// </remarks>
public sealed class KmeansFitAggregateFunction : IAggregateFunction, IAggregateFunctionMetadata
{
    private const int DefaultSeed = 42;
    private const int DefaultMaxIterations = 100;
    private const float DefaultTolerance = 1e-4f;

    /// <inheritdoc cref="IAggregateFunctionMetadata.Name"/>
    public static string Name => "kmeans_fit_agg";

    /// <inheritdoc/>
    string IAggregateFunction.Name => Name;

    /// <inheritdoc/>
    public static FunctionCategory Category => FunctionCategory.Aggregate;

    /// <inheritdoc/>
    public static string Description =>
        "Fits k-means clusters over the group's Float32 vectors (k-means++ seeding, Lloyd's algorithm): "
        + "kmeans_fit_agg(vec FLOAT32[], k [, {seed, max_iter, tol}]) → Struct{centroids FLOAT32[k,d], inertia, iterations}. "
        + "Label rows with nearest_centroid(model.centroids, vec).";

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
        new FunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("vec", DataKindMatcher.Exact(DataKind.Float32), IsArray: ArrayMatch.Array),
                new ParameterSpec("k", DataKindMatcher.Family(DataKindFamily.IntegerFamily)),
                new ParameterSpec("options", DataKindMatcher.Exact(DataKind.Struct)),
            ],
            VariadicTrailing: null,
            ReturnType: ReturnTypeRule.Constant(DataKind.Struct)),
    ];

    /// <inheritdoc/>
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is not (2 or 3))
        {
            throw new ArgumentException(
                "kmeans_fit_agg() requires two or three arguments: vector, cluster count k, and optional options struct.");
        }
        if (argumentKinds[0] != DataKind.Float32)
        {
            throw new ArgumentException(
                $"kmeans_fit_agg() first argument must be FLOAT32[], got {argumentKinds[0]}.");
        }
        if (!DataKindFamily.IntegerFamily.Contains(argumentKinds[1]))
        {
            throw new ArgumentException(
                $"kmeans_fit_agg() second argument (k) must be an integer, got {argumentKinds[1]}.");
        }
        if (argumentKinds.Length == 3 && argumentKinds[2] != DataKind.Struct)
        {
            throw new ArgumentException(
                $"kmeans_fit_agg() third argument (options) must be a struct, got {argumentKinds[2]}.");
        }
        return DataKind.Struct;
    }

    /// <inheritdoc/>
    public ReturnTypeRule ReturnRule { get; } = ReturnTypeRule.Constant(DataKind.Struct);

    /// <inheritdoc/>
    public IAggregateAccumulator CreateAccumulator() => new Accumulator();

    /// <summary>
    /// Collects the group's vectors in managed memory; k-means++ seeding and
    /// Lloyd's iterations run once at <see cref="ResultAsync"/>.
    /// </summary>
    private sealed class Accumulator : IAggregateAccumulator
    {
        private readonly List<float[]> _vectors = [];
        private int _d = -1;
        private int _k = -1;
        private int _seed = DefaultSeed;
        private int _maxIterations = DefaultMaxIterations;
        private float _tolerance = DefaultTolerance;
        private bool _optionsCaptured;

        public void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame)
        {
            if (!arguments[1].IsNull)
            {
                CaptureK(arguments[1]);
            }
            if (arguments.Length == 3 && !_optionsCaptured && !arguments[2].IsNull)
            {
                CaptureOptions(arguments[2], frame);
            }

            if (arguments[0].IsNull)
            {
                return;
            }

            ReadOnlySpan<float> vec = arguments[0].AsArraySpan<float>(frame.Source, frame.SidecarRegistry);
            if (_d < 0)
            {
                if (vec.Length < 1)
                {
                    throw new FunctionArgumentException(Name, "input vectors must have at least one element.");
                }
                _d = vec.Length;
            }
            else if (vec.Length != _d)
            {
                throw new FunctionArgumentException(Name,
                    $"expected a {_d}-dimensional vector (pinned by the group's first vector), got {vec.Length} dimensions.");
            }

            _vectors.Add(vec.ToArray());
        }

        private void CaptureK(in DataValue kArg)
        {
            if (!kArg.TryToFloat(out float kFloat))
            {
                throw new FunctionArgumentException(Name,
                    $"could not read cluster count k from a {kArg.Kind} value.");
            }
            int k = (int)kFloat;
            if (k < 1)
            {
                throw new FunctionArgumentException(Name,
                    $"cluster count k must be at least 1, got {k}.");
            }
            if (_k < 0)
            {
                _k = k;
            }
            else if (k != _k)
            {
                throw new FunctionArgumentException(Name,
                    $"cluster count k must be constant across the group; saw {_k} and {k}.");
            }
        }

        private void CaptureOptions(in DataValue optionsArg, in InvocationFrame frame)
        {
            _optionsCaptured = true;

            TypeDescriptor? descriptor = frame.Types?.GetDescriptor(optionsArg.TypeId);
            if (descriptor?.Fields is null)
            {
                throw new FunctionArgumentException(Name,
                    "options struct has no registered field descriptors; expected a struct literal "
                    + "with any of seed, max_iter, tol.");
            }

            DataValue[] fields = optionsArg.AsStruct(frame.Source);
            for (int i = 0; i < descriptor.Fields.Count && i < fields.Length; i++)
            {
                if (fields[i].IsNull) continue;
                if (!fields[i].TryToFloat(out float value)) continue;

                switch (descriptor.Fields[i].Name.ToLowerInvariant())
                {
                    case "seed":
                        _seed = (int)value;
                        break;
                    case "max_iter":
                        int maxIter = (int)value;
                        if (maxIter < 1)
                        {
                            throw new FunctionArgumentException(Name,
                                $"max_iter must be at least 1, got {maxIter}.");
                        }
                        _maxIterations = maxIter;
                        break;
                    case "tol":
                        if (value < 0)
                        {
                            throw new FunctionArgumentException(Name,
                                $"tol must be non-negative, got {value}.");
                        }
                        _tolerance = value;
                        break;
                }
            }
        }

        /// <inheritdoc/>
        public ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame)
        {
            Accumulator o = (Accumulator)other;
            if (o._k >= 0)
            {
                if (_k >= 0 && _k != o._k)
                {
                    throw new FunctionArgumentException(Name,
                        $"cluster count k must be constant across the group; saw {_k} and {o._k}.");
                }
                _k = o._k;
            }
            if (o._optionsCaptured && !_optionsCaptured)
            {
                _seed = o._seed;
                _maxIterations = o._maxIterations;
                _tolerance = o._tolerance;
                _optionsCaptured = true;
            }
            if (o._d >= 0)
            {
                if (_d >= 0 && _d != o._d)
                {
                    throw new FunctionArgumentException(Name,
                        $"cannot merge groups with different vector dimensionality: {_d} vs {o._d}.");
                }
                _d = o._d;
            }
            _vectors.AddRange(o._vectors);
            return ValueTask.CompletedTask;
        }

        public ValueTask<DataValue> ResultAsync(InvocationFrame frame)
        {
            if (_vectors.Count == 0)
            {
                return new(DataValue.NullStruct(TypeRegistry.NoType));
            }
            int k = _k > 0 ? _k : 1;
            int n = _vectors.Count;
            int d = _d;
            if (n < k)
            {
                throw new FunctionArgumentException(Name,
                    $"requires at least k non-null vectors per group; got {n} vectors for k = {k}.");
            }

            var (centroids, inertia, iterations) = Fit(k, n, d);

            float[] centroidsOut = new float[k * d];
            for (int c = 0; c < k; c++)
            {
                for (int i = 0; i < d; i++)
                {
                    centroidsOut[c * d + i] = (float)centroids[c][i];
                }
            }

            ushort typeId = TypeRegistry.NoType;
            if (frame.Types is { } types)
            {
                int float32ArrayTypeId = types.InternArrayType(DataKind.Float32);
                int float32ScalarTypeId = types.InternScalarType(DataKind.Float32);
                int int32ScalarTypeId = types.InternScalarType(DataKind.Int32);
                typeId = (ushort)types.InternStructType(
                [
                    new StructFieldDescriptor("centroids", float32ArrayTypeId),
                    new StructFieldDescriptor("inertia", float32ScalarTypeId),
                    new StructFieldDescriptor("iterations", int32ScalarTypeId),
                ]);
            }

            DataValue[] fields =
            [
                DataValue.FromArenaMultiDimArray<float>(centroidsOut, [k, d], DataKind.Float32, frame.Target),
                DataValue.FromFloat32((float)inertia),
                DataValue.FromInt32(iterations),
            ];
            return new(DataValue.FromStruct(fields, frame.Target, typeId));
        }

        /// <inheritdoc />
        public void Reset()
        {
            _vectors.Clear();
            _d = -1;
            _k = -1;
            _seed = DefaultSeed;
            _maxIterations = DefaultMaxIterations;
            _tolerance = DefaultTolerance;
            _optionsCaptured = false;
        }

        private (double[][] Centroids, double Inertia, int Iterations) Fit(int k, int n, int d)
        {
            Random rng = new(_seed);
            double[][] centroids = SeedPlusPlus(rng, k, n, d);
            int[] assignment = new int[n];
            int[] counts = new int[k];
            double[][] sums = new double[k][];
            for (int c = 0; c < k; c++) sums[c] = new double[d];

            int iterations = 0;
            for (int iteration = 0; iteration < _maxIterations; iteration++)
            {
                iterations++;

                // Assign.
                for (int p = 0; p < n; p++)
                {
                    assignment[p] = NearestIndex(centroids, _vectors[p], k, d);
                }

                // Recompute means.
                Array.Clear(counts);
                for (int c = 0; c < k; c++) Array.Clear(sums[c]);
                for (int p = 0; p < n; p++)
                {
                    int c = assignment[p];
                    counts[c]++;
                    float[] v = _vectors[p];
                    double[] sum = sums[c];
                    for (int i = 0; i < d; i++) sum[i] += v[i];
                }

                double maxShift = 0;
                for (int c = 0; c < k; c++)
                {
                    if (counts[c] == 0)
                    {
                        // Empty cluster: re-seed with the point farthest from its
                        // assigned centroid (first on ties) — deterministic.
                        int farthest = 0;
                        double farthestDist = -1;
                        for (int p = 0; p < n; p++)
                        {
                            double dist = DistanceSquared(centroids[assignment[p]], _vectors[p], d);
                            if (dist > farthestDist)
                            {
                                farthestDist = dist;
                                farthest = p;
                            }
                        }
                        float[] v = _vectors[farthest];
                        for (int i = 0; i < d; i++) centroids[c][i] = v[i];
                        maxShift = double.MaxValue;
                        continue;
                    }

                    double shiftSq = 0;
                    double[] sum = sums[c];
                    for (int i = 0; i < d; i++)
                    {
                        double next = sum[i] / counts[c];
                        double delta = next - centroids[c][i];
                        shiftSq += delta * delta;
                        centroids[c][i] = next;
                    }
                    maxShift = System.Math.Max(maxShift, System.Math.Sqrt(shiftSq));
                }

                if (maxShift <= _tolerance)
                {
                    break;
                }
            }

            // Final assignment for inertia against the converged centroids.
            double inertia = 0;
            for (int p = 0; p < n; p++)
            {
                inertia += DistanceSquared(centroids[NearestIndex(centroids, _vectors[p], k, d)], _vectors[p], d);
            }

            return (centroids, inertia, iterations);
        }

        /// <summary>k-means++ seeding: D²-weighted sampling with a seeded PRNG.</summary>
        private double[][] SeedPlusPlus(Random rng, int k, int n, int d)
        {
            double[][] centroids = new double[k][];
            double[] nearestD2 = new double[n];

            int first = rng.Next(n);
            centroids[0] = ToDouble(_vectors[first], d);
            for (int p = 0; p < n; p++)
            {
                nearestD2[p] = DistanceSquared(centroids[0], _vectors[p], d);
            }

            for (int c = 1; c < k; c++)
            {
                double total = 0;
                for (int p = 0; p < n; p++) total += nearestD2[p];

                int chosen;
                if (total <= 0)
                {
                    // All remaining points coincide with chosen centroids.
                    chosen = rng.Next(n);
                }
                else
                {
                    double r = rng.NextDouble() * total;
                    chosen = n - 1;
                    double cumulative = 0;
                    for (int p = 0; p < n; p++)
                    {
                        cumulative += nearestD2[p];
                        if (cumulative >= r)
                        {
                            chosen = p;
                            break;
                        }
                    }
                }

                centroids[c] = ToDouble(_vectors[chosen], d);
                for (int p = 0; p < n; p++)
                {
                    nearestD2[p] = System.Math.Min(nearestD2[p], DistanceSquared(centroids[c], _vectors[p], d));
                }
            }

            return centroids;
        }

        private static double[] ToDouble(float[] v, int d)
        {
            double[] result = new double[d];
            for (int i = 0; i < d; i++) result[i] = v[i];
            return result;
        }

        private static int NearestIndex(double[][] centroids, float[] v, int k, int d)
        {
            int best = 0;
            double bestDist = double.MaxValue;
            for (int c = 0; c < k; c++)
            {
                double dist = DistanceSquared(centroids[c], v, d);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }
            return best;
        }

        private static double DistanceSquared(double[] centroid, float[] v, int d)
        {
            double sum = 0;
            for (int i = 0; i < d; i++)
            {
                double delta = centroid[i] - v[i];
                sum += delta * delta;
            }
            return sum;
        }
    }
}
