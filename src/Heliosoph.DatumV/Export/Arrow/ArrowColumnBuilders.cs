using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Scalars;
using Apache.Arrow.Types;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions.Json;
using Heliosoph.DatumV.Functions.Scalar.Spatial;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Per-column accumulator for one Arrow <see cref="RecordBatch"/>.
/// Single-use: <see cref="Append"/> rows in order, then call
/// <see cref="Build"/> exactly once. The sink constructs one per column
/// per input batch.
/// </summary>
internal interface IArrowColumnBuilder
{
    void Append(DataValue value, IValueStore store);
    IArrowArray Build();
}

internal static class ArrowColumnBuilderFactory
{
    public static IArrowColumnBuilder Create(ColumnInfo column, SidecarRegistry? sidecarRegistry)
    {
        // Struct routes through the dedicated struct builder regardless
        // of IsArray — the struct + array-of-struct distinction is handled
        // inside the struct builder family because both need recursive
        // child-field handling.
        if (column.Kind == DataKind.Struct)
        {
            return column.IsArray
                ? new ArrowListOfStructBuilder(column, sidecarRegistry)
                : new ArrowStructBuilder(column, sidecarRegistry);
        }
        else if (column.IsArray)
        {
            return ArrowListBuilders.Create(column.Kind, sidecarRegistry);
        }
        
        return CreateScalar(column.Kind, sidecarRegistry);
    }

    private static IArrowColumnBuilder CreateScalar(DataKind kind, SidecarRegistry? sidecarRegistry)
    {
        return kind switch
        {
            DataKind.Boolean => new ArrowBoolBuilder(),
            DataKind.Int8 => new ArrowInt8Builder(),
            DataKind.UInt8 => new ArrowUInt8Builder(),
            DataKind.Int16 => new ArrowInt16Builder(),
            DataKind.UInt16 => new ArrowUInt16Builder(),
            DataKind.Int32 => new ArrowInt32Builder(),
            DataKind.UInt32 => new ArrowUInt32Builder(),
            DataKind.Int64 => new ArrowInt64Builder(),
            DataKind.UInt64 => new ArrowUInt64Builder(),
            DataKind.Float16 or DataKind.Float32 => new ArrowFloat32Builder(),
            DataKind.Float64 => new ArrowFloat64Builder(),
            DataKind.String => new ArrowStringBuilder(sidecarRegistry),
            DataKind.Date => new ArrowDateBuilder(),
            DataKind.Time => new ArrowTimeBuilder(),
            DataKind.Timestamp => new ArrowTimestampBuilder(withTimezone: false),
            DataKind.TimestampTz => new ArrowTimestampBuilder(withTimezone: true),
            DataKind.Decimal => new ArrowDecimalBuilder(),
            DataKind.Uuid => new ArrowUuidAsStringBuilder(),
            DataKind.Duration => new ArrowDurationAsStringBuilder(),
            DataKind.Interval => new ArrowIntervalNativeBuilder(),
            DataKind.Int128 => new ArrowInt128AsStringBuilder(),
            DataKind.UInt128 => new ArrowUInt128AsStringBuilder(),
            DataKind.Json => new ArrowJsonAsStringBuilder(sidecarRegistry),
            DataKind.Image => new ArrowBytesPassthroughBuilder(sidecarRegistry),
            DataKind.Audio => new ArrowBytesPassthroughBuilder(sidecarRegistry),
            DataKind.Video => new ArrowBytesPassthroughBuilder(sidecarRegistry),
            DataKind.Mesh => new ArrowMeshAsGltfBuilder(sidecarRegistry),
            DataKind.PointCloud => new ArrowPointCloudAsPlyBuilder(sidecarRegistry),
            _ => throw new ExportPlanException(
                $"COPY TO arrow: kind {kind} has no scalar builder. " +
                "Surface this case in the format's plan-time rejector."),
        };
    }
}

