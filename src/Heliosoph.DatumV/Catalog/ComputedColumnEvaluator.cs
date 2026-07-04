using Heliosoph.DatumV.Catalog.Executors;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Shared <see cref="ValueRef"/> → target-shaped <see cref="DataValue"/>
/// conversion for <c>GENERATED ALWAYS AS</c> column evaluation. Used by
/// <see cref="InsertExecutor"/>'s per-row fill pass and
/// <see cref="UpdateExecutor"/>'s dependent-recompute pass — both produce
/// a <see cref="ValueRef"/> from <see cref="ExpressionEvaluator.EvaluateAsValueRefAsync"/>
/// and need to land the value in a target column's arena.
/// </summary>
internal static class ComputedColumnEvaluator
{
    /// <summary>
    /// Converts a <see cref="ValueRef"/> (the output of
    /// <see cref="ExpressionEvaluator.EvaluateAsValueRefAsync"/>) into a target-shaped
    /// <see cref="DataValue"/>. Handles array literals natively — when the source
    /// array's element kind doesn't match the target (e.g. <c>[10, 20, 30]</c>
    /// narrows to <c>Int8[]</c> but the column is <c>Int32[]</c>), elements are
    /// widened individually through <see cref="LiteralCoercion"/>. Cross-arena
    /// copies are not needed for INSERT VALUES (single arena for evaluation and
    /// writing); for UPDATE the caller threads the workArena as
    /// <paramref name="targetArena"/> so the new value outlives the per-batch
    /// scan arena.
    /// </summary>
    public static DataValue ConvertValueRefToTarget(
        ValueRef source, ColumnInfo target, Arena targetArena, string columnName,
        TypeRegistry? types = null)
    {
        if (source.IsNull)
        {
            if (!target.Nullable)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}' is NOT NULL but the supplied value is NULL.");
            }
            return target.IsArray ? DataValue.NullArrayOf(target.Kind) : DataValue.Null(target.Kind);
        }

        // Typed-array target: shape must match (source.IsArray == true).
        if (target.IsArray)
        {
            if (!source.IsArray)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': target is {target.Kind}[] but the " +
                    $"supplied value is scalar {source.Kind}.");
            }

            // Array<Struct> targets always take the per-element coercion path.
            // The same-kind ToDataValue fast path below would carry whatever
            // per-element TypeIds the source expression happened to stamp;
            // coercing against the declared shape re-orders fields, coerces
            // field kinds, and stamps the column's canonical type-id.
            if (target.Kind == DataKind.Struct)
            {
                return ConvertStructArrayValueRef(source, target, targetArena, columnName, types);
            }

            // Same element kind: hand the array directly to ToDataValue, which
            // materialises payload bytes into the target arena. This is the
            // hot path for functions like sha256/base64 that produce
            // byte-backed UInt8[] via ValueRef.FromBytes — those don't carry
            // a ValueRef[] payload, so calling GetArrayElements on them
            // throws. Same-kind doesn't need per-element extraction, so we
            // skip GetArrayElements entirely here.
            if (source.Kind == target.Kind)
            {
                DataValue materialised = source.ToDataValue(targetArena);
                return LiteralCoercion.EnforceFixedShape(materialised, target, columnName, targetArena);
            }

            // Different element kind: per-element coerce via LiteralCoercion,
            // then assemble a new typed array from the coerced DataValues.
            // GetArrayElements is only called for cross-kind cases — those
            // arrays come from struct/array literal evaluation, which builds
            // ValueRef[] payloads, so the call is safe here.
            ReadOnlySpan<ValueRef> elements = source.GetArrayElements();
            DataValue coerced = CoerceArrayElements(elements, target, targetArena, columnName);
            return LiteralCoercion.EnforceFixedShape(coerced, target, columnName, targetArena);
        }

        // Scalar target. Reject array-source / struct-source up front.
        if (source.IsArray)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': target is scalar {target.Kind} but the " +
                "supplied value is an array.");
        }
        if (source.Kind == DataKind.Struct)
        {
            return ConvertStructValueRef(source, target, targetArena, columnName, types);
        }

        // Blob-kind sources (Image / Audio / Video / Json). ValueRef.ToDataValue
        // already handles materialising managed bytes / SKBitmap / CBOR slices
        // into the target arena — let it do the byte copy. Reject only on kind
        // mismatch; no implicit blob coercion.
        if (source.Kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json)
        {
            if (target.Kind != source.Kind)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': target is {target.Kind} but the " +
                    $"supplied value is {source.Kind}; blob kinds (Image/Audio/Video/Json) " +
                    "do not coerce across kinds.");
            }
            return source.ToDataValue(targetArena);
        }

        // Non-array, non-struct, non-blob: box the scalar via
        // ValueRef.ToObject() and run through LiteralCoercion, the same
        // lossless coercion path VALUES uses for plain literals. The String
        // arm reads from ValueRef's in-struct materialized payload — no
        // store needed at this boundary.
        return LiteralCoercion.Coerce(
            source.ToObject(), target, targetArena, columnName);
    }

    /// <summary>
    /// Converts a struct-shaped <see cref="ValueRef"/> into the declared
    /// struct column shape. Fields match by NAME (case-insensitive) through
    /// the value's <see cref="TypeDescriptor"/>, each field coercing
    /// recursively to its declared <see cref="ColumnInfo"/>. The result is
    /// rebuilt in the declared field order and stamped with the type-id
    /// interned from the declared shape, so downstream consumers (writer
    /// type-table capture, field access) see the column's canonical type.
    /// Missing source fields land as typed nulls when the declared field is
    /// nullable and throw otherwise; extra source fields always throw —
    /// silently dropping data is worse than an error.
    /// </summary>
    private static DataValue ConvertStructValueRef(
        ValueRef source, ColumnInfo target, Arena targetArena, string columnName,
        TypeRegistry? types, ushort precomputedTypeId = 0)
    {
        if (target.Kind != DataKind.Struct || target.Fields is not { Count: > 0 } declared)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': target is {target.Kind}{(target.IsArray ? "[]" : "")} " +
                "but the supplied value is a struct.");
        }
        // A scalar struct can't fill an Array<Struct> column — Array<Struct>
        // targets route through ConvertStructArrayValueRef, which calls this
        // with a per-element (non-array) target. Reaching here with an array
        // target means a scalar struct was handed to an array column.
        if (target.IsArray)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': target is an Array<Struct> column but the supplied value " +
                "is a scalar struct — wrap it in an array literal to match the declared shape.");
        }

        TypeDescriptor? shape = types?.GetDescriptor(source.TypeId);
        if (shape?.Fields is not { } sourceFields)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': the struct value carries no type information, so its " +
                "fields cannot be matched to the declared Struct shape by name.");
        }

        ReadOnlySpan<ValueRef> values = source.GetStructFields();
        if (values.Length != sourceFields.Count)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': struct value carries {values.Length} fields but its " +
                $"type descriptor declares {sourceFields.Count} — the construction site stamped " +
                "a mismatched type-id (internal error).");
        }

        DataValue[] coerced = new DataValue[declared.Count];
        for (int i = 0; i < declared.Count; i++)
        {
            ColumnInfo field = declared[i];
            int sourceIndex = -1;
            for (int f = 0; f < sourceFields.Count; f++)
            {
                if (string.Equals(sourceFields[f].Name, field.Name, StringComparison.OrdinalIgnoreCase))
                {
                    sourceIndex = f;
                    break;
                }
            }

            if (sourceIndex < 0)
            {
                if (!field.Nullable)
                {
                    throw new InvalidOperationException(
                        $"Column '{columnName}': struct value has no field '{field.Name}', " +
                        "which the declared Struct type marks NOT NULL.");
                }
                coerced[i] = field.IsArray ? DataValue.NullArrayOf(field.Kind) : DataValue.Null(field.Kind);
                continue;
            }

            coerced[i] = ConvertValueRefToTarget(
                values[sourceIndex], field, targetArena, $"{columnName}.{field.Name}", types);
        }

        foreach (StructFieldDescriptor sourceField in sourceFields)
        {
            bool known = false;
            for (int i = 0; i < declared.Count; i++)
            {
                if (string.Equals(declared[i].Name, sourceField.Name, StringComparison.OrdinalIgnoreCase))
                {
                    known = true;
                    break;
                }
            }
            if (!known)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': struct value carries field '{sourceField.Name}' " +
                    "which the declared Struct type does not define.");
            }
        }

        // Array element coercion pre-interns the declared shape once and
        // passes it down, so per-element interning collapses to a no-op.
        ushort typeId = precomputedTypeId != 0
            ? precomputedTypeId
            : checked((ushort)types!.InternStructFromColumnInfoFields(declared));
        return DataValue.FromStruct(coerced, targetArena, typeId);
    }

    /// <summary>
    /// Converts an array-of-struct <see cref="ValueRef"/> into the declared
    /// <c>Array&lt;Struct&gt;</c> column shape: every element runs through
    /// <see cref="ConvertStructValueRef"/> (name-matched, declared field
    /// order, canonical type-id) and the result is reassembled with the
    /// declared element type-id so downstream encoders persist a consistent
    /// shape.
    /// </summary>
    private static DataValue ConvertStructArrayValueRef(
        ValueRef source, ColumnInfo target, Arena targetArena, string columnName,
        TypeRegistry? types)
    {
        if (target.Fields is not { Count: > 0 } declared)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': the declared Array<Struct> column carries no field " +
                "shape, so struct elements cannot be coerced.");
        }
        if (types is null)
        {
            throw new InvalidOperationException(
                $"Column '{columnName}': coercing Array<Struct> values requires the query's " +
                "TypeRegistry (internal wiring error).");
        }

        ReadOnlySpan<ValueRef> elements = source.GetArrayElements();
        ushort elementTypeId = checked((ushort)types.InternStructFromColumnInfoFields(declared));
        ColumnInfo elementTarget = new(target.Name, nullable: true, declared);

        DataValue[][] fieldArrays = new DataValue[elements.Length][];
        for (int i = 0; i < elements.Length; i++)
        {
            if (elements[i].IsNull)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': null element at index {i}; " +
                    "per-element nulls inside arrays are not yet supported.");
            }
            DataValue coerced = ConvertStructValueRef(
                elements[i], elementTarget, targetArena, $"{columnName}[{i}]", types,
                precomputedTypeId: elementTypeId);
            fieldArrays[i] = coerced.AsStruct(targetArena);
        }
        return DataValue.FromStructArray(fieldArrays, targetArena, elementTypeId);
    }

    /// <summary>
    /// Builds a typed array <see cref="DataValue"/> from per-element source
    /// <see cref="ValueRef"/>s, coercing each element to the target column's
    /// element kind via <see cref="LiteralCoercion"/>.
    /// </summary>
    private static DataValue CoerceArrayElements(
        ReadOnlySpan<ValueRef> sourceElements,
        ColumnInfo target,
        Arena targetArena,
        string columnName)
    {
        // LiteralCoercion gates IsArray off the column descriptor; build a
        // scalar-shaped clone so per-element coercion routes through the
        // normal scalar arms.
        ColumnInfo elementTarget = new(target.Name, target.Kind, nullable: false);

        DataValue[] coerced = new DataValue[sourceElements.Length];
        for (int i = 0; i < sourceElements.Length; i++)
        {
            ValueRef element = sourceElements[i];
            if (element.IsNull)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': null element at index {i}; " +
                    "per-element nulls inside arrays are not yet supported.");
            }
            if (element.IsArray)
            {
                throw new InvalidOperationException(
                    $"Column '{columnName}': nested arrays are not supported.");
            }

            object scalar = element.ToObject()!;
            coerced[i] = LiteralCoercion.Coerce(scalar, elementTarget, targetArena, columnName);
        }

        return DataValue.FromTypedArray(target.Kind, coerced, targetArena, targetArena);
    }
}
