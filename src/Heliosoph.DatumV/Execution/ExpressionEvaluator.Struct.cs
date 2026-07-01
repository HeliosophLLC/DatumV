using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Functions.Image;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

public sealed partial class ExpressionEvaluator
{
    // ───────────────── Struct and index-access evaluation ─────────────────

    private async ValueTask<DataValue> EvaluateStructLiteralAsync(
        StructLiteralExpression literal, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue[] fields = new DataValue[literal.Fields.Count];
        for (int index = 0; index < literal.Fields.Count; index++)
        {
            fields[index] = await EvaluateAsync(literal.Fields[index].Value, frame, cancellationToken).ConfigureAwait(false);
        }

        ushort typeId = 0;
        if (_typeRegistry is not null)
        {
            var fieldDescriptors = new StructFieldDescriptor[literal.Fields.Count];
            for (int i = 0; i < literal.Fields.Count; i++)
            {
                int fieldTypeId = _typeRegistry.InternScalarType(fields[i].Kind);
                fieldDescriptors[i] = new StructFieldDescriptor(literal.Fields[i].Name, fieldTypeId);
            }
            typeId = (ushort)_typeRegistry.InternStructType(fieldDescriptors);
        }

        return DataValue.FromStruct(fields, frame.Target, typeId);
    }

    /// <summary>
    /// Whether <paramref name="kind"/> can be used as a positional ordinal in
    /// <c>struct[i]</c> indexing. Mirrors <see cref="DataValue.TryToFloat(out float)"/>'s
    /// supported numeric kinds — the implementation funnels through
    /// <see cref="ToFloat"/>, so the check must accept every kind that helper
    /// recognises (otherwise small numeric literals like <c>1</c>, parsed as
    /// <see cref="DataKind.Int8"/>, fall through to the named-field path and
    /// trip a String-only conversion).
    /// </summary>
    private static bool IsPositionalIndexKind(DataKind kind) => kind is
        DataKind.Int8 or DataKind.UInt8
        or DataKind.Int16 or DataKind.UInt16
        or DataKind.Int32 or DataKind.UInt32
        or DataKind.Int64 or DataKind.UInt64
        or DataKind.Int128 or DataKind.UInt128
        or DataKind.Float16 or DataKind.Float32 or DataKind.Float64
        or DataKind.Decimal;

