using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Distributions;

/// <summary>
/// Shared helpers for the <c>random_*</c> distribution functions: seed-aware
/// RNG selection, integer extraction, and the Box-Muller / Marsaglia-Tsang
/// primitives that several of the distributions sit on top of.
/// </summary>
internal static class RandomDistributionsCore
{
    /// <summary>
    /// Picks the RNG for a call: <see cref="Random.Shared"/> when no
    /// seed argument is present, otherwise a fresh <see cref="Random"/>
    /// seeded from the trailing integer argument.
    /// </summary>
    /// <returns>
    /// <see langword="false"/> when a seed argument is present but null —
    /// callers should propagate a null result of the appropriate kind.
    /// </returns>
    public static bool TryGetRng(
        ReadOnlySpan<ValueRef> args,
        int seedIndex,
        out Random rng)
    {
        if (args.Length > seedIndex)
        {
            ValueRef seedArg = args[seedIndex];
            if (seedArg.IsNull)
            {
                rng = null!;
                return false;
            }
            int seed = unchecked((int)ReadInteger(seedArg));
            rng = new Random(seed);
            return true;
        }
        rng = Random.Shared;
        return true;
    }

    /// <summary>Reads any integer-family <see cref="ValueRef"/> as a <see cref="long"/>.</summary>
    public static long ReadInteger(ValueRef v) => v.Kind switch
    {
        DataKind.Int8 => v.AsInt8(),
        DataKind.UInt8 => v.AsUInt8(),
        DataKind.Int16 => v.AsInt16(),
        DataKind.UInt16 => v.AsUInt16(),
        DataKind.Int32 => v.AsInt32(),
        DataKind.UInt32 => v.AsUInt32(),
        DataKind.Int64 => v.AsInt64(),
        DataKind.UInt64 => unchecked((long)v.AsUInt64()),
        _ => throw new FunctionArgumentException("random", $"unsupported integer kind {v.Kind}."),
    };

    /// <summary>Standard normal sample N(0, 1) via the Box-Muller transform.</summary>
    public static double SampleStandardNormal(Random rng)
    {
        double u1 = 1.0 - rng.NextDouble();
        double u2 = rng.NextDouble();
        return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2.0 * System.Math.PI * u2);
    }

    /// <summary>Gamma(shape, 1) sample via Marsaglia and Tsang's method.</summary>
    public static double SampleGamma(Random rng, double shape)
    {
        if (shape < 1.0)
        {
            double boost = System.Math.Pow(rng.NextDouble(), 1.0 / shape);
            return SampleGamma(rng, shape + 1.0) * boost;
        }

        double d = shape - 1.0 / 3.0;
        double c = 1.0 / System.Math.Sqrt(9.0 * d);

        while (true)
        {
            double x = SampleStandardNormal(rng);
            double v = 1.0 + c * x;
            if (v <= 0) continue;
            v = v * v * v;
            double u = rng.NextDouble();
            if (u < 1.0 - 0.0331 * (x * x) * (x * x))
                return d * v;
            if (System.Math.Log(u) < 0.5 * x * x + d * (1.0 - v + System.Math.Log(v)))
                return d * v;
        }
    }
}
