using DatumIngest.Model;
using Parquet.Data;

namespace DatumIngest.Serialization.Parquet;

/// <summary>
/// Extracts <see cref="DataValue"/> instances from Parquet <see cref="DataColumn"/> arrays
/// using typed casts instead of <see cref="Array.GetValue(long)"/> boxing.
/// </summary>
internal static class ParquetValueExtractor
{
    /// <summary>
    /// Extracts a <see cref="DataValue"/> at the given row index from a typed Parquet column.
    /// Uses direct array casts to avoid boxing allocations.
    /// </summary>
    internal static DataValue Extract(DataColumn column, DataKind kind, long rowIndex, IValueStore store)
    {
        Array data = column.Data;

        return kind switch
        {
            DataKind.Float32 => ExtractFloat32(data, rowIndex),
            DataKind.Float64 => ExtractFloat64(data, rowIndex),
            DataKind.Int8 => ExtractInt8(data, rowIndex),
            DataKind.Int16 => ExtractInt16(data, rowIndex),
            DataKind.Int32 => ExtractInt32(data, rowIndex),
            DataKind.Int64 => ExtractInt64(data, rowIndex),
            DataKind.UInt8 => ExtractUInt8(data, rowIndex),
            DataKind.UInt16 => ExtractUInt16(data, rowIndex),
            DataKind.UInt32 => ExtractUInt32(data, rowIndex),
            DataKind.UInt64 => ExtractUInt64(data, rowIndex),
            DataKind.Boolean => ExtractBoolean(data, rowIndex),
            DataKind.String => ExtractString(data, rowIndex, store),
            DataKind.DateTime => ExtractDateTime(data, rowIndex),
            DataKind.Date => ExtractDate(data, rowIndex),
            DataKind.UInt8Array => ExtractUInt8Array(data, rowIndex, store),
            _ => throw new NotSupportedException(
                $"ParquetValueExtractor does not support {kind}. Add a typed extraction method."),
        };
    }