    private async ValueTask<DataValue> EvaluateIndexAccessAsync(
        IndexAccessExpression indexAccess, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        DataValue source = await EvaluateAsync(indexAccess.Source, frame, cancellationToken).ConfigureAwait(false);
        return await EvaluateIndexAccessCoreAsync(source, indexAccess, frame, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// ValueRef-native index access. The point of this path is the managed-
    /// struct named-field fast path: pull a field's <see cref="ValueRef"/>
    /// straight out of the struct's <c>ValueRef[]</c> payload, leaving every
    /// sibling field untouched. Lowering to the DataValue path instead
    /// <c>ToDataValue</c>'s the WHOLE struct into the arena just to read one
    /// field — so reading a 16-byte scalar off a struct that also carries a
    /// multi-MB array copies the array too, every time. In a loop that reads
    /// two fields off a model-output struct per iteration, that redundant
    /// copying is the dominant arena cost.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow: single string index, non-null <em>managed</em>
    /// struct (its payload is a <c>ValueRef[]</c>), and a field name resolvable
    /// via the per-query <see cref="TypeRegistry"/>. Positional access, arrays,
    /// arena/sidecar-backed structs, and untyped structs fall through to the
    /// DataValue dispatch — the source is evaluated once and reused, so the
    /// fallback never re-runs the source expression.
    /// </remarks>
    private async ValueTask<ValueRef> EvaluateIndexAccessAsValueRefAsync(
        IndexAccessExpression indexAccess, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef sourceRef = await EvaluateAsValueRefAsync(indexAccess.Source, frame, cancellationToken).ConfigureAwait(false);

        if (!sourceRef.IsNull
            && sourceRef.Kind == DataKind.Struct
            && sourceRef.Materialized is ValueRef[]
            && indexAccess.Indices.Count == 1
            && _typeRegistry is not null
            && sourceRef.TypeId != 0)
        {
            ValueRef indexRef = await EvaluateAsValueRefAsync(indexAccess.Indices[0], frame, cancellationToken).ConfigureAwait(false);
            if (indexRef.Kind == DataKind.String && !indexRef.IsNull)
            {
                int idx = _typeRegistry.GetDescriptor(sourceRef.TypeId)?.FindFieldIndex(indexRef.AsString()) ?? -1;
                if (idx >= 0)
                {
                    ReadOnlySpan<ValueRef> fields = sourceRef.GetStructFields();
                    if (idx < fields.Length)
                    {
                        return fields[idx];
                    }
                }
            }
            // Field not resolvable via the registry (or out of range) — fall
            // through to the authoritative DataValue dispatch below.
        }

        // Fallback: materialise the already-evaluated source once and run the
        // full dispatch (arrays, positional access, arena structs, untyped
        // structs, LET/schema field resolution).
        DataValue source = sourceRef.ToDataValue(frame.Source, sourceRef.TypeId, frame.Types);
        DataValue raw = await EvaluateIndexAccessCoreAsync(source, indexAccess, frame, cancellationToken).ConfigureAwait(false);
        return ToValueRef(raw, frame);
    }

    /// <summary>
    /// Index-access dispatch given an already-evaluated <paramref name="source"/>
    /// (array element / struct field). Split out from
    /// <see cref="EvaluateIndexAccessAsync"/> so the ValueRef-native fast path
    /// can reuse the full dispatch without re-evaluating the source expression.
    /// </summary>
    private async ValueTask<DataValue> EvaluateIndexAccessCoreAsync(
        DataValue source, IndexAccessExpression indexAccess, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        if (source.IsNull)
        {
            return source;
        }

        IReadOnlyList<Expression> indices = indexAccess.Indices;

        if (source.IsArray)
        {
            // Multi-index (multi-dim) access: arr[i, j, ...]. Validate the index
            // count matches the array's dimensionality and compute a flat row-major
            // offset. Single-index (the common path) falls through unchanged.
            if (indices.Count > 1)
            {
                if (!source.IsMultiDim)
                {
                    throw new InvalidOperationException(
                        $"Array indexing with {indices.Count} indices requires a multi-dim array; " +
                        $"got a 1-D Array<{source.Kind}>.");
                }
                // Materialise the shape span to an array so it can survive across
                // the per-index await calls (Spans can't cross await boundaries).
                int[] shape = source.GetShape(frame.Source, frame.SidecarRegistry).ToArray();
                if (indices.Count != shape.Length)
                {
                    throw new InvalidOperationException(
                        $"Array indexing expected {shape.Length} indices (ndim) but {indices.Count} were supplied.");
                }
                long flatOffset = 0;
                for (int i = 0; i < indices.Count; i++)
                {
                    DataValue idxValue = await EvaluateAsync(indices[i], frame, cancellationToken).ConfigureAwait(false);
                    if (!IsPositionalIndexKind(idxValue.Kind))
                    {
                        throw new InvalidOperationException(
                            $"Multi-dim array index {i} must be numeric; got {idxValue.Kind}.");
                    }
                    // PostgreSQL-style 1-based indices: shift to 0-based internal offset.
                    int dimIndex = (int)ToFloat(idxValue) - 1;
                    int dimSize = shape[i];
                    if (dimIndex < 0 || dimIndex >= dimSize)
                    {
                        return DataValue.Null(source.Kind);
                    }
                    flatOffset = flatOffset * dimSize + dimIndex;
                }
                return ReadTypedArrayElement(source, (int)flatOffset, frame);
            }

            // Single-index path: integer position for typed arrays, string is a misuse.
            // Reject single-index access against a multi-dim source — we don't support
            // slicing (returning sub-arrays), only scalar element access.
            if (source.IsMultiDim)
            {
                throw new InvalidOperationException(
                    $"Array indexing on a {source.Ndim}-dimensional Array<{source.Kind}> requires " +
                    $"{source.Ndim} indices; only 1 was supplied. Slicing (returning a sub-array) is not supported.");
            }
            DataValue index = await EvaluateAsync(indices[0], frame, cancellationToken).ConfigureAwait(false);
            if (index.Kind == DataKind.String)
            {
                throw new InvalidOperationException(
                    $"Named field access ('{Str(index, frame)}') is not supported on Array<{source.Kind}> — " +
                    $"use positional destructuring: LET (a, b, ...) = expr.");
            }

            // PostgreSQL-style 1-based indexing: arr[1] is the first element.
            int position = (int)ToFloat(index) - 1;
            return ReadTypedArrayElement(source, position, frame);
        }

        if (source.Kind == DataKind.Struct)
        {
            if (indices.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Struct field access takes exactly one index; got {indices.Count}.");
            }
            DataValue index = await EvaluateAsync(indices[0], frame, cancellationToken).ConfigureAwait(false);
            // Integer / float index → positional (ordinal) access by declaration
            // order. Numeric literals like @row[1] parse to the narrowest type
            // that fits (Int8 for small values), so the kind check covers every
            // numeric kind TryToFloat handles rather than only Int32/Int64.
            if (IsPositionalIndexKind(index.Kind))
            {
                DataValue[] fields = source.AsStruct(frame.Source);
                // PostgreSQL-style 1-based ordinal: struct[1] is the first field,
                // mirroring the array bracket accessor.
                int position = (int)ToFloat(index) - 1;
                if (position < 0 || position >= fields.Length)
                {
                    return DataValue.NullStruct(source.TypeId);
                }
                return fields[position];
            }

            // String index → named field access.
            return EvaluateStructFieldAccess(source, index, indexAccess, frame);
        }

        if (source.Kind == DataKind.Point2D)
        {
            throw new InvalidOperationException(
                $"Index access is not supported on Point2D values. Use the point_x / point_y accessors instead.");    
        }
        else if (source.Kind == DataKind.Point3D)
        {
            throw new InvalidOperationException(
                $"Index access is not supported on Point3D values. Use the point_x / point_y / point_z accessors instead.");
        }

        throw new InvalidOperationException(
            $"Index access is not supported on {source.Kind} values.");
    }

    /// <summary>
    /// Reads a single element from a typed array (<c>Kind=elementKind + IsArray</c>)
    /// at <paramref name="position"/> and returns it as a freshly-built scalar
    /// <see cref="DataValue"/>. Dispatches by element kind across the typed-
    /// array accessors (<see cref="DataValue.AsStringArray"/> /
    /// <see cref="DataValue.AsImageArray"/> / <see cref="DataValue.AsStructArray"/>
    /// for reference kinds, <see cref="DataValue.AsArraySpan{T}"/> for
    /// fixed-width primitives). Out-of-range returns a typed null of the
    /// element kind.
    /// </summary>
    /// <remarks>
    /// Reading the i-th element via the bulk accessor (e.g. AsStringArray
    /// reads all elements) is wasteful for single-index access. Future
    /// optimisation: per-kind GetArrayElementAt(position) accessors that read
    /// one slot's bytes via the slot block. Punted because there are no
    /// repeated-index-access hotspots today.
    /// </remarks>
    private DataValue ReadTypedArrayElement(DataValue source, int position, EvaluationFrame frame)
    {
        DataKind elementKind = source.Kind;
        switch (elementKind)
        {
            case DataKind.String:
            {
                string[] elements = source.AsStringArray(frame.Source, frame.SidecarRegistry);
                if (position < 0 || position >= elements.Length)
                {
                    return DataValue.Null(DataKind.String);
                }
                return DataValue.FromString(elements[position], frame.Target);
            }
            case DataKind.Image:
            {
                byte[][] elements = source.AsImageArray(frame.Source, frame.SidecarRegistry);
                if (position < 0 || position >= elements.Length)
                {
                    return DataValue.Null(DataKind.Image);
                }
                return ImageDataValueFactory.FromEncodedBytes(elements[position], frame.Target);
            }
            case DataKind.Struct:
            {
                // Each element is already a self-describing Struct DataValue with
                // its own TypeId stamped in the slot — just pick one. No registry
                // hop, no FromStruct rewrap, no f0..fN regression risk. The
                // translator turns on-disk TypeIds (from sidecar slots) into
                // runtime ids; in-memory paths pass through unchanged.
                DataValue[] elements = source.AsStructArray(
                    frame.Source, frame.SidecarRegistry, frame.TypeIdTranslations);
                if (position < 0 || position >= elements.Length)
                {
                    // Borrow TypeId from any existing element so the null still
                    // names the shape that *would* have been there. Empty arrays
                    // can't supply one — fall back to 0.
                    ushort fallbackTypeId = elements.Length > 0 ? elements[0].TypeId : (ushort)0;
                    return DataValue.NullStruct(fallbackTypeId);
                }
                return elements[position];
            }
        }

        // Fixed-width primitives. The single-element read goes through
        // AsArraySpan<T>; we wrap the resulting scalar back as a DataValue.
        return ReadFixedWidthArrayElement(source, position, frame);
    }

    /// <summary>
    /// Looks up the element TypeId for an Array&lt;Struct&gt; value via the
    /// registry's <c>ElementTypeId</c>. Returns 0 when the registry is null,
    /// the source has no TypeId, or the descriptor isn't an array shape.
    /// </summary>
    private ushort ResolveArrayElementTypeId(DataValue source)
    {
        if (_typeRegistry is null) return 0;
        ushort sourceTypeId = source.TypeId;
        if (sourceTypeId == 0) return 0;
        TypeDescriptor? desc = _typeRegistry.GetDescriptor(sourceTypeId);
        if (desc is null || !desc.IsArray) return 0;
        return desc.ElementTypeId is { } eid ? (ushort)eid : (ushort)0;
    }

    private static DataValue ReadFixedWidthArrayElement(DataValue source, int position, EvaluationFrame frame)
    {
        DataKind elementKind = source.Kind;
        return elementKind switch
        {
            DataKind.Boolean => ReadBooleanElement(source, position, frame),
            DataKind.Int8 => ReadElement<sbyte>(source, position, frame, DataValue.FromInt8, DataValue.Null(DataKind.Int8)),
            DataKind.UInt8 => ReadElement<byte>(source, position, frame, DataValue.FromUInt8, DataValue.Null(DataKind.UInt8)),
            DataKind.Int16 => ReadElement<short>(source, position, frame, DataValue.FromInt16, DataValue.Null(DataKind.Int16)),
            DataKind.UInt16 => ReadElement<ushort>(source, position, frame, DataValue.FromUInt16, DataValue.Null(DataKind.UInt16)),
            DataKind.Float16 => ReadElement<Half>(source, position, frame, DataValue.FromFloat16, DataValue.Null(DataKind.Float16)),
            DataKind.Int32 => ReadElement<int>(source, position, frame, DataValue.FromInt32, DataValue.Null(DataKind.Int32)),
            DataKind.UInt32 => ReadElement<uint>(source, position, frame, DataValue.FromUInt32, DataValue.Null(DataKind.UInt32)),
            DataKind.Float32 => ReadElement<float>(source, position, frame, DataValue.FromFloat32, DataValue.Null(DataKind.Float32)),
            DataKind.Date => ReadElement<int>(source, position, frame,
                dayNumber => DataValue.FromDate(DateOnly.FromDayNumber(dayNumber)), DataValue.Null(DataKind.Date)),
            DataKind.Int64 => ReadElement<long>(source, position, frame, DataValue.FromInt64, DataValue.Null(DataKind.Int64)),
            DataKind.UInt64 => ReadElement<ulong>(source, position, frame, DataValue.FromUInt64, DataValue.Null(DataKind.UInt64)),
            DataKind.Float64 => ReadElement<double>(source, position, frame, DataValue.FromFloat64, DataValue.Null(DataKind.Float64)),
            DataKind.Time => ReadElement<long>(source, position, frame,
                ticks => DataValue.FromTime(new TimeOnly(ticks)), DataValue.Null(DataKind.Time)),
            DataKind.Duration => ReadElement<long>(source, position, frame,
                ticks => DataValue.FromDuration(new TimeSpan(ticks)), DataValue.Null(DataKind.Duration)),
            DataKind.Decimal => ReadElement<decimal>(source, position, frame, DataValue.FromDecimal, DataValue.Null(DataKind.Decimal)),
            DataKind.Int128 => ReadElement<Int128>(source, position, frame, DataValue.FromInt128, DataValue.Null(DataKind.Int128)),
            DataKind.UInt128 => ReadElement<UInt128>(source, position, frame, DataValue.FromUInt128, DataValue.Null(DataKind.UInt128)),
            DataKind.Uuid => ReadElement<Guid>(source, position, frame, DataValue.FromUuid, DataValue.Null(DataKind.Uuid)),
            _ => throw new InvalidOperationException(
                $"Index access on Array<{elementKind}> is not yet wired through ReadFixedWidthArrayElement."),
        };
    }

    private static DataValue ReadBooleanElement(DataValue source, int position, EvaluationFrame frame)
    {
        ReadOnlySpan<byte> elements = source.AsArraySpan<byte>(frame.Source, frame.SidecarRegistry);
        if (position < 0 || position >= elements.Length)
        {
            return DataValue.Null(DataKind.Boolean);
        }
        return DataValue.FromBoolean(elements[position] != 0);
    }

    private static DataValue ReadElement<T>(
        DataValue source, int position, EvaluationFrame frame,
        Func<T, DataValue> wrap, DataValue outOfRangeNull) where T : unmanaged
    {
        ReadOnlySpan<T> elements = source.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        if (position < 0 || position >= elements.Length)
        {
            return outOfRangeNull;
        }
        return wrap(elements[position]);
    }

    private DataValue EvaluateStructFieldAccess(
        DataValue source, DataValue index, IndexAccessExpression indexAccess, EvaluationFrame frame)
    {
        DataValue[] fields = source.AsStruct(frame.Source);
        string fieldName = Str(index, frame);

        // Fast path: value already carries a type-id stamped at construction time.
        // Avoids schema/AST walking for values emitted by EvaluateStructLiteral or
        // model-invocation scatter that stamped the type-id at construction.
        if (_typeRegistry is not null && source.TypeId != 0)
        {
            TypeDescriptor? typeDesc = _typeRegistry.GetDescriptor(source.TypeId);
            if (typeDesc is not null)
            {
                int idx = typeDesc.FindFieldIndex(fieldName);
                if (idx >= 0)
                    return idx < fields.Length ? fields[idx] : DataValue.NullStruct(source.TypeId);
                return DataValue.NullStruct(source.TypeId);
            }
        }

        // Procedural variable bound to a struct via FOR-IN — field names live
        // alongside the binding on the variable scope, so we can resolve named
        // access without scanning a schema or AST. Variable-first precedence:
        // an unqualified ColumnReference is treated as a variable if its name
        // is bound in scope, before consulting the row schema.
        if (indexAccess.Source is ColumnReference unqualifiedSource
            && unqualifiedSource.TableName is null
            && _variableScope.TryGetFieldNames(unqualifiedSource.ColumnName, out IReadOnlyList<string>? variableFieldNames)
            && variableFieldNames is not null)
        {
            for (int i = 0; i < variableFieldNames.Count; i++)
            {
                if (string.Equals(variableFieldNames[i], fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return i < fields.Length ? fields[i] : DataValue.NullStruct(source.TypeId);
                }
            }
            return DataValue.NullStruct(source.TypeId);
        }

        // Try to resolve field position from schema when source is a column reference.
        if (indexAccess.Source is ColumnReference colRef)
        {
            IReadOnlyList<ColumnInfo>? columnFields = FindStructColumnFields(colRef, _sourceSchema);
            if (columnFields is not null)
            {
                return LookupFieldByName(fields, columnFields, fieldName, source.TypeId);
            }
        }

        // For struct literals, the field names are available in the AST.
        if (indexAccess.Source is StructLiteralExpression literal)
        {
            for (int i = 0; i < literal.Fields.Count; i++)
            {
                if (string.Equals(literal.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                {
                    return i < fields.Length ? fields[i] : DataValue.Null(DataKind.Float32);
                }
            }

            return DataValue.NullStruct(source.TypeId);
        }

        // Fallback for hidden LET binding references (e.g., __destructure_N produced by named
        // destructuring desugaring). When the schema doesn't carry struct field metadata for the
        // binding, recover field positions by following the chain of ColumnReference aliases in
        // _letBindingExpressions until we reach a StructLiteralExpression whose field names are
        // encoded in the AST. This handles both direct (`LET {a} = {x:1}`) and indirect
        // (`LET s = {x:1}; LET {a} = s`) cases.
        if (indexAccess.Source is ColumnReference bindingRef
            && _letBindingExpressions is not null
            && _letBindingExpressions.TryGetValue(bindingRef.ColumnName, out Expression? bindingExpr))
        {
            // Follow ColumnReference aliases up to a small depth cap to guard against cycles.
            for (int depth = 0; depth < 8 && bindingExpr is ColumnReference chainRef; depth++)
            {
                if (!_letBindingExpressions.TryGetValue(chainRef.ColumnName, out bindingExpr))
                    break;
            }

            if (bindingExpr is StructLiteralExpression bindingLiteral)
            {
                for (int i = 0; i < bindingLiteral.Fields.Count; i++)
                {
                    if (string.Equals(bindingLiteral.Fields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
                    {
                        return i < fields.Length ? fields[i] : DataValue.Null(DataKind.Float32);
                    }
                }

                return DataValue.NullStruct(source.TypeId);
            }
        }

        throw new InvalidOperationException(
            $"Cannot resolve struct field '{fieldName}': the source expression of kind " +
            $"{indexAccess.Source.GetType().Name} does not carry field name metadata at evaluation time. " +
            "Access struct fields via a column reference or a struct literal.");
    }

    private static IReadOnlyList<ColumnInfo>? FindStructColumnFields(ColumnReference column, Schema? schema)
    {
        if (schema is null)
        {
            return null;
        }

        ColumnInfo? info = null;

        if (column.TableName is not null)
        {
            info = schema.FindColumn($"{column.TableName}.{column.ColumnName}");
        }

        info ??= schema.FindColumn(column.ColumnName);
        return info?.Fields;
    }

    private static DataValue LookupFieldByName(
        DataValue[] fields,
        IReadOnlyList<ColumnInfo> columnFields,
        string fieldName,
        ushort typeId)
    {
        for (int i = 0; i < columnFields.Count; i++)
        {
            if (string.Equals(columnFields[i].Name, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return i < fields.Length ? fields[i] : DataValue.Null(columnFields[i].Kind);
            }
        }

        return DataValue.NullStruct(typeId);
    }
}