internal sealed class ArrowBoolBuilder : IArrowColumnBuilder
{
    private readonly BooleanArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsBoolean());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowInt8Builder : IArrowColumnBuilder
{
    private readonly Int8Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsInt8());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUInt8Builder : IArrowColumnBuilder
{
    private readonly UInt8Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsUInt8());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowInt16Builder : IArrowColumnBuilder
{
    private readonly Int16Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsInt16());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUInt16Builder : IArrowColumnBuilder
{
    private readonly UInt16Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsUInt16());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowInt32Builder : IArrowColumnBuilder
{
    private readonly Int32Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsInt32());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUInt32Builder : IArrowColumnBuilder
{
    private readonly UInt32Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsUInt32());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowInt64Builder : IArrowColumnBuilder
{
    private readonly Int64Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsInt64());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUInt64Builder : IArrowColumnBuilder
{
    private readonly UInt64Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsUInt64());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowFloat32Builder : IArrowColumnBuilder
{
    private readonly FloatArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(v.Kind == DataKind.Float16 ? (float)v.AsFloat16() : v.AsFloat32());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowFloat64Builder : IArrowColumnBuilder
{
    private readonly DoubleArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsFloat64());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    private readonly SidecarRegistry? _registry;
    public ArrowStringBuilder(SidecarRegistry? registry) { _registry = registry; }
    public void Append(DataValue v, IValueStore store)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsString(store, _registry));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowDateBuilder : IArrowColumnBuilder
{
    private readonly Date32Array.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(v.AsDate().ToDateTime(TimeOnly.MinValue));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowTimeBuilder : IArrowColumnBuilder
{
    private readonly Time64Array.Builder _b = new(new Time64Type(TimeUnit.Microsecond));
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else
        {
            // TimeOnly.Ticks runs at 100-ns resolution; microseconds = ticks / 10.
            long micros = v.AsTime().Ticks / 10L;
            _b.Append(micros);
        }
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowTimestampBuilder : IArrowColumnBuilder
{
    private readonly TimestampArray.Builder _b;
    private readonly bool _withTimezone;
    public ArrowTimestampBuilder(bool withTimezone)
    {
        _b = new TimestampArray.Builder(TimeUnit.Microsecond);
        _withTimezone = withTimezone;
    }
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) { _b.AppendNull(); return; }
        if (_withTimezone)
        {
            _b.Append(v.AsTimestampTz());
        }
        else
        {
            // Naive timestamp: the .NET builder requires a DateTimeOffset,
            // so wrap the naive DateTime with Zero offset. The
            // TimestampType on the field is built without a timezone so
            // the on-disk value is naive — only the .NET API surface
            // requires the DateTimeOffset hop.
            DateTime dt = v.AsTimestamp();
            _b.Append(new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)));
        }
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowDecimalBuilder : IArrowColumnBuilder
{
    private readonly Decimal128Array.Builder _b = new(new Decimal128Type(precision: 38, scale: 18));
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsDecimal());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUuidAsStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull(); else _b.Append(v.AsUuid().ToString("D"));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowDurationAsStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(v.AsDuration().ToString("c", CultureInfo.InvariantCulture));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowIntervalNativeBuilder : IArrowColumnBuilder
{
    private readonly MonthDayNanosecondIntervalArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) { _b.AppendNull(); return; }
        Interval iv = v.AsInterval();
        // µs → ns at the native Arrow boundary. The 64-bit nanosecond
        // field overflows roughly past ±292 years' worth of
        // microseconds — well outside any realistic interval payload.
        long nanoseconds = checked(iv.Microseconds * 1_000L);
        _b.Append(new MonthDayNanosecondInterval(iv.Months, iv.Days, nanoseconds));
    }
    public IArrowArray Build() => _b.Build(allocator: default);
}

internal sealed class ArrowInt128AsStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(v.AsInt128().ToString(CultureInfo.InvariantCulture));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowUInt128AsStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    public void Append(DataValue v, IValueStore _)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(v.AsUInt128().ToString(CultureInfo.InvariantCulture));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowJsonAsStringBuilder : IArrowColumnBuilder
{
    private readonly StringArray.Builder _b = new();
    private readonly SidecarRegistry? _registry;
    public ArrowJsonAsStringBuilder(SidecarRegistry? registry) { _registry = registry; }
    public void Append(DataValue v, IValueStore store)
    {
        if (v.IsNull) _b.AppendNull();
        else _b.Append(CborJsonCodec.DecodeToJsonText(v.AsByteSpan(store, _registry)));
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowBytesPassthroughBuilder : IArrowColumnBuilder
{
    private readonly BinaryArray.Builder _b = new();
    private readonly SidecarRegistry? _registry;
    public ArrowBytesPassthroughBuilder(SidecarRegistry? registry) { _registry = registry; }
    public void Append(DataValue v, IValueStore store)
    {
        if (v.IsNull) { _b.AppendNull(); return; }
        // BinaryArray.Builder.Append takes byte[]; the span has to be
        // materialised. The bytes are inline either way — the underlying
        // store hands us a span over its buffer — but Arrow's builder
        // wants an owned array to put in its arena.
        _b.Append(v.AsByteSpan(store, _registry).ToArray());
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowMeshAsGltfBuilder : IArrowColumnBuilder
{
    private readonly BinaryArray.Builder _b = new();
    private readonly SidecarRegistry? _registry;
    public ArrowMeshAsGltfBuilder(SidecarRegistry? registry) { _registry = registry; }
    public void Append(DataValue v, IValueStore store)
    {
        if (v.IsNull) { _b.AppendNull(); return; }
        // GltfExporter takes byte[] (not span). The mesh blob is already
        // an owned byte sequence in the arena — materialise once here.
        byte[] gltf = GltfExporter.Export(v.AsByteSpan(store, _registry).ToArray(), "Heliosoph.DatumV");
        _b.Append(gltf);
    }
    public IArrowArray Build() => _b.Build();
}

internal sealed class ArrowPointCloudAsPlyBuilder : IArrowColumnBuilder
{
    private readonly BinaryArray.Builder _b = new();
    private readonly SidecarRegistry? _registry;
    public ArrowPointCloudAsPlyBuilder(SidecarRegistry? registry) { _registry = registry; }
    public void Append(DataValue v, IValueStore store)
    {
        if (v.IsNull) { _b.AppendNull(); return; }
        byte[] ply = PlyExporter.Export(v.AsByteSpan(store, _registry).ToArray(), "Heliosoph.DatumV");
        _b.Append(ply);
    }
    public IArrowArray Build() => _b.Build();
}
