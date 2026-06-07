using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Per-element-kind list-column builders. Each accumulates per-row
/// offsets + a validity bitmap + a flat inner-element buffer, then
/// composes them into an Arrow <see cref="ListArray"/> at
/// <see cref="IArrowColumnBuilder.Build"/> time. Mirrors the manual
/// construction shape <c>OpenArrowFunctionTests</c> uses to set up its
/// fixtures, so what we write is what the reader expects.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Null handling:</strong> a row-level NULL is recorded with a
/// validity bit of 0 and the previous offset repeated, so the inner
/// buffer doesn't grow for null rows. The Arrow reader skips the slot
/// transparently. Per-element NULLs *inside* a present list aren't
/// supported because the engine's <c>AsArraySpan&lt;T&gt;</c> accessors
/// reject them at access time — list-level NULL is the only nullability
/// path in v1.
/// </para>
/// <para>
/// <strong>Validity bitmap</strong> is omitted entirely when every row
/// is non-null (a common case for required-marked array columns), so
/// the resulting Arrow file matches the simpler representation external
/// tools produce by default. The bitmap layout when present is the
/// standard Arrow LSB-first packed-byte form built via
/// <see cref="ArrowBuffer.BitmapBuilder"/>.
/// </para>
/// </remarks>
internal abstract class ArrowListBuilderBase : IArrowColumnBuilder
{
    private readonly List<int> _offsets = new() { 0 };
    private readonly List<bool> _validity = new();
    private int _nullCount;

    public void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            // Repeat the previous offset so the inner buffer doesn't
            // advance; flip the validity bit.
            _offsets.Add(_offsets[^1]);
            _validity.Add(false);
            _nullCount++;
            return;
        }

        int written = AppendElements(value, store);
        _offsets.Add(_offsets[^1] + written);
        _validity.Add(true);
    }

    protected abstract int AppendElements(DataValue value, IValueStore store);
    protected abstract IArrowArray BuildInnerValues();
    protected abstract IArrowType ElementArrowType { get; }

    public IArrowArray Build()
    {
        IArrowArray values = BuildInnerValues();

        ArrowBuffer.Builder<int> offsetsBuilder = new(_offsets.Count);
        for (int i = 0; i < _offsets.Count; i++) offsetsBuilder.Append(_offsets[i]);
        ArrowBuffer offsetsBuffer = offsetsBuilder.Build();

        ArrowBuffer validityBuffer = ArrowBuffer.Empty;
        if (_nullCount > 0)
        {
            ArrowBuffer.BitmapBuilder bitmap = new(_validity.Count);
            for (int i = 0; i < _validity.Count; i++) bitmap.Append(_validity[i]);
            validityBuffer = bitmap.Build();
        }

        return new ListArray(
            new ListType(ElementArrowType),
            _validity.Count,
            offsetsBuffer,
            values,
            validityBuffer,
            _nullCount);
    }
}

internal sealed class ArrowListOfBooleanBuilder : ArrowListBuilderBase
{
    private readonly BooleanArray.Builder _values = new();
    protected override IArrowType ElementArrowType => BooleanType.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<bool> span = v.AsArraySpan<bool>(store);
        foreach (bool b in span) _values.Append(b);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfInt8Builder : ArrowListBuilderBase
{
    private readonly Int8Array.Builder _values = new();
    protected override IArrowType ElementArrowType => Int8Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<sbyte> span = v.AsArraySpan<sbyte>(store);
        foreach (sbyte x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfUInt8Builder : ArrowListBuilderBase
{
    private readonly UInt8Array.Builder _values = new();
    protected override IArrowType ElementArrowType => UInt8Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<byte> span = v.AsArraySpan<byte>(store);
        foreach (byte x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfInt16Builder : ArrowListBuilderBase
{
    private readonly Int16Array.Builder _values = new();
    protected override IArrowType ElementArrowType => Int16Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<short> span = v.AsArraySpan<short>(store);
        foreach (short x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfUInt16Builder : ArrowListBuilderBase
{
    private readonly UInt16Array.Builder _values = new();
    protected override IArrowType ElementArrowType => UInt16Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<ushort> span = v.AsArraySpan<ushort>(store);
        foreach (ushort x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfInt32Builder : ArrowListBuilderBase
{
    private readonly Int32Array.Builder _values = new();
    protected override IArrowType ElementArrowType => Int32Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<int> span = v.AsArraySpan<int>(store);
        foreach (int x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfUInt32Builder : ArrowListBuilderBase
{
    private readonly UInt32Array.Builder _values = new();
    protected override IArrowType ElementArrowType => UInt32Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<uint> span = v.AsArraySpan<uint>(store);
        foreach (uint x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfInt64Builder : ArrowListBuilderBase
{
    private readonly Int64Array.Builder _values = new();
    protected override IArrowType ElementArrowType => Int64Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<long> span = v.AsArraySpan<long>(store);
        foreach (long x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfUInt64Builder : ArrowListBuilderBase
{
    private readonly UInt64Array.Builder _values = new();
    protected override IArrowType ElementArrowType => UInt64Type.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<ulong> span = v.AsArraySpan<ulong>(store);
        foreach (ulong x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfFloat32Builder : ArrowListBuilderBase
{
    private readonly FloatArray.Builder _values = new();
    protected override IArrowType ElementArrowType => FloatType.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<float> span = v.AsArraySpan<float>(store);
        foreach (float x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfFloat64Builder : ArrowListBuilderBase
{
    private readonly DoubleArray.Builder _values = new();
    protected override IArrowType ElementArrowType => DoubleType.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        ReadOnlySpan<double> span = v.AsArraySpan<double>(store);
        foreach (double x in span) _values.Append(x);
        return span.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}

internal sealed class ArrowListOfStringBuilder : ArrowListBuilderBase
{
    private readonly StringArray.Builder _values = new();
    private readonly SidecarRegistry? _registry;
    public ArrowListOfStringBuilder(SidecarRegistry? registry) { _registry = registry; }
    protected override IArrowType ElementArrowType => StringType.Default;
    protected override int AppendElements(DataValue v, IValueStore store)
    {
        string[] arr = v.AsStringArray(store, _registry);
        foreach (string s in arr) _values.Append(s);
        return arr.Length;
    }
    protected override IArrowArray BuildInnerValues() => _values.Build();
}
