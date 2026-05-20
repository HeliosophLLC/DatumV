using System.Buffers;
using System.Diagnostics;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Execution;

public sealed partial class ExpressionEvaluator
{
    private async ValueTask<DataValue> EvaluateFunctionAsync(
        FunctionCallExpression function, EvaluationFrame frame, CancellationToken cancellationToken) =>
        ToDataValue(await EvaluateFunctionAsValueRefAsync(function, frame, cancellationToken).ConfigureAwait(false), frame);

    /// <summary>
    /// Evaluates a function call directly as a <see cref="ValueRef"/>. Used as
    /// the inner step of nested function chains so intermediate values stay in
    /// managed memory rather than round-tripping through the arena: in
    /// <c>outer(middle(inner(x)))</c>, only <c>outer</c>'s top-level result
    /// crosses the <see cref="ToDataValue"/> boundary.
    /// </summary>
    private async ValueTask<ValueRef> EvaluateFunctionAsValueRefAsync(
        FunctionCallExpression function, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        IScalarFunction? scalarFunction = _functions.TryGetScalar(function.CallName);

        if (scalarFunction is null)
        {
            // Check the other registries to give a more useful error when
            // the name does exist but in a non-scalar shape. CALL lowers to
            // SELECT <expr> so TVFs / aggregates / window functions all
            // surface as "Unknown function" here unless we cross-check.
            if (_functions.TryGetTableValued(function.CallName) is not null)
            {
                throw new InvalidOperationException(
                    $"'{function.CallName}' is a table-valued function; use it in a FROM clause " +
                    $"(e.g. SELECT * FROM {function.CallName}(...)) rather than as a scalar expression.");
            }
            else if (_functions.TryGetAggregate(function.CallName) is not null)
            {
                throw new InvalidOperationException(
                    $"'{function.CallName}' is an aggregate function; use it inside SELECT with GROUP BY " +
                    "(or wrap a scalar argument so it computes against a single value).");
            }
            else if (_functions.TryGetWindow(function.CallName) is not null)
            {
                throw new InvalidOperationException(
                    $"'{function.CallName}' is a window function; use it with an OVER clause " +
                    $"(e.g. {function.CallName}(...) OVER (...)).");
            }
            
            throw new InvalidOperationException(
                $"Unknown function: '{function.CallName}'.");
        }

        int argumentCount = function.Arguments.Count;
        ValueRef[] arguments = ArrayPool<ValueRef>.Shared.Rent(argumentCount);
        try
        {
            for (int index = 0; index < argumentCount; index++)
            {
                arguments[index] = await EvaluateAsValueRefAsync(function.Arguments[index], frame, cancellationToken).ConfigureAwait(false);
            }

            if (_validatedScalarCalls.Add(function))
            {
                ValidateScalarCallSiteOrThrow(scalarFunction, function, arguments.AsSpan(0, argumentCount));
                ResolveParameterCheckBindings(function, arguments.AsSpan(0, argumentCount));
            }

            if (_siteParameterChecks.TryGetValue(function, out ParameterCheckBinding[]? bindings)
                && bindings.Length > 0)
            {
                ValidateParameterChecksOrThrow(function, bindings, arguments.AsSpan(0, argumentCount));
            }

            // Per-call span on DatumActivity.Scalars. Nests under the owning
            // operator's batch span via Activity.Current so a recent-activity
            // dump shows which function inside an operator batch did the work.
            // The Scalars source is intentionally separate from Operators —
            // listeners can subscribe to operator-only if per-row allocation
            // cost is unwelcome.
            using Activity? scalarSpan = DatumActivity.Scalars.StartActivity(function.CallName);
            ValueRef result = await scalarFunction.ExecuteAsync(arguments.AsMemory(0, argumentCount), frame, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            // Clear references so the pool doesn't root managed payloads.
            arguments.AsSpan(0, argumentCount).Clear();
            ArrayPool<ValueRef>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// DataValue-returning entry for the post-elision accessor node.
    /// Materialises the ValueRef-native result through <see cref="ToDataValue"/>,
    /// mirroring the <see cref="EvaluateFunctionAsync"/> shape so the elision
    /// is transparent to callers that consume <see cref="DataValue"/>.
    /// </summary>
    private async ValueTask<DataValue> EvaluateInlineAccessorAsync(
        InlineAccessorExpression accessor, EvaluationFrame frame, CancellationToken cancellationToken) =>
        ToDataValue(await EvaluateInlineAccessorAsValueRefAsync(accessor, frame, cancellationToken).ConfigureAwait(false), frame);

    /// <summary>
    /// Fast path for <see cref="InlineAccessorExpression"/>: evaluates the
    /// single argument, reads the requested per-kind inline-metadata byte
    /// (or set of bytes) directly off <see cref="DataValue"/>, and returns
    /// a <see cref="ValueRef"/> without invoking
    /// <see cref="IScalarFunction.ExecuteAsync"/> on the stamped path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When inline metadata reads as the zero sentinel (the producing path
    /// didn't stamp it — typically a model output or a legacy value), the
    /// handler delegates to the original
    /// <see cref="IInlineMetadataAccessor"/> function's <c>ExecuteAsync</c>
    /// so the slow-path decode behaviour is preserved bit-for-bit. The
    /// fallback dispatch reuses the standard
    /// <see cref="ArrayPool{T}"/>-rented argument array and per-call
    /// activity span so observability stays identical to the un-elided path.
    /// </para>
    /// <para>
    /// Per-argument NULL handling matches the original functions: a NULL
    /// input yields a NULL result of the descriptor's
    /// <see cref="InlineAccessorDescriptors.Descriptor.ResultKind"/>.
    /// </para>
    /// </remarks>
    private async ValueTask<ValueRef> EvaluateInlineAccessorAsValueRefAsync(
        InlineAccessorExpression accessor, EvaluationFrame frame, CancellationToken cancellationToken)
    {
        ValueRef arg = await EvaluateAsValueRefAsync(accessor.Argument, frame, cancellationToken).ConfigureAwait(false);

        InlineAccessorDescriptors.Descriptor descriptor = InlineAccessorDescriptors.Get(accessor.Field);

        if (arg.IsNull)
        {
            return ValueRef.Null(descriptor.ResultKind);
        }

        // Fast path: per-kind inline metadata. Returns a stamped ValueRef
        // when the producing path populated the relevant payload bytes; the
        // common case for ingest-sourced media values.
        ValueRef? stamped = TryReadInlineMetadata(accessor.Field, arg.InlineDataValue);
        if (stamped is { } v)
        {
            return v;
        }

        // Slow path: delegate to the original function's full decode. The
        // registry lookup mirrors EvaluateFunctionAsValueRefAsync's path so
        // any test-time function shadow / override is honoured here too.
        IScalarFunction? fallback = _functions.TryGetScalar(descriptor.FunctionName);
        if (fallback is null)
        {
            throw new InvalidOperationException(
                $"Inline accessor fallback function '{descriptor.FunctionName}' is not registered. " +
                "The elider produced an InlineAccessorExpression but the registry lost the function — " +
                "this indicates a planner/registry inconsistency.");
        }

        ValueRef[] arguments = ArrayPool<ValueRef>.Shared.Rent(1);
        try
        {
            arguments[0] = arg;
            using Activity? scalarSpan = DatumActivity.Scalars.StartActivity(descriptor.FunctionName);
            ValueRef result = await fallback.ExecuteAsync(arguments.AsMemory(0, 1), frame, cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            arguments.AsSpan(0, 1).Clear();
            ArrayPool<ValueRef>.Shared.Return(arguments);
        }
    }

    /// <summary>
    /// Reads the inline-metadata byte(s) for <paramref name="field"/> off
    /// <paramref name="dv"/>. Returns <see langword="null"/> when the bytes
    /// read as the zero "unstamped" sentinel — the caller falls back to
    /// the original function's full decode path. Each accessor here
    /// mirrors the fast-path branch of the matching
    /// <see cref="IInlineMetadataAccessor"/> function.
    /// </summary>
    private static ValueRef? TryReadInlineMetadata(InlineAccessorField field, DataValue dv)
    {
        switch (field)
        {
            case InlineAccessorField.ImageWidth:
                ushort iw = dv.ImageWidth;
                return iw != 0 ? ValueRef.FromInt32(iw) : null;
            case InlineAccessorField.ImageHeight:
                ushort ih = dv.ImageHeight;
                return ih != 0 ? ValueRef.FromInt32(ih) : null;
            case InlineAccessorField.ImageChannels:
                byte ic = dv.ImageChannels;
                return ic != 0 ? ValueRef.FromInt32(ic) : null;
            case InlineAccessorField.AudioSampleRate:
                uint rate = dv.AudioSampleRate;
                return rate != 0 ? ValueRef.FromInt32(checked((int)rate)) : null;
            case InlineAccessorField.VideoWidth:
                ushort vw = dv.VideoWidth;
                return vw != 0 ? ValueRef.FromInt32(vw) : null;
            case InlineAccessorField.VideoHeight:
                ushort vh = dv.VideoHeight;
                return vh != 0 ? ValueRef.FromInt32(vh) : null;
            case InlineAccessorField.PointCloudCount:
                uint pc = dv.PointCloudCount;
                return pc != 0 ? ValueRef.FromInt32(checked((int)pc)) : null;
            case InlineAccessorField.PointCloudHasColor:
                byte pcAttrs = dv.PointCloudAttributes;
                return pcAttrs != 0
                    ? ValueRef.FromBoolean((pcAttrs & (byte)Model.Spatial.PointCloudFlags.HasColor) != 0)
                    : null;
            case InlineAccessorField.MeshVertexCount:
                uint mv = dv.MeshVertexCount;
                return mv != 0 ? ValueRef.FromInt32(checked((int)mv)) : null;
            case InlineAccessorField.MeshTriangleCount:
                uint mt = dv.MeshTriangleCount;
                return mt != 0 ? ValueRef.FromInt32(checked((int)mt)) : null;
            case InlineAccessorField.StringByteLength:
                // Inline String: byte length cached in _charCount.low — always
                // present for inline strings. Non-inline reaches here as a
                // managed-string ValueRef whose InlineDataValue is the null
                // carrier (IsInline=false) — return null to fall back to the
                // function's char-walk on the materialised string.
                if (dv.IsInline && dv.Kind == DataKind.String)
                {
                    return ValueRef.FromInt32(dv.StringUtf8ByteLength);
                }
                return null;
            case InlineAccessorField.StringCodePointLength:
                // Inline String: code-point count cached in _charCount.high.
                // Non-inline: fall back to the function's Rune walk.
                if (dv.IsInline && dv.Kind == DataKind.String)
                {
                    return ValueRef.FromInt32(dv.InlineStringCodePointCount);
                }
                return null;
            default:
                throw new InvalidOperationException($"Unhandled InlineAccessorField: {field}");
        }
    }
}
