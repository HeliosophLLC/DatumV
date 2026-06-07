using Apache.Arrow;
using Apache.Arrow.Types;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;
using ArrowStructType = Apache.Arrow.Types.StructType;

namespace Heliosoph.DatumV.Export.Arrow;

/// <summary>
/// Per-column accumulator for an Arrow <see cref="StructArray"/>. Owns
/// one inner <see cref="IArrowColumnBuilder"/> per declared field plus
/// the struct-level validity bitmap; at <see cref="Build"/> time it
/// composes the children into a <see cref="StructArray"/>. Mirrors the
/// JSON sink's schema-aware struct writer in shape — fields are named
/// from the projected <see cref="ColumnInfo.Fields"/>, and nested struct
/// children recurse through the same builder factory.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Null handling.</strong> A struct-level null appends a null
/// to every child builder (so child offsets stay aligned) and flips the
/// struct's validity bit to 0. The child reads are skipped by the
/// reader's per-row null check, so the appended child nulls are
/// placeholders the readback never observes.
/// </para>
/// <para>
/// <strong>Reader symmetry.</strong> The reader rebuilds field names
/// from the on-disk Arrow <c>StructType.Fields</c>, so the writer's
/// only job is to emit the right type + child arrays. No
/// <c>datumv.*</c> metadata is needed at the struct level — Arrow's
/// native StructType round-trips cleanly.
/// </para>
/// </remarks>
internal sealed class ArrowStructBuilder : IArrowColumnBuilder
{
    private readonly ColumnInfo _column;
    private readonly IArrowColumnBuilder[] _children;
    private readonly System.Collections.Generic.List<bool> _validity = new();
    private int _nullCount;

    public ArrowStructBuilder(ColumnInfo column, SidecarRegistry? sidecarRegistry)
    {
        _column = column;
        if (column.Fields is not { } fields)
        {
            throw new ExportPlanException(
                $"COPY TO arrow: struct column '{column.Name}' is missing field metadata. " +
                "The format's plan-time rejector should have caught this — please file an issue.");
        }
        _children = new IArrowColumnBuilder[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            _children[i] = ArrowColumnBuilderFactory.Create(fields[i], sidecarRegistry);
        }
    }

    public void Append(DataValue value, IValueStore store)
    {
        if (value.IsNull)
        {
            // Append a null in every child so per-child counts stay
            // aligned with the struct length; the struct validity bit
            // records the real null at this row.
            for (int i = 0; i < _children.Length; i++)
            {
                _children[i].Append(DataValue.Null(_column.Fields![i].Kind), store);
            }
            _validity.Add(false);
            _nullCount++;
            return;
        }

        DataValue[] fieldValues = value.AsStruct(store);
        if (fieldValues.Length != _children.Length)
        {
            throw new ExportRuntimeException(
                $"COPY TO arrow: struct value for column '{_column.Name}' has " +
                $"{fieldValues.Length} fields but the projected schema declares " +
                $"{_children.Length}. The source query's projection drifted from the planner schema.");
        }
        
        for (int i = 0; i < _children.Length; i++)
        {
            _children[i].Append(fieldValues[i], store);
        }
        _validity.Add(true);
    }

    public IArrowArray Build()
    {
        IArrowArray[] childArrays = new IArrowArray[_children.Length];
        Field[] childFields = new Field[_children.Length];
        for (int i = 0; i < _children.Length; i++)
        {
            childArrays[i] = _children[i].Build();
            // Re-derive the child Arrow field so the StructType binds to
            // the same shape the field builder produced for the parent
            // Schema. Cheaper than caching from the field-build phase
            // and keeps this builder self-contained.
            childFields[i] = ArrowFieldBuilder.Build(_column.Fields![i]);
        }

        ArrowBuffer validityBuffer = ArrowBuffer.Empty;
        if (_nullCount > 0)
        {
            ArrowBuffer.BitmapBuilder bitmap = new(_validity.Count);
            for (int i = 0; i < _validity.Count; i++) bitmap.Append(_validity[i]);
            validityBuffer = bitmap.Build();
        }

        ArrowStructType structType = new(childFields);
        ArrayData data = new(
            structType,
            length: _validity.Count,
            nullCount: _nullCount,
            offset: 0,
            buffers: [validityBuffer],
            children: childArrays.Select(c => c.Data).ToArray());
        return new StructArray(data);
    }
}

/// <summary>
/// Per-column accumulator for <c>Array&lt;Struct&gt;</c>. Composes an
/// inner <see cref="ArrowStructBuilder"/> under the standard
/// list-buffer scaffolding (offsets + validity bitmap).
/// </summary>
internal sealed class ArrowListOfStructBuilder : ArrowListBuilderBase
{
    private readonly ColumnInfo _column;
    private readonly ArrowStructBuilder _values;
    public ArrowListOfStructBuilder(ColumnInfo column, SidecarRegistry? sidecarRegistry)
    {
        _column = column;
        _values = new ArrowStructBuilder(column, sidecarRegistry);
    }

    protected override IArrowType ElementArrowType =>
        ((ListType)ArrowFieldBuilder.Build(_column).DataType).ValueDataType;

    protected override int AppendElements(DataValue v, IValueStore store)
    {
        DataValue[] elements = v.AsStructArray(store);
        for (int i = 0; i < elements.Length; i++)
        {
            _values.Append(elements[i], store);
        }
        return elements.Length;
    }

    protected override IArrowArray BuildInnerValues() => _values.Build();
}
