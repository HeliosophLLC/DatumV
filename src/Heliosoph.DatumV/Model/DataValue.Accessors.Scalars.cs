using System.Numerics;
using System.Runtime.CompilerServices;

namespace Heliosoph.DatumV.Model;

public readonly partial struct DataValue
{
    // ───────────────────────── Numeric scalar accessors ─────────────────────────

    /// <summary>Returns the 32-bit floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public float AsFloat32()
    {
        ThrowIfNullOrWrongKind(DataKind.Float32);
        return BitConverter.Int32BitsToSingle(_p0);
    }

    /// <summary>Returns the unsigned 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public byte AsUInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt8);
        return (byte)_p0;
    }

    /// <summary>Returns the signed 8-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public sbyte AsInt8()
    {
        ThrowIfNullOrWrongKind(DataKind.Int8);
        return (sbyte)_p0;
    }

    /// <summary>Returns the signed 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public short AsInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.Int16);
        return (short)_p0;
    }

    /// <summary>Returns the unsigned 16-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ushort AsUInt16()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt16);
        return (ushort)_p0;
    }

    /// <summary>Returns the signed 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public int AsInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.Int32);
        return _p0;
    }

    /// <summary>Returns the unsigned 32-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public uint AsUInt32()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt32);
        return unchecked((uint)_p0);
    }

    /// <summary>Returns the signed 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public long AsInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.Int64);
        return ReadLong();
    }

    /// <summary>Returns the unsigned 64-bit integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public ulong AsUInt64()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt64);
        return unchecked((ulong)ReadLong());
    }

    /// <summary>Returns the 64-bit double-precision floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public double AsFloat64()
    {
        ThrowIfNullOrWrongKind(DataKind.Float64);
        return BitConverter.Int64BitsToDouble(ReadLong());
    }

    /// <summary>Returns the 16-bit IEEE 754 binary16 floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Half AsFloat16()
    {
        ThrowIfNullOrWrongKind(DataKind.Float16);
        return BitConverter.UInt16BitsToHalf((ushort)_p0);
    }

    /// <summary>Returns the 128-bit decimal floating-point payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public decimal AsDecimal()
    {
        ThrowIfNullOrWrongKind(DataKind.Decimal);
        return new decimal([_p0, _p1, _p2, _p3]);
    }

    /// <summary>Returns the 128-bit unsigned integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public UInt128 AsUInt128()
    {
        ThrowIfNullOrWrongKind(DataKind.UInt128);
        return Unsafe.As<int, UInt128>(ref Unsafe.AsRef(in _p0));
    }

    /// <summary>Returns the 128-bit signed integer payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Int128 AsInt128()
    {
        ThrowIfNullOrWrongKind(DataKind.Int128);
        return Unsafe.As<int, Int128>(ref Unsafe.AsRef(in _p0));
    }

    /// <summary>Returns the 2D point payload as a <see cref="Vector2"/>.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Vector2 AsPoint2D()
    {
        ThrowIfNullOrWrongKind(DataKind.Point2D);
        return new Vector2(
            BitConverter.Int32BitsToSingle(_p0),
            BitConverter.Int32BitsToSingle(_p1));
    }

    /// <summary>Returns the 3D point payload as a <see cref="Vector3"/>.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Vector3 AsPoint3D()
    {
        ThrowIfNullOrWrongKind(DataKind.Point3D);
        return new Vector3(
            BitConverter.Int32BitsToSingle(_p0),
            BitConverter.Int32BitsToSingle(_p1),
            BitConverter.Int32BitsToSingle(_p2));
    }

    /// <summary>
    /// Returns the <see cref="DataKind.VideoFrame"/> handle's inline payload as a
    /// <c>(VideoId, FrameIndex)</c> tuple. The consumer routes the pair through the
    /// per-query video registry to obtain pixel bytes.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public (uint VideoId, int FrameIndex) AsVideoFrame()
    {
        ThrowIfNullOrWrongKind(DataKind.VideoFrame);
        return (unchecked((uint)_p0), _p1);
    }

    // ─────────────────────── Widening numeric conversions ───────────────────────

    /// <summary>
    /// Returns <see langword="true"/> when this value's <see cref="Kind"/> is any integer,
    /// floating-point, or boolean scalar that can be widened to <see cref="float"/> or <see cref="double"/>.
    /// Boolean values are treated as 1 (true) and 0 (false).
    /// </summary>
    public bool IsNumericScalar => IsNumericScalarKind(_kind);

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer,
    /// floating-point, or boolean scalar that can be converted to a numeric value.
    /// </summary>
    public static bool IsNumericScalarKind(DataKind kind) =>
        kind is DataKind.Float16 or DataKind.Float32 or DataKind.Float64 or DataKind.Decimal
            or DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64 or DataKind.Int128
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64 or DataKind.UInt128
            or DataKind.Boolean;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="kind"/> is any integer type
    /// (signed or unsigned). Excludes floating-point and boolean kinds.
    /// Use for function parameters that are logically integer (positions, counts, indices).
    /// </summary>
    public static bool IsIntegerKind(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64
            or DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64;

    /// <summary>
    /// Widens any numeric scalar value to <see cref="float"/>.
    /// Int64/UInt64 values may lose precision beyond 2^24.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public float ToFloat()
    {
        if (TryToFloat(out float result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to float.");
    }

    /// <summary>
    /// Widens any numeric scalar value to <see cref="double"/>.
    /// UInt64 values may lose precision beyond 2^53.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public double ToDouble()
    {
        if (TryToDouble(out double result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to double.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="int"/>.
    /// Floating-point values are truncated. Values outside the int range overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public int ToInt32()
    {
        if (TryToInt32(out int result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to int.");
    }

    /// <summary>
    /// Converts any numeric scalar value to <see cref="long"/>.
    /// Floating-point values are truncated. UInt64 values beyond <see cref="long.MaxValue"/> overflow silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The value is null or not a numeric scalar kind.</exception>
    public long ToInt64()
    {
        if (TryToInt64(out long result)) return result;
        throw new InvalidOperationException($"Cannot convert {_kind} to long.");
    }

    /// <summary>Attempts to widen this value to <see cref="float"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToFloat(out float result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = (float)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = AsUInt64(); return true;
            case DataKind.Int128:  result = (float)AsInt128(); return true;
            case DataKind.UInt128: result = (float)AsUInt128(); return true;
            case DataKind.Float16: result = (float)AsFloat16(); return true;
            case DataKind.Decimal: result = (float)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1f : 0f; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to widen this value to <see cref="double"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToDouble(out double result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = AsFloat32(); return true;
            case DataKind.Float64: result = AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (double)AsUInt64(); return true;
            case DataKind.Int128:  result = (double)AsInt128(); return true;
            case DataKind.UInt128: result = (double)AsUInt128(); return true;
            case DataKind.Float16: result = (double)AsFloat16(); return true;
            case DataKind.Decimal: result = (double)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1.0 : 0.0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="int"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt32(out int result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (int)AsFloat32(); return true;
            case DataKind.Float64: result = (int)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = (int)AsUInt32(); return true;
            case DataKind.Int64:   result = (int)AsInt64(); return true;
            case DataKind.UInt64:  result = (int)AsUInt64(); return true;
            case DataKind.Int128:  result = (int)AsInt128(); return true;
            case DataKind.UInt128: result = (int)AsUInt128(); return true;
            case DataKind.Float16: result = (int)AsFloat16(); return true;
            case DataKind.Decimal: result = (int)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1 : 0; return true;
            default: result = default; return false;
        }
    }

    /// <summary>Attempts to convert this value to <see cref="long"/>. Returns <see langword="false"/> for non-numeric kinds or null values.</summary>
    public bool TryToInt64(out long result)
    {
        if (IsNull) { result = default; return false; }
        switch (_kind)
        {
            case DataKind.Float32: result = (long)AsFloat32(); return true;
            case DataKind.Float64: result = (long)AsFloat64(); return true;
            case DataKind.UInt8:   result = AsUInt8(); return true;
            case DataKind.Int8:    result = AsInt8(); return true;
            case DataKind.Int16:   result = AsInt16(); return true;
            case DataKind.UInt16:  result = AsUInt16(); return true;
            case DataKind.Int32:   result = AsInt32(); return true;
            case DataKind.UInt32:  result = AsUInt32(); return true;
            case DataKind.Int64:   result = AsInt64(); return true;
            case DataKind.UInt64:  result = (long)AsUInt64(); return true;
            case DataKind.Int128:  result = (long)AsInt128(); return true;
            case DataKind.UInt128: result = (long)AsUInt128(); return true;
            case DataKind.Float16: result = (long)AsFloat16(); return true;
            case DataKind.Decimal: result = (long)AsDecimal(); return true;
            case DataKind.Boolean: result = AsBoolean() ? 1L : 0L; return true;
            default: result = default; return false;
        }
    }

    // ─────────────────────── Date / time / uuid / bool / type ─────────────────────

    /// <summary>Returns the calendar date payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateOnly AsDate()
    {
        ThrowIfNullOrWrongKind(DataKind.Date);
        return DateOnly.FromDayNumber(_p0);
    }

    /// <summary>
    /// Returns the UTC timestamp payload (PG <c>timestamptz</c>). The returned
    /// <see cref="DateTimeOffset"/> always carries an offset of
    /// <see cref="TimeSpan.Zero"/> — input offsets were discarded at
    /// construction (see <see cref="FromTimestampTz"/>).
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTimeOffset AsTimestampTz()
    {
        ThrowIfNullOrWrongKind(DataKind.TimestampTz);
        return new DateTimeOffset(ReadLong(), TimeSpan.Zero);
    }

    /// <summary>
    /// Returns the naive timestamp payload (PG <c>timestamp</c>) as a
    /// <see cref="DateTime"/> with <see cref="DateTimeKind.Unspecified"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DateTime AsTimestamp()
    {
        ThrowIfNullOrWrongKind(DataKind.Timestamp);
        return new DateTime(ReadLong(), DateTimeKind.Unspecified);
    }

    /// <summary>Returns the UUID payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Guid AsUuid()
    {
        ThrowIfNullOrWrongKind(DataKind.Uuid);
        return Unsafe.As<int, Guid>(ref Unsafe.AsRef(in _p0));
    }

    /// <summary>Returns the boolean payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public bool AsBoolean()
    {
        ThrowIfNullOrWrongKind(DataKind.Boolean);
        return _p0 != 0;
    }

    /// <summary>Returns the time-of-day payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeOnly AsTime()
    {
        ThrowIfNullOrWrongKind(DataKind.Time);
        return new TimeOnly(ReadLong());
    }

    /// <summary>Returns the duration payload.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public TimeSpan AsDuration()
    {
        ThrowIfNullOrWrongKind(DataKind.Duration);
        return new TimeSpan(ReadLong());
    }

    /// <summary>
    /// Returns the Postgres <see cref="Interval"/> payload. Months ride in
    /// <c>_p0</c>, days in <c>_p1</c>, microseconds across <c>_p2</c>+<c>_p3</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public Interval AsInterval()
    {
        ThrowIfNullOrWrongKind(DataKind.Interval);
        long micros = (uint)_p2 | ((long)_p3 << 32);
        return new Interval(_p0, _p1, micros);
    }

    /// <summary>Returns the <see cref="DataKind"/> that this type tag describes.</summary>
    /// <exception cref="InvalidOperationException">Wrong kind or null.</exception>
    public DataKind AsType()
    {
        ThrowIfNullOrWrongKind(DataKind.Type);
        return (DataKind)(byte)_p0;
    }
}
