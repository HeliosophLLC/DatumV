using System.Numerics;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

public sealed partial class ExpressionEvaluator
{
    /// <summary>
    /// Evaluates an expression as a <see cref="ValueRef"/>. Function call
    /// expressions short-circuit to <see cref="EvaluateFunctionAsValueRefAsync"/>
    /// to keep nested chains in managed memory; everything else falls back to
    /// the existing <see cref="EvaluateAsync(Expression, EvaluationFrame, CancellationToken)"/>
    /// path and lifts the result via <see cref="ToValueRef"/>.
    /// </summary>
    /// <remarks>
    /// This matters for <c>outer(middle(inner(x)))</c>-style chains: each
    /// recursive call into <see cref="EvaluateFunctionAsValueRefAsync"/> produces a
    /// managed <see cref="ValueRef"/> the next stage consumes directly, so the
    /// only arena write is the outermost call's <see cref="ToDataValue"/>.
    /// Earlier intermediates become unreachable as soon as the next stage's
    /// result is constructed and are reclaimed by the GC.
    /// </remarks>
    public ValueTask<ValueRef> EvaluateAsValueRefAsync(
        Expression expression, EvaluationFrame frame, CancellationToken cancellationToken = default)
    {
        // Auto-attach this evaluator as the frame's LambdaInvoker if a caller
        // constructed the frame without one. See the matching comment on
        // EvaluateAsync — same rationale, applied to the ValueRef-path entry.
        if (frame.LambdaInvoker is null)
        {
            frame = frame.WithLambdaInvoker(this);
        }

        // Sync fast path: the two arms that produce a ValueRef synchronously
        // (procedural-variable column reads, lambda capture). No state machine
        // is built when the call lands here.
        if (expression is ColumnReference columnRef
            && columnRef.TableName is null
            && _variableScope.TryGet(columnRef.ColumnName, out ValueRef variableValue))
        {
            return new ValueTask<ValueRef>(variableValue);
        }
        if (expression is LambdaExpression lambda)
        {
            return new ValueTask<ValueRef>(
                ValueRef.FromLambda(LambdaValue.Capture(lambda, frame.Row)));
        }

        // ValueRef-native handlers for predicate-hot expression kinds. They're
        // `async ValueTask<>` themselves but may complete synchronously when
        // their operands do — try-IsCompletedSuccessfully here would buy
        // nothing because we still have to dispatch on the expression type
        // first, and the inner methods already avoid arena writes when their
        // inputs are inline.
        switch (expression)
        {
            case FunctionCallExpression functionCall:
                return EvaluateFunctionAsValueRefAsync(functionCall, frame, cancellationToken);
            case CastExpression castExpr:
                return EvaluateCastAsValueRefAsync(castExpr, frame, cancellationToken);
            case InlineAccessorExpression inlineAccessor:
                return EvaluateInlineAccessorAsValueRefAsync(inlineAccessor, frame, cancellationToken);
            case BinaryExpression binary:
                return EvaluateBinaryAsValueRefAsync(binary, frame, cancellationToken);
            case UnaryExpression unary:
                return EvaluateUnaryAsValueRefAsync(unary, frame, cancellationToken);
            case IsNullExpression isNull:
                return EvaluateIsNullAsValueRefAsync(isNull, frame, cancellationToken);
        }

        // Fallback for every other expression kind: lower through the DataValue
        // path and lift the result. EvaluateAsync's sync fast path means
        // leaf-level evaluations (LiteralExpression, ColumnReference against a
        // row column) complete synchronously here too — no state machine.
        ValueTask<DataValue> rawTask = EvaluateAsync(expression, frame, cancellationToken);
        if (rawTask.IsCompletedSuccessfully)
        {
            return new ValueTask<ValueRef>(ToValueRef(rawTask.Result, frame));
        }
        return AwaitToValueRefAsync(rawTask, frame);

        static async ValueTask<ValueRef> AwaitToValueRefAsync(ValueTask<DataValue> pending, EvaluationFrame frame)
        {
            DataValue raw = await pending.ConfigureAwait(false);
            return ToValueRef(raw, frame);
        }
    }

