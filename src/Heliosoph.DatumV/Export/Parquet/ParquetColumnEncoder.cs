using Heliosoph.DatumV.Model;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace Heliosoph.DatumV.Export.Parquet;

/// <summary>
/// Per-column accumulator that buffers row values until the sink flushes a
/// row group, then materialises them as a typed <see cref="DataColumn"/>.
/// One <see cref="ParquetColumnEncoder"/> per output column for the lifetime
/// of an <see cref="ParquetExportSink"/>.
/// </summary>
internal abstract class ParquetColumnEncoder
{
    /// <summary>Parquet field metadata — name, CLR type, nullability, array flag.</summary>
    public abstract DataField Field { get; }

    /// <summary>Append a single <see cref="DataValue"/> to the column buffer.</summary>
    public abstract void Append(DataValue value, IValueStore store);

    /// <summary>
    /// Materialise the accumulated rows as a <see cref="DataColumn"/> and
    /// hand it to <paramref name="rg"/>. The buffer is reset afterwards so
    /// the encoder is ready for the next row group.
    /// </summary>
    public abstract Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken);

    /// <summary>Number of rows currently buffered.</summary>
    public abstract int Count { get; }

    /// <summary>
    /// Returns the encoder that <paramref name="column"/> requires. Throws
    /// <see cref="ExportPlanException"/> for kinds the v1 Parquet sink does
    /// not yet implement; the planner mirrors the supported set so this is
    /// only hit by genuinely-unsupported columns.
    /// </summary>
    public static ParquetColumnEncoder Create(ColumnInfo column)
    {
        if (column.IsArray)
        {
            throw new ExportPlanException(
                $"COPY TO parquet: column '{column.Name}' is an array. Array columns are not " +
                "supported in the v1 Parquet sink. (Coming in a follow-up.)");
        }

        string name = column.Name;
        bool nullable = column.Nullable;

        return column.Kind switch
        {
            DataKind.Boolean => nullable
                ? new NullableValueTypeEncoder<bool>(name, static (v, _) => v.AsBoolean())
                : new ValueTypeEncoder<bool>(name, static (v, _) => v.AsBoolean()),

            DataKind.Int8 => nullable
                ? new NullableValueTypeEncoder<sbyte>(name, static (v, _) => v.AsInt8())
                : new ValueTypeEncoder<sbyte>(name, static (v, _) => v.AsInt8()),

            DataKind.Int16 => nullable
                ? new NullableValueTypeEncoder<short>(name, static (v, _) => v.AsInt16())
                : new ValueTypeEncoder<short>(name, static (v, _) => v.AsInt16()),

            DataKind.Int32 => nullable
                ? new NullableValueTypeEncoder<int>(name, static (v, _) => v.AsInt32())
                : new ValueTypeEncoder<int>(name, static (v, _) => v.AsInt32()),

            DataKind.Int64 => nullable
                ? new NullableValueTypeEncoder<long>(name, static (v, _) => v.AsInt64())
                : new ValueTypeEncoder<long>(name, static (v, _) => v.AsInt64()),

            DataKind.UInt8 => nullable
                ? new NullableValueTypeEncoder<byte>(name, static (v, _) => v.AsUInt8())
                : new ValueTypeEncoder<byte>(name, static (v, _) => v.AsUInt8()),

            DataKind.UInt16 => nullable
                ? new NullableValueTypeEncoder<ushort>(name, static (v, _) => v.AsUInt16())
                : new ValueTypeEncoder<ushort>(name, static (v, _) => v.AsUInt16()),

            DataKind.UInt32 => nullable
                ? new NullableValueTypeEncoder<uint>(name, static (v, _) => v.AsUInt32())
                : new ValueTypeEncoder<uint>(name, static (v, _) => v.AsUInt32()),

            DataKind.UInt64 => nullable
                ? new NullableValueTypeEncoder<ulong>(name, static (v, _) => v.AsUInt64())
                : new ValueTypeEncoder<ulong>(name, static (v, _) => v.AsUInt64()),

            DataKind.Float32 => nullable
                ? new NullableValueTypeEncoder<float>(name, static (v, _) => v.AsFloat32())
                : new ValueTypeEncoder<float>(name, static (v, _) => v.AsFloat32()),

            DataKind.Float64 => nullable
                ? new NullableValueTypeEncoder<double>(name, static (v, _) => v.AsFloat64())
                : new ValueTypeEncoder<double>(name, static (v, _) => v.AsFloat64()),

            // Strings are inherently reference-typed and always treated as nullable
            // by Parquet.Net's DataField<string>. SQL nullability is honoured at
            // append time — a NULL DataValue emits a CLR null into the column.
            DataKind.String => new ReferenceTypeEncoder<string>(
                name, static (v, store) => v.AsString(store)),

            // Image bytes flow into Parquet as raw BYTE_ARRAY (DataField<byte[]>).
            // This is the Inline media disposition; sidecar mode is reserved for
            // a follow-up that adds the Directory target.
            DataKind.Image => new ReferenceTypeEncoder<byte[]>(
                name, static (v, store) => v.AsImage(store)),

            _ => throw new ExportPlanException(
                $"COPY TO parquet: column '{column.Name}' has kind {column.Kind}, which the v1 " +
                "Parquet sink does not yet encode. (Supported v1 kinds: Boolean, Int8/16/32/64, " +
                "UInt8/16/32/64, Float32/64, String, Image.)"),
        };
    }
}

