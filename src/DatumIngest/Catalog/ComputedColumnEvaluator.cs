using DatumIngest.Catalog.Executors;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog;

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
        ValueRef source, ColumnInfo target, Arena targetArena, string columnName)
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

            // Same element kind: hand the array directly to ToDataValue, which
            // materialises payload bytes into the target arena. This is the
            // hot path for functions like sha256/base64 that produce
            // byte-backed UInt8[] via ValueRef.FromBytes — those don't carry
            // a ValueRef[] payload, so calling GetArrayElements on them
            // throws. Same-kind doesn't need per-element extraction, so we
            // skip GetArrayElements entirely here.
            if (source.Kind == target.Kind)
            {
                return source.ToDataValue(targetArena);
            }

            // Different element kind: per-element coerce via LiteralCoercion,
            // then assemble a new typed array from the coerced DataValues.
            // GetArrayElements is only called for cross-kind cases — those
            // arrays come from struct/array literal evaluation, which builds
            // ValueRef[] payloads, so the call is safe here.
            ReadOnlySpan<ValueRef> elements = source.GetArrayElements();
            return CoerceArrayElements(elements, target, targetArena, columnName);
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
            throw new InvalidOperationException(
                $"Column '{columnName}': struct values are not yet supported. " +
                "Struct-typed manifest support lands with the Value Type Registry.");
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