    /// <inheritdoc cref="ILambdaInvoker.InvokeLambdaAsync"/>
    /// <remarks>
    /// <para>
    /// The invocation strategy threads through existing infrastructure:
    /// the lambda's captured <see cref="Row"/> replaces the frame's
    /// <see cref="EvaluationFrame.Row"/> (so the body's free-variable
    /// references resolve against the row in scope when the lambda was
    /// constructed), and the parameter bindings are pushed onto the
    /// evaluator's <see cref="VariableScope"/> for the duration of the
    /// body evaluation. The variable-scope lookup precedes the row lookup
    /// in <see cref="EvaluateAsValueRefAsync"/>, so parameter names shadow
    /// captured-row columns naturally.
    /// </para>
    /// <para>
    /// The <see cref="VariableScope"/> is required: a frame without one
    /// has no place to bind parameters. Hosts that want lambda support
    /// must construct the evaluator with a non-null <c>variableScope</c>
    /// (the procedural-body executor already does this; ad-hoc frames
    /// need to opt in).
    /// </para>
    /// </remarks>
    public async ValueTask<ValueRef> InvokeLambdaAsync(
        ValueRef lambda,
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        LambdaValue value = lambda.AsLambda();
        ReadOnlySpan<ValueRef> args = arguments.Span;
        if (args.Length != value.Parameters.Count)
        {
            throw new ArgumentException(
                $"Lambda expects {value.Parameters.Count} argument(s) "
                + $"({string.Join(", ", value.Parameters)}); got {args.Length}.",
                nameof(arguments));
        }
        _variableScope.PushFrame();
        try
        {
            for (int i = 0; i < args.Length; i++)
            {
                _variableScope.Declare(value.Parameters[i], args[i]);
            }
            // Captures become the row in scope for the lambda body. Column
            // references in the body resolve against captures unless they're
            // shadowed by a parameter binding (which the variable-scope
            // lookup handles first inside EvaluateAsValueRefAsync).
            EvaluationFrame inner = frame.WithRow(value.Captures);
            return await EvaluateAsValueRefAsync(value.Body.Body, inner, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _variableScope.PopFrame();
        }
    }

    /// <summary>
    /// Materialises a <see cref="DataValue"/> argument into a
    /// <see cref="ValueRef"/>: arena-backed strings/arrays are read into managed
    /// payloads, sidecar-backed values are loaded via the registry, and inline
    /// values pass through unchanged. Public so callers that produce a
    /// <see cref="DataValue"/> from a sub-query (procedural batch evaluation,
    /// scalar sub-SELECT) can lift the result to a managed-payload ValueRef
    /// before storing it in a <see cref="VariableScope"/> — the resulting
    /// binding outlives the producing batch's arena.
    /// </summary>
    public static ValueRef ToValueRef(DataValue value, EvaluationFrame frame)
    {
        // Null values: pick the right shape (null array vs null scalar) so
        // downstream IsArray checks don't misfire on a typed-null array.
        if (value.IsNull)
        {
            return value.IsArray
                ? ValueRef.NullArray(value.Kind)
                : ValueRef.Null(value.Kind);
        }

        // Arrays must be dispatched before the inline scalar branch: inline
        // arrays (IsInlineArray flag — small typed arrays packed into the
        // 16-byte payload) satisfy IsInline, but FromInline can't materialise
        // their elements as the ValueRef[] downstream consumers expect from
        // GetArrayElements. Route every IsArray value through the dispatcher
        // first so inline and arena-backed arrays produce the same shape.
        if (value.IsArray)
        {
            return ArrayDataValueToValueRef(value, frame);
        }

        if (value.IsInline)
        {
            return ValueRef.FromInline(value);
        }

        // Non-inline single-value byte blobs. Forward the originating DataValue
        // as the metadata carrier so the inline accessor fast path
        // (audio_sample_rate, image_width, etc.) reads stamped sample-rate /
        // dimensions / etc. instead of the zero sentinel — without this, an
        // arena- or sidecar-backed media value reaching a function argument
        // loses its inline metadata at the DataValue → ValueRef hop and every
        // *_*() metadata accessor downstream returns NULL.
        if (value.Kind is DataKind.Image or DataKind.Audio or DataKind.Video or DataKind.Json or DataKind.PointCloud or DataKind.Mesh)
        {
            ReadOnlySpan<byte> bytes = value.AsByteSpan(frame.Source, frame.SidecarRegistry);
            return ValueRef.FromBytesWithMetadata(value, bytes.ToArray());
        }

        if (value.Kind == DataKind.Struct)
        {
            return StructDataValueToValueRef(value, frame);
        }

        return value.Kind switch
        {
            DataKind.String =>
                ValueRef.FromString(value.AsString(frame.Source, frame.SidecarRegistry)),
            _ => throw new InvalidOperationException(
                $"Cannot convert non-inline DataValue of kind {value.Kind} into a ValueRef. "
                + "Add support to ExpressionEvaluator.ToValueRef when this kind reaches the function boundary."),
        };
    }

    /// <summary>
    /// Lifts a single non-null Struct <see cref="DataValue"/> into a
    /// <see cref="ValueRef"/> by reading its fields from the arena and
    /// recursively lifting each one. The resulting ValueRef carries the
    /// source's <see cref="DataValue.TypeId"/> on its inline carrier so
    /// downstream consumers can resolve field names via the registry.
    /// </summary>
    private static ValueRef StructDataValueToValueRef(DataValue value, EvaluationFrame frame)
    {
        DataValue[] fieldValues = value.AsStruct(frame.Source);
        ValueRef[] fields = new ValueRef[fieldValues.Length];
        for (int i = 0; i < fieldValues.Length; i++)
        {
            fields[i] = ToValueRef(fieldValues[i], frame);
        }
        return ValueRef.FromStruct(fields, value.TypeId);
    }

    /// <summary>
    /// Reads a non-inline array <see cref="DataValue"/> into a managed payload
    /// and wraps the result in the matching <see cref="ValueRef"/> shape so
    /// downstream functions see <c>IsArray=true</c>. Reference-type arrays
    /// (<c>Array&lt;String&gt;</c>, <c>Array&lt;Image&gt;</c>) build a per-element
    /// <see cref="ValueRef"/>; fixed-width primitive arrays flow through
    /// <see cref="ValueRef.FromPrimitiveArray{T}"/> with the matching
    /// element type.
    /// </summary>
    private static ValueRef ArrayDataValueToValueRef(DataValue value, EvaluationFrame frame)
    {
        // Multi-dim arrays: materialise as a flat managed T[] (so existing
        // `_materialized as float[]` consumers continue to work) AND attach the
        // shape via FromPrimitiveMultiDimArray so multi-dim-aware paths
        // (array_shape, array_get, bracket access) see ndim + shape.
        if (value.IsMultiDim)
        {
            int[] shape = value.GetShape(frame.Source, frame.SidecarRegistry).ToArray();
            return value.Kind switch
            {
                DataKind.Boolean => PrimitiveMultiDimToValueRef<byte>(value, shape, frame),
                DataKind.UInt8   => PrimitiveMultiDimToValueRef<byte>(value, shape, frame),
                DataKind.UInt16  => PrimitiveMultiDimToValueRef<ushort>(value, shape, frame),
                DataKind.UInt32  => PrimitiveMultiDimToValueRef<uint>(value, shape, frame),
                DataKind.UInt64  => PrimitiveMultiDimToValueRef<ulong>(value, shape, frame),
                DataKind.Int8    => PrimitiveMultiDimToValueRef<sbyte>(value, shape, frame),
                DataKind.Int16   => PrimitiveMultiDimToValueRef<short>(value, shape, frame),
                DataKind.Int32   => PrimitiveMultiDimToValueRef<int>(value, shape, frame),
                DataKind.Int64   => PrimitiveMultiDimToValueRef<long>(value, shape, frame),
                DataKind.Float16 => PrimitiveMultiDimToValueRef<Half>(value, shape, frame),
                DataKind.Float32 => PrimitiveMultiDimToValueRef<float>(value, shape, frame),
                DataKind.Float64 => PrimitiveMultiDimToValueRef<double>(value, shape, frame),
                _ => throw new InvalidOperationException(
                    $"Cannot convert multi-dim Array<{value.Kind}> into a ValueRef. " +
                    "Add a case to ArrayDataValueToValueRef when this element kind reaches the function boundary."),
            };
        }

        return value.Kind switch
        {
            DataKind.String => StringArrayToValueRef(value, frame),
            DataKind.Image => BytesArrayToValueRef(value, DataKind.Image, frame),
            DataKind.Struct => StructArrayToValueRef(value, frame),

            DataKind.Boolean => PrimitiveArrayToValueRef<byte>(value, frame),
            DataKind.UInt8 => PrimitiveArrayToValueRef<byte>(value, frame),
            DataKind.UInt16 => PrimitiveArrayToValueRef<ushort>(value, frame),
            DataKind.UInt32 => PrimitiveArrayToValueRef<uint>(value, frame),
            DataKind.UInt64 => PrimitiveArrayToValueRef<ulong>(value, frame),
            DataKind.Int8 => PrimitiveArrayToValueRef<sbyte>(value, frame),
            DataKind.Int16 => PrimitiveArrayToValueRef<short>(value, frame),
            DataKind.Int32 => PrimitiveArrayToValueRef<int>(value, frame),
            DataKind.Int64 => PrimitiveArrayToValueRef<long>(value, frame),
            DataKind.Float16 => PrimitiveArrayToValueRef<Half>(value, frame),
            DataKind.Float32 => PrimitiveArrayToValueRef<float>(value, frame),
            DataKind.Float64 => PrimitiveArrayToValueRef<double>(value, frame),

            // Spatial: Point2D packs as an 8-byte (Vector2) inline scalar,
            // so array storage memcpys directly through Vector2[]. Used by
            // FaceDetection.landmarks and any other model that emits a list
            // of 2D points (keypoint detectors, pose estimators).
            DataKind.Point2D => PrimitiveArrayToValueRef<Vector2>(value, frame),

            _ => throw new InvalidOperationException(
                $"Cannot convert non-inline Array<{value.Kind}> into a ValueRef. "
                + "Add a case to ExpressionEvaluator.ArrayDataValueToValueRef when this element kind reaches the function boundary."),
        };
    }

    private static ValueRef StringArrayToValueRef(DataValue value, EvaluationFrame frame)
    {
        string[] strings = value.AsStringArray(frame.Source, frame.SidecarRegistry);
        ValueRef[] elements = new ValueRef[strings.Length];
        for (int i = 0; i < strings.Length; i++)
        {
            elements[i] = ValueRef.FromString(strings[i]);
        }
        return ValueRef.FromArray(DataKind.String, elements);
    }

    private static ValueRef BytesArrayToValueRef(DataValue value, DataKind elementKind, EvaluationFrame frame)
    {
        byte[][] blobs = value.AsImageArray(frame.Source, frame.SidecarRegistry);
        ValueRef[] elements = new ValueRef[blobs.Length];
        for (int i = 0; i < blobs.Length; i++)
        {
            elements[i] = ValueRef.FromBytes(elementKind, blobs[i]);
        }
        return ValueRef.FromArray(elementKind, elements);
    }

    private static ValueRef PrimitiveArrayToValueRef<T>(DataValue value, EvaluationFrame frame)
        where T : unmanaged
    {
        ReadOnlySpan<T> span = value.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        return ValueRef.FromPrimitiveArray(span.ToArray(), value.Kind);
    }

    /// <summary>
    /// Multi-dim counterpart to <see cref="PrimitiveArrayToValueRef{T}"/>:
    /// materialises the flat element span (post-shape-prefix) into a managed
    /// <c>T[]</c> AND attaches the per-dim shape so the resulting ValueRef
    /// both satisfies <c>_materialized as T[]</c> consumers and surfaces
    /// <see cref="ValueRef.IsMultiDim"/>/<see cref="ValueRef.Ndim"/> +
    /// <see cref="ValueRef.ToDataValue"/> multi-dim materialisation.
    /// </summary>
    private static ValueRef PrimitiveMultiDimToValueRef<T>(DataValue value, int[] shape, EvaluationFrame frame)
        where T : unmanaged
    {
        ReadOnlySpan<T> span = value.AsArraySpan<T>(frame.Source, frame.SidecarRegistry);
        return ValueRef.FromPrimitiveMultiDimArray(span.ToArray(), shape, value.Kind);
    }

    /// <summary>
    /// Lifts a non-inline <c>Array&lt;Struct&gt;</c> into a <see cref="ValueRef"/>
    /// of struct-shaped <see cref="ValueRef"/>s. Each element is itself read via
    /// <see cref="StructDataValueToValueRef"/> so nested struct/array fields
    /// (e.g. SCRFD's <c>landmarks: Array&lt;Struct{x, y}&gt;</c>) lift through
    /// the same recursion path. Per-element <see cref="DataValue.TypeId"/>s
    /// are preserved.
    /// </summary>
    private static ValueRef StructArrayToValueRef(DataValue value, EvaluationFrame frame)
    {
        DataValue[] elements = value.AsStructArray(frame.Source, frame.SidecarRegistry, frame.TypeIdTranslations);
        ValueRef[] refs = new ValueRef[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            refs[i] = StructDataValueToValueRef(elements[i], frame);
        }
        return ValueRef.FromArray(DataKind.Struct, refs);
    }

    /// <summary>
    /// Lowers a function-result <see cref="ValueRef"/> back into a
    /// <see cref="DataValue"/> against <paramref name="frame"/>'s target arena.
    /// Thin wrapper around <see cref="ValueRef.ToDataValue"/> that picks the
    /// frame's target store; the recursion for struct/array values is
    /// handled by ValueRef itself.
    /// </summary>
    private static DataValue ToDataValue(ValueRef value, EvaluationFrame frame) =>
        // Pass the ValueRef's own TypeId through so struct-shape metadata
        // round-trips through DataValue. Without this, identity-cast on a
        // struct (DECLARE encoded Struct = ...) strips the TypeId and
        // breaks downstream `encoded['field']` index access.
        value.ToDataValue(frame.Target, value.TypeId, frame.Types);
}
