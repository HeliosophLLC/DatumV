using DatumIngest.Model;

namespace DatumIngest.Functions.Math;

/// <summary>
/// Computes the cosine similarity between two vectors: cosine_similarity(a, b).
/// Returns a Scalar in the range [-1, 1].
/// </summary>
public sealed class CosineSimilarityFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "cosine_similarity";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("cosine_similarity() requires exactly 2 arguments.");
        if (argumentKinds[0] is not DataKind.Vector || argumentKinds[1] is not DataKind.Vector)
            throw new ArgumentException("cosine_similarity() requires two Vector arguments.");
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return DataValue.Null(DataKind.Float32);
        float[] a = arguments[0].AsVector();
        float[] b = arguments[1].AsVector();
        int length = System.Math.Min(a.Length, b.Length);

        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }

        float denominator = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return DataValue.FromFloat32(denominator == 0f ? 0f : dot / denominator);
    }
}

/// <summary>
/// Computes the Euclidean (L2) distance between two vectors: euclidean_distance(a, b).
/// </summary>
public sealed class EuclideanDistanceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "euclidean_distance";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("euclidean_distance() requires exactly 2 arguments.");
        if (argumentKinds[0] is not DataKind.Vector || argumentKinds[1] is not DataKind.Vector)
            throw new ArgumentException("euclidean_distance() requires two Vector arguments.");
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return DataValue.Null(DataKind.Float32);
        float[] a = arguments[0].AsVector();
        float[] b = arguments[1].AsVector();
        int length = System.Math.Min(a.Length, b.Length);

        float sumSqDiff = 0f;
        for (int i = 0; i < length; i++)
        {
            float diff = a[i] - b[i];
            sumSqDiff += diff * diff;
        }

        return DataValue.FromFloat32(MathF.Sqrt(sumSqDiff));
    }
}

/// <summary>
/// Computes the Manhattan (L1) distance between two vectors: manhattan_distance(a, b).
/// </summary>
public sealed class ManhattanDistanceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "manhattan_distance";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("manhattan_distance() requires exactly 2 arguments.");
        if (argumentKinds[0] is not DataKind.Vector || argumentKinds[1] is not DataKind.Vector)
            throw new ArgumentException("manhattan_distance() requires two Vector arguments.");
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return DataValue.Null(DataKind.Float32);
        float[] a = arguments[0].AsVector();
        float[] b = arguments[1].AsVector();
        int length = System.Math.Min(a.Length, b.Length);

        float sum = 0f;
        for (int i = 0; i < length; i++)
        {
            sum += MathF.Abs(a[i] - b[i]);
        }

        return DataValue.FromFloat32(sum);
    }
}

/// <summary>
/// Computes the dot product of two vectors: dot(a, b) = Σ(aᵢ * bᵢ).
/// </summary>
public sealed class DotFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "dot";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("dot() requires exactly 2 arguments.");
        if (argumentKinds[0] is not DataKind.Vector || argumentKinds[1] is not DataKind.Vector)
            throw new ArgumentException("dot() requires two Vector arguments.");
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return DataValue.Null(DataKind.Float32);
        float[] a = arguments[0].AsVector();
        float[] b = arguments[1].AsVector();
        int length = System.Math.Min(a.Length, b.Length);

        float dot = 0f;
        for (int i = 0; i < length; i++)
        {
            dot += a[i] * b[i];
        }

        return DataValue.FromFloat32(dot);
    }
}

/// <summary>
/// Computes the Hamming distance between two strings: hamming_distance(a, b).
/// Counts the number of positions where the characters differ. Strings must be the same length.
/// </summary>
public sealed class HammingDistanceFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "hamming_distance";

    /// <inheritdoc />
    public int QueryUnitCost => 2;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 2)
            throw new ArgumentException("hamming_distance() requires exactly 2 arguments.");
        if (argumentKinds[0] is not DataKind.String || argumentKinds[1] is not DataKind.String)
            throw new ArgumentException("hamming_distance() requires two String arguments.");
        return DataKind.Float32;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull || arguments[1].IsNull) return DataValue.Null(DataKind.Float32);
        string a = arguments[0].AsString();
        string b = arguments[1].AsString();

        int length = System.Math.Min(a.Length, b.Length);
        int distance = System.Math.Abs(a.Length - b.Length);

        for (int i = 0; i < length; i++)
        {
            if (a[i] != b[i]) distance++;
        }

        return DataValue.FromFloat32(distance);
    }
}
