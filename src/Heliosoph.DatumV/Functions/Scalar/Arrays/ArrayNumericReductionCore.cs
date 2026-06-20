using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Shared sum-and-count reduction over a typed numeric array, used by
/// <see cref="ArraySumFunction"/> and <see cref="ArrayAvgFunction"/>. Iterates
/// the flat element span (multi-dim arrays reduce across the whole tensor)
/// and accumulates into a <see cref="double"/>. Only the integer and float
/// element kinds carry an arithmetic identity; every other kind is rejected
/// with a clear per-function message.
/// </summary>
internal static class ArrayNumericReductionCore
{
    /// <summary>
    /// Sum across the typed primitive span. Returns the running sum and the
    /// element count; an empty span yields <c>(0, 0)</c> and lets the caller
    /// decide between "null" (avg of nothing) and "zero" semantics.
    /// </summary>
    internal static (double Sum, int Count) SumAndCount(
        ValueRef arrayArg,
        EvaluationFrame frame,
        string functionName)
    {
        DataKind elementKind = arrayArg.ArrayElementKind;
        DataValue source = arrayArg.ToDataValue(frame.Source);

        return elementKind switch
        {
            DataKind.UInt8   => SumPrimitive<byte>   (source, frame, b => b),
            DataKind.Int8    => SumPrimitive<sbyte>  (source, frame, b => b),
            DataKind.UInt16  => SumPrimitive<ushort> (source, frame, u => u),
            DataKind.Int16   => SumPrimitive<short>  (source, frame, s => s),
            DataKind.Float16 => SumPrimitive<Half>   (source, frame, h => (double)h),
            DataKind.UInt32  => SumPrimitive<uint>   (source, frame, u => u),
            DataKind.Int32   => SumPrimitive<int>    (source, frame, i => i),
            DataKind.Float32 => SumPrimitive<float>  (source, frame, f => f),
            DataKind.UInt64  => SumPrimitive<ulong>  (source, frame, u => u),
            DataKind.Int64   => SumPrimitive<long>   (source, frame, l => l),
            DataKind.Float64 => SumPrimitive<double> (source, frame, d => d),

            _ => throw new FunctionArgumentException(functionName,
                $"requires a numeric element kind, got Array<{elementKind}>."),
        };
    }

    private static (double Sum, int Count) SumPrimitive<T>(
        DataValue source,
        EvaluationFrame frame,
        Func<T, double> widen)
        where T : unmanaged
    {
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        double sum = 0;
        for (int i = 0; i < elements.Length; i++)
        {
            sum += widen(elements[i]);
        }
        return (sum, elements.Length);
    }
}
