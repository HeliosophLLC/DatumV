using System.Numerics;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar.Arrays;

/// <summary>
/// Shared min/max reduction over a typed array, used by
/// <see cref="ArrayMinFunction"/> and <see cref="ArrayMaxFunction"/>. Iterates
/// the flat element span (multi-dim arrays reduce across the whole tensor)
/// and dispatches per element kind to <see cref="IComparable{T}"/>-based
/// selection. Element kinds backed by a fixed-width primitive (numeric +
/// Boolean + temporal + Uuid + Decimal) are supported; reference-array kinds
/// (String, Struct, the media kinds, Json) and the meta kinds (Unknown, Type,
/// Point2D) are rejected.
/// </summary>
internal static class ArrayMinMaxCore
{
    internal static ValueRef Execute(
        ValueRef arrayArg,
        bool pickSmaller,
        EvaluationFrame frame,
        string functionName)
    {
        DataKind elementKind = arrayArg.ArrayElementKind;
        if (arrayArg.IsNull)
        {
            return ValueRef.Null(elementKind);
        }

        DataValue source = arrayArg.ToDataValue(frame.Source);

        return elementKind switch
        {
            DataKind.UInt8    => ReduceWrap<byte>   (source, frame, pickSmaller, elementKind, ValueRef.FromUInt8),
            DataKind.Int8     => ReduceWrap<sbyte>  (source, frame, pickSmaller, elementKind, b => ValueRef.FromInt8(b)),
            DataKind.UInt16   => ReduceWrap<ushort> (source, frame, pickSmaller, elementKind, ValueRef.FromUInt16),
            DataKind.Int16    => ReduceWrap<short>  (source, frame, pickSmaller, elementKind, ValueRef.FromInt16),
            DataKind.Float16  => ReduceWrap<Half>   (source, frame, pickSmaller, elementKind, ValueRef.FromFloat16),
            DataKind.UInt32   => ReduceWrap<uint>   (source, frame, pickSmaller, elementKind, ValueRef.FromUInt32),
            DataKind.Int32    => ReduceWrap<int>    (source, frame, pickSmaller, elementKind, ValueRef.FromInt32),
            DataKind.Float32  => ReduceWrap<float>  (source, frame, pickSmaller, elementKind, ValueRef.FromFloat32),
            DataKind.UInt64   => ReduceWrap<ulong>  (source, frame, pickSmaller, elementKind, ValueRef.FromUInt64),
            DataKind.Int64    => ReduceWrap<long>   (source, frame, pickSmaller, elementKind, ValueRef.FromInt64),
            DataKind.Float64  => ReduceWrap<double> (source, frame, pickSmaller, elementKind, ValueRef.FromFloat64),
            DataKind.Int128   => ReduceWrap<Int128> (source, frame, pickSmaller, elementKind, ValueRef.FromInt128),
            DataKind.UInt128  => ReduceWrap<UInt128>(source, frame, pickSmaller, elementKind, ValueRef.FromUInt128),
            DataKind.Decimal  => ReduceWrap<decimal>(source, frame, pickSmaller, elementKind, ValueRef.FromDecimal),
            DataKind.Uuid     => ReduceWrap<Guid>   (source, frame, pickSmaller, elementKind, ValueRef.FromUuid),

            // Temporal kinds are stored as their tick / day-number primitive;
            // compare on the raw primitive (monotonic in the wall value) and
            // wrap the winner with the proper temporal factory.
            DataKind.Date        => ReduceWrap<int> (source, frame, pickSmaller, elementKind,
                                        d => ValueRef.FromDate(DateOnly.FromDayNumber(d))),
            DataKind.Time        => ReduceWrap<long>(source, frame, pickSmaller, elementKind,
                                        t => ValueRef.FromTime(new TimeOnly(t))),
            DataKind.Duration    => ReduceWrap<long>(source, frame, pickSmaller, elementKind,
                                        t => ValueRef.FromDuration(new TimeSpan(t))),
            DataKind.Timestamp   => ReduceWrap<long>(source, frame, pickSmaller, elementKind,
                                        t => ValueRef.FromTimestamp(new DateTime(t))),
            DataKind.TimestampTz => ReduceWrap<long>(source, frame, pickSmaller, elementKind,
                                        t => ValueRef.FromTimestampTz(new DateTimeOffset(t, TimeSpan.Zero))),

            // Boolean storage is byte (0/1). Compare as byte, wrap as bool.
            DataKind.Boolean => ReduceWrap<byte>(source, frame, pickSmaller, elementKind,
                                    b => ValueRef.FromBoolean(b != 0)),

            // Reference-array carriers (ValueRef[] / string[] / arena blobs) and
            // kinds without natural ordering. Media kinds and Json/Struct have
            // no ordinal semantics; Point2D is a vector, not a scalar; String
            // would need cross-arena/sidecar resolution and is not yet wired.
            DataKind.String or DataKind.Struct
                or DataKind.Image or DataKind.Audio
                or DataKind.Video or DataKind.VideoFrame
                or DataKind.Json or DataKind.PointCloud or DataKind.Mesh
                or DataKind.Point2D or DataKind.Point3D
                or DataKind.Unknown or DataKind.Type
                => throw new FunctionArgumentException(functionName,
                    $"does not support element kind {elementKind}."),

            _ => throw new FunctionArgumentException(functionName,
                $"does not support element kind {elementKind}."),
        };
    }

    private static ValueRef ReduceWrap<T>(
        DataValue source,
        EvaluationFrame frame,
        bool pickSmaller,
        DataKind elementKind,
        Func<T, ValueRef> wrap)
        where T : unmanaged, IComparable<T>
    {
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        if (elements.Length == 0)
        {
            return ValueRef.Null(elementKind);
        }
        T best = elements[0];
        for (int i = 1; i < elements.Length; i++)
        {
            int cmp = elements[i].CompareTo(best);
            if (pickSmaller ? cmp < 0 : cmp > 0)
            {
                best = elements[i];
            }
        }
        return wrap(best);
    }
}