/// <summary>
/// Non-nullable value-type column encoder. Buffers values directly in a
/// <c>List&lt;T&gt;</c>; flushes by materialising a typed array.
/// </summary>
internal sealed class ValueTypeEncoder<T> : ParquetColumnEncoder where T : struct
{
    private readonly DataField<T> _field;
    private readonly List<T> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;

    public ValueTypeEncoder(string name, Func<DataValue, IValueStore, T> extract)
    {
        _field = new DataField<T>(name);
        _extract = extract;
    }

    public override DataField Field => _field;
    public override int Count => _buffer.Count;

    public override void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            // The planner gates non-nullable columns; a runtime null here means the
            // source query produced one anyway. Surface a clear runtime error.
            throw new ExportRuntimeException(
                $"COPY TO parquet: column '{_field.Name}' is non-nullable but the source " +
                "query produced a NULL value.");
        }
        _buffer.Add(_extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T[] arr = _buffer.ToArray();
        _buffer.Clear();
        await rg.WriteColumnAsync(new DataColumn(_field, arr), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Nullable value-type column encoder. Buffers values as <c>T?</c>, emits
/// the <c>T?[]</c> array Parquet.Net wires up for nullable primitive
/// columns.
/// </summary>
internal sealed class NullableValueTypeEncoder<T> : ParquetColumnEncoder where T : struct
{
    private readonly DataField<T?> _field;
    private readonly List<T?> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;

    public NullableValueTypeEncoder(string name, Func<DataValue, IValueStore, T> extract)
    {
        _field = new DataField<T?>(name);
        _extract = extract;
    }

    public override DataField Field => _field;
    public override int Count => _buffer.Count;

    public override void Append(DataValue value, IValueStore store)
    {
        _buffer.Add(value.IsNull ? null : _extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T?[] arr = _buffer.ToArray();
        _buffer.Clear();
        await rg.WriteColumnAsync(new DataColumn(_field, arr), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Reference-type column encoder (string, byte[]). Reference types are
/// inherently nullable in CLR; a SQL NULL maps to a CLR null in the buffer.
/// </summary>
internal sealed class ReferenceTypeEncoder<T> : ParquetColumnEncoder where T : class
{
    private readonly DataField<T> _field;
    private readonly List<T?> _buffer = new();
    private readonly Func<DataValue, IValueStore, T> _extract;

    public ReferenceTypeEncoder(string name, Func<DataValue, IValueStore, T> extract)
    {
        _field = new DataField<T>(name);
        _extract = extract;
    }

    public override DataField Field => _field;
    public override int Count => _buffer.Count;

    public override void Append(DataValue value, IValueStore store)
    {
        _buffer.Add(value.IsNull ? null : _extract(value, store));
    }

    public override async Task FlushAsync(ParquetRowGroupWriter rg, CancellationToken cancellationToken)
    {
        if (_buffer.Count == 0) return;
        T?[] arr = _buffer.ToArray();
        _buffer.Clear();
        await rg.WriteColumnAsync(new DataColumn(_field, arr), cancellationToken).ConfigureAwait(false);
    }
}