    private static DataValue ExtractFloat32(Array data, long rowIndex)
    {
        if (data is float?[] nf) return nf[rowIndex] is float fv ? DataValue.FromFloat32(fv) : DataValue.Null(DataKind.Float32);
        if (data is float[] f) return DataValue.FromFloat32(f[rowIndex]);
        if (data is double?[] nd) return nd[rowIndex] is double dv ? DataValue.FromFloat32((float)dv) : DataValue.Null(DataKind.Float32);
        return DataValue.FromFloat32(Convert.ToSingle(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractFloat64(Array data, long rowIndex)
    {
        if (data is double?[] nd) return nd[rowIndex] is double dv ? DataValue.FromFloat64(dv) : DataValue.Null(DataKind.Float64);
        if (data is double[] d) return DataValue.FromFloat64(d[rowIndex]);
        if (data is decimal?[] nm) return nm[rowIndex] is decimal mv ? DataValue.FromFloat64((double)mv) : DataValue.Null(DataKind.Float64);
        if (data is float?[] nf) return nf[rowIndex] is float fv ? DataValue.FromFloat64(fv) : DataValue.Null(DataKind.Float64);
        return DataValue.FromFloat64(Convert.ToDouble(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractInt8(Array data, long rowIndex)
    {
        if (data is sbyte?[] n) return n[rowIndex] is sbyte v ? DataValue.FromInt8(v) : DataValue.Null(DataKind.Int8);
        if (data is sbyte[] a) return DataValue.FromInt8(a[rowIndex]);
        return DataValue.FromInt8(Convert.ToSByte(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractInt16(Array data, long rowIndex)
    {
        if (data is short?[] n) return n[rowIndex] is short v ? DataValue.FromInt16(v) : DataValue.Null(DataKind.Int16);
        if (data is short[] a) return DataValue.FromInt16(a[rowIndex]);
        return DataValue.FromInt16(Convert.ToInt16(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractInt32(Array data, long rowIndex)
    {
        if (data is int?[] n) return n[rowIndex] is int v ? DataValue.FromInt32(v) : DataValue.Null(DataKind.Int32);
        if (data is int[] a) return DataValue.FromInt32(a[rowIndex]);
        return DataValue.FromInt32(Convert.ToInt32(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractInt64(Array data, long rowIndex)
    {
        if (data is long?[] n) return n[rowIndex] is long v ? DataValue.FromInt64(v) : DataValue.Null(DataKind.Int64);
        if (data is long[] a) return DataValue.FromInt64(a[rowIndex]);
        return DataValue.FromInt64(Convert.ToInt64(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractUInt8(Array data, long rowIndex)
    {
        if (data is byte?[] n) return n[rowIndex] is byte v ? DataValue.FromUInt8(v) : DataValue.Null(DataKind.UInt8);
        if (data is byte[] a) return DataValue.FromUInt8(a[rowIndex]);
        return DataValue.FromUInt8(Convert.ToByte(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractUInt16(Array data, long rowIndex)
    {
        if (data is ushort?[] n) return n[rowIndex] is ushort v ? DataValue.FromUInt16(v) : DataValue.Null(DataKind.UInt16);
        if (data is ushort[] a) return DataValue.FromUInt16(a[rowIndex]);
        return DataValue.FromUInt16(Convert.ToUInt16(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractUInt32(Array data, long rowIndex)
    {
        if (data is uint?[] n) return n[rowIndex] is uint v ? DataValue.FromUInt32(v) : DataValue.Null(DataKind.UInt32);
        if (data is uint[] a) return DataValue.FromUInt32(a[rowIndex]);
        return DataValue.FromUInt32(Convert.ToUInt32(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractUInt64(Array data, long rowIndex)
    {
        if (data is ulong?[] n) return n[rowIndex] is ulong v ? DataValue.FromUInt64(v) : DataValue.Null(DataKind.UInt64);
        if (data is ulong[] a) return DataValue.FromUInt64(a[rowIndex]);
        return DataValue.FromUInt64(Convert.ToUInt64(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractBoolean(Array data, long rowIndex)
    {
        if (data is bool?[] n) return n[rowIndex] is bool v ? DataValue.FromBoolean(v) : DataValue.Null(DataKind.Boolean);
        if (data is bool[] a) return DataValue.FromBoolean(a[rowIndex]);
        return DataValue.FromBoolean(Convert.ToBoolean(data.GetValue(rowIndex)));
    }

    private static DataValue ExtractString(Array data, long rowIndex, IValueStore store)
    {
        if (data is string?[] s)
        {
            string? v = s[rowIndex];
            if (v is null) return DataValue.Null(DataKind.String);
            return DataValue.FromString(v, store);
        }

        object? element = data.GetValue(rowIndex);
        if (element is null) return DataValue.Null(DataKind.String);
        string str = element.ToString() ?? string.Empty;
        return DataValue.FromString(str, store);
    }

    private static DataValue ExtractDateTime(Array data, long rowIndex)
    {
        if (data is DateTimeOffset?[] ndto)
            return ndto[rowIndex] is DateTimeOffset dto ? DataValue.FromDateTime(dto) : DataValue.Null(DataKind.DateTime);
        if (data is DateTimeOffset[] dtoArr)
            return DataValue.FromDateTime(dtoArr[rowIndex]);
        if (data is DateTime?[] ndt)
            return ndt[rowIndex] is DateTime dt
                ? DataValue.FromDateTime(new DateTimeOffset(dt.ToUniversalTime(), TimeSpan.Zero))
                : DataValue.Null(DataKind.DateTime);
        if (data is DateTime[] dtArr)
            return DataValue.FromDateTime(new DateTimeOffset(dtArr[rowIndex].ToUniversalTime(), TimeSpan.Zero));

        object? element = data.GetValue(rowIndex);
        if (element is null) return DataValue.Null(DataKind.DateTime);
        return DataValue.FromDateTime(element switch
        {
            DateTimeOffset o => o,
            DateTime d => new DateTimeOffset(d.ToUniversalTime(), TimeSpan.Zero),
            _ => new DateTimeOffset(Convert.ToDateTime(element).ToUniversalTime(), TimeSpan.Zero),
        });
    }

    private static DataValue ExtractDate(Array data, long rowIndex)
    {
        if (data is DateOnly?[] ndo)
            return ndo[rowIndex] is DateOnly d ? DataValue.FromDate(d) : DataValue.Null(DataKind.Date);
        if (data is DateOnly[] doArr)
            return DataValue.FromDate(doArr[rowIndex]);
        if (data is DateTime?[] ndt)
            return ndt[rowIndex] is DateTime dt
                ? DataValue.FromDate(DateOnly.FromDateTime(dt))
                : DataValue.Null(DataKind.Date);

        object? element = data.GetValue(rowIndex);
        if (element is null) return DataValue.Null(DataKind.Date);
        return element is DateOnly dateOnly
            ? DataValue.FromDate(dateOnly)
            : DataValue.FromDate(DateOnly.FromDateTime(Convert.ToDateTime(element)));
    }

    private static DataValue ExtractUInt8Array(Array data, long rowIndex, IValueStore store)
    {
        if (data is byte[]?[] ba)
        {
            byte[]? v = ba[rowIndex];
            if (v is null) return DataValue.Null(DataKind.UInt8Array);
            return DataValue.FromUInt8Array(v, store);
        }

        object? element = data.GetValue(rowIndex);
        if (element is null) return DataValue.Null(DataKind.UInt8Array);
        byte[] bytes = (byte[])element;
        return DataValue.FromUInt8Array(bytes, store);
    }

    /// <summary>
    /// Extracts a single element value for list column reconstruction.
    /// Uses the same typed extraction but always from a non-null element.
    /// </summary>
    internal static DataValue ExtractElement(object element, DataKind kind, IValueStore store)
    {
        return kind switch
        {
            DataKind.Float32 => DataValue.FromFloat32(Convert.ToSingle(element)),
            DataKind.Float64 => DataValue.FromFloat64(Convert.ToDouble(element)),
            DataKind.Int8 => DataValue.FromInt8(Convert.ToSByte(element)),
            DataKind.Int16 => DataValue.FromInt16(Convert.ToInt16(element)),
            DataKind.Int32 => DataValue.FromInt32(Convert.ToInt32(element)),
            DataKind.Int64 => DataValue.FromInt64(Convert.ToInt64(element)),
            DataKind.UInt8 => DataValue.FromUInt8(Convert.ToByte(element)),
            DataKind.UInt16 => DataValue.FromUInt16(Convert.ToUInt16(element)),
            DataKind.UInt32 => DataValue.FromUInt32(Convert.ToUInt32(element)),
            DataKind.UInt64 => DataValue.FromUInt64(Convert.ToUInt64(element)),
            DataKind.Boolean => DataValue.FromBoolean(Convert.ToBoolean(element)),
            DataKind.String => DataValue.FromString(element.ToString() ?? string.Empty, store),
            _ => throw new NotSupportedException(
                $"ParquetValueExtractor does not support element kind {kind}. Add a typed extraction case."),
        };
    }
}
