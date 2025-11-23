using DatumIngest.Model;

namespace DatumIngest.DatumFile.V2.Encoding;

/// <summary>
/// Per-column-page zone-map accumulator. Records each appended
/// <see cref="DataValue"/> into running null-count / min / max state and
/// emits a <see cref="DatumZoneMap"/> on <see cref="Build"/>. One instance
/// per column-page pair; the writer resets between pages.
/// </summary>
/// <remarks>
/// Min/max are captured only for comparable kinds. Non-comparable kinds
/// (Vector, Image, byte arrays, Array, Struct)
/// produce a zone map with <see cref="DatumZoneMap.NullCount"/> only —
/// callers can still prune on null count alone.
/// </remarks>
internal sealed class PageZoneMapBuilderV2
{
    private uint _nullCount;
    private object? _min;
    private object? _max;
    private DataKind _kind = DataKind.Unknown;

    /// <summary>Resets state so the same builder can be reused across pages.</summary>
    public void Reset()
    {
        _nullCount = 0;
        _min = null;
        _max = null;
        _kind = DataKind.Unknown;
    }

    /// <summary>Records a null cell.</summary>
    public void RecordNull() => _nullCount++;

    /// <summary>
    /// Records a non-null cell. <paramref name="store"/> is consulted only
    /// for arena-backed strings; pass any non-null store when string
    /// columns are in scope (the FixedWidth/BitPackedBoolean encoders
    /// can pass <see langword="null"/> because their kinds never need it).
    /// </summary>
    public void Record(DataValue value, IValueStore? store)
    {
        if (value.IsNull)
        {
            _nullCount++;
            return;
        }

        object? boxed = ExtractComparable(value, store);
        if (boxed is null)
        {
            return;
        }

        if (_min is null)
        {
            _min = boxed;
            _max = boxed;
            _kind = value.Kind;
            return;
        }

        if (((IComparable)boxed).CompareTo(_min) < 0) _min = boxed;
        if (((IComparable)boxed).CompareTo(_max) > 0) _max = boxed;
    }

    /// <summary>Builds the zone map for the current page.</summary>
    public DatumZoneMap Build()
    {
        if (_min is null || _max is null)
        {
            return new DatumZoneMap(_nullCount);
        }
        return new DatumZoneMap(_nullCount, _kind, _min, _max);
    }

    private static object? ExtractComparable(DataValue v, IValueStore? store)
    {
        // Typed-array columns have no scalar min/max. Skip before the kind
        // dispatch so e.g. UInt8 + IsArray doesn't fall into the UInt8 arm.
        if (v.IsArray)
        {
            return null;
        }

        return v.Kind switch
        {
            DataKind.Int8 => (object)v.AsInt8(),
            DataKind.UInt8 => v.AsUInt8(),
            DataKind.Int16 => v.AsInt16(),
            DataKind.UInt16 => v.AsUInt16(),
            DataKind.Int32 => v.AsInt32(),
            DataKind.UInt32 => v.AsUInt32(),
            DataKind.Int64 => v.AsInt64(),
            DataKind.UInt64 => v.AsUInt64(),
            DataKind.Float32 => v.AsFloat32(),
            DataKind.Float64 => v.AsFloat64(),
            DataKind.Boolean => v.AsBoolean(),
            DataKind.Date => v.AsDate(),
            DataKind.Time => v.AsTime(),
            DataKind.DateTime => v.AsDateTime(),
            DataKind.Duration => v.AsDuration(),
            DataKind.Uuid => v.AsUuid(),
            DataKind.String when store is not null => v.AsString(store),
            // Vector / Image / byte arrays / Array / Struct / Type / Unknown —
            // non-comparable.
            _ => null,
        };
    }
}
