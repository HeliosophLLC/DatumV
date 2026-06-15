using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Arrays;

/// <summary>
/// Shared linear-scan engine for <see cref="ArrayContainsFunction"/> and
/// <see cref="ArrayPositionFunction"/>. Walks the typed element span looking
/// for the first slot equal to the supplied search value, returning the
/// 1-based index of the match (or 0 for "not found"). Numeric kinds widen
/// the search value to the element kind; mismatched kinds outside the
/// numeric family return "not found" without throwing — matching the
/// PostgreSQL semantic where <c>contains(int[], 'foo')</c> simply produces
/// false rather than a type error.
/// </summary>
internal static class ArraySearchCore
{
    /// <summary>
    /// Sentinel returned for "not found" — distinct from any 1-based index.
    /// </summary>
    internal const int NotFound = 0;

    /// <summary>
    /// Searches for <paramref name="searchValue"/> in <paramref name="arrayArg"/>
    /// and returns the 1-based index of the first match, or
    /// <see cref="NotFound"/> if no element compares equal. A null search
    /// value matches nothing (SQL NULL ≠ anything, including NULL).
    /// </summary>
    internal static int IndexOf(
        ValueRef arrayArg,
        ValueRef searchValue,
        EvaluationFrame frame,
        string functionName)
    {
        if (searchValue.IsNull)
        {
            return NotFound;
        }

        DataKind elementKind = arrayArg.ArrayElementKind;
        DataValue source = arrayArg.ToDataValue(frame.Source);

        return elementKind switch
        {
            DataKind.UInt8   => SearchInteger<byte>   (source, searchValue, frame, byte.MinValue,    byte.MaxValue,    (long v) => (byte)v),
            DataKind.Int8    => SearchInteger<sbyte>  (source, searchValue, frame, sbyte.MinValue,   sbyte.MaxValue,   (long v) => (sbyte)v),
            DataKind.UInt16  => SearchInteger<ushort> (source, searchValue, frame, ushort.MinValue,  ushort.MaxValue,  (long v) => (ushort)v),
            DataKind.Int16   => SearchInteger<short>  (source, searchValue, frame, short.MinValue,   short.MaxValue,   (long v) => (short)v),
            DataKind.UInt32  => SearchInteger<uint>   (source, searchValue, frame, uint.MinValue,    uint.MaxValue,    (long v) => (uint)v),
            DataKind.Int32   => SearchInteger<int>    (source, searchValue, frame, int.MinValue,     int.MaxValue,     (long v) => (int)v),
            DataKind.Int64   => SearchInt64           (source, searchValue, frame),
            DataKind.UInt64  => SearchUInt64          (source, searchValue, frame),
            DataKind.Float16 => SearchFloat<Half>     (source, searchValue, frame, d => (Half)d),
            DataKind.Float32 => SearchFloat<float>    (source, searchValue, frame, d => (float)d),
            DataKind.Float64 => SearchFloat<double>   (source, searchValue, frame, d => d),
            DataKind.Boolean => SearchBoolean(arrayArg, searchValue),
            DataKind.String  => SearchString(arrayArg, searchValue, frame),

            _ => throw new FunctionArgumentException(functionName,
                $"does not support element kind {elementKind}."),
        };
    }

    private static int SearchInteger<T>(
        DataValue source,
        ValueRef searchValue,
        EvaluationFrame frame,
        long min,
        long max,
        Func<long, T> narrow)
        where T : unmanaged, IEquatable<T>
    {
        if (!searchValue.TryToInt64(out long v) || v < min || v > max)
        {
            return NotFound;
        }
        T needle = narrow(v);
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Equals(needle))
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchInt64(DataValue source, ValueRef searchValue, EvaluationFrame frame)
    {
        if (!searchValue.TryToInt64(out long needle))
        {
            return NotFound;
        }
        ReadOnlySpan<long> elements = source.AsArraySpan<long>(frame.Source, frame.SidecarRegistry);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i] == needle)
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchUInt64(DataValue source, ValueRef searchValue, EvaluationFrame frame)
    {
        if (!searchValue.TryToInt64(out long v) || v < 0)
        {
            return NotFound;
        }
        ulong needle = (ulong)v;
        ReadOnlySpan<ulong> elements = source.AsArraySpan<ulong>(frame.Source, frame.SidecarRegistry);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i] == needle)
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchFloat<T>(
        DataValue source,
        ValueRef searchValue,
        EvaluationFrame frame,
        Func<double, T> narrow)
        where T : unmanaged, IEquatable<T>
    {
        if (!searchValue.TryToDouble(out double v))
        {
            return NotFound;
        }
        T needle = narrow(v);
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].Equals(needle))
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchBoolean(ValueRef arrayArg, ValueRef searchValue)
    {
        if (searchValue.Kind != DataKind.Boolean)
        {
            return NotFound;
        }
        bool needle = searchValue.AsBoolean();

        return arrayArg.Materialized switch
        {
            byte[] bytes  => SearchBooleanBytes(bytes, needle),
            bool[] bools  => SearchBooleanBools(bools, needle),
            _ => NotFound,
        };
    }

    private static int SearchBooleanBytes(byte[] bytes, bool needle)
    {
        byte target = needle ? (byte)1 : (byte)0;
        for (int i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == target)
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchBooleanBools(bool[] bools, bool needle)
    {
        for (int i = 0; i < bools.Length; i++)
        {
            if (bools[i] == needle)
            {
                return i + 1;
            }
        }
        return NotFound;
    }

    private static int SearchString(ValueRef arrayArg, ValueRef searchValue, EvaluationFrame frame)
    {
        if (searchValue.Kind != DataKind.String)
        {
            return NotFound;
        }
        string needle = ReadString(searchValue, frame);

        if (arrayArg.Materialized is string[] strings)
        {
            for (int i = 0; i < strings.Length; i++)
            {
                if (string.Equals(strings[i], needle, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }
            return NotFound;
        }

        if (arrayArg.Materialized is ValueRef[] refs)
        {
            for (int i = 0; i < refs.Length; i++)
            {
                if (refs[i].IsNull || refs[i].Kind != DataKind.String) continue;
                string element = ReadString(refs[i], frame);
                if (string.Equals(element, needle, StringComparison.Ordinal))
                {
                    return i + 1;
                }
            }
            return NotFound;
        }

        return NotFound;
    }

    private static string ReadString(ValueRef value, EvaluationFrame frame)
    {
        DataValue dv = value.ToDataValue(frame.Source);
        return dv.AsString(frame.Source, frame.SidecarRegistry);
    }
}
