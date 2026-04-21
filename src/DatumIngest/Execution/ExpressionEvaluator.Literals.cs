using System.Buffers;
using DatumIngest.Functions;
using DatumIngest.Functions.Audio;
using DatumIngest.Functions.Image;
using DatumIngest.Functions.Video;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

public sealed partial class ExpressionEvaluator
{
    private static DataValue EvaluateLiteral(LiteralExpression literal, EvaluationFrame frame)
    {
        if (literal.Value is null)
        {
            return DataValue.UnknownNull();
        }

        return literal.Value switch
        {
            DataValue dataValue => dataValue,
            sbyte int8Value => DataValue.FromInt8(int8Value),
            short int16Value => DataValue.FromInt16(int16Value),
            int intValue => DataValue.FromInt32(intValue),
            long longValue => DataValue.FromInt64(longValue),
            ulong uint64Value => DataValue.FromUInt64(uint64Value),
            Int128 int128Value => DataValue.FromInt128(int128Value),
            UInt128 uint128Value => DataValue.FromUInt128(uint128Value),
            float floatValue => DataValue.FromFloat32(floatValue),
            double doubleValue => DataValue.FromFloat64(doubleValue),
            string stringValue => DataValue.FromString(stringValue, frame.Target),
            bool boolValue => DataValue.FromBoolean(boolValue),
            BinaryParameter binary => MaterializeBinaryParameter(binary, frame.Target),
            _ => throw new InvalidOperationException(
                $"Unsupported literal type: {literal.Value.GetType().Name}."),
        };
    }

    /// <summary>
    /// Materialises a <see cref="BinaryParameter"/> wrapper (carried in a
    /// <see cref="LiteralExpression"/> by the parameter binder for binary
    /// parameters delivered as multipart parts) into a <see cref="DataValue"/>
    /// against the active query store. Lazy materialisation keeps the
    /// parameter binder unaware of the per-query <see cref="IValueStore"/>.
    /// </summary>
    private static DataValue MaterializeBinaryParameter(BinaryParameter binary, IValueStore target)
    {
        return binary.Kind switch
        {
            DataKind.Image => ImageDataValueFactory.FromEncodedBytes(binary.Bytes, target),
            DataKind.Audio => AudioDataValueFactory.FromEncodedBytes(binary.Bytes, target),
            DataKind.Video => VideoDataValueFactory.FromEncodedBytes(binary.Bytes, target),
            DataKind.UInt8 => DataValue.FromByteArray(binary.Bytes, target),
            _ => throw new InvalidOperationException(
                $"BinaryParameter kind {binary.Kind} is not a recognised binary kind. " +
                "Use Image / Audio / Video / UInt8."),
        };
    }

    private static DataValue EvaluateTypeLiteral(TypeLiteralExpression typeLiteral)
    {
        if (!Enum.TryParse<DataKind>(typeLiteral.TypeName, ignoreCase: true, out DataKind kind))
        {
            throw new InvalidOperationException(
                $"Unknown type name: '{typeLiteral.TypeName}'.");
        }

        return DataValue.FromType(kind);
    }

    /// <summary>
    /// Validates a scalar function call's argument kinds via
    /// <see cref="IScalarFunction.ValidateArguments"/>. On failure, wraps the function's
    /// argument exception with the call site's source span and rethrows as an
    /// <see cref="ExpressionEvaluationException"/> so the user sees a clean
    /// <c>[Line N, Col C] foo(): expects ...</c> error.
    /// </summary>
    /// <summary>
    /// One-shot resolution of <see cref="ParameterCheck"/> bindings for a call site.
    /// Walks the registry descriptor, picks the variant whose parameter kinds match
    /// the arguments, and caches the (slot, name, check) tuples for the parameters
    /// that declared a check. Empty array is cached when nothing needs checking —
    /// the per-row dispatch still does a single dictionary lookup but skips the walk.
    /// </summary>
    private void ResolveParameterCheckBindings(
        FunctionCallExpression function,
        ReadOnlySpan<ValueRef> arguments)
    {
        FunctionDescriptor? descriptor = _functions.TryGetScalarDescriptor(function.CallName);
        if (descriptor is null || descriptor.Signatures.Count == 0)
        {
            _siteParameterChecks[function] = Array.Empty<ParameterCheckBinding>();
            return;
        }

        DataKind[] kindsBuf = ArrayPool<DataKind>.Shared.Rent(arguments.Length);
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                kindsBuf[i] = arguments[i].Kind;
            }
            FunctionSignatureVariant? variant = FunctionMetadata.MatchVariant(
                descriptor.Signatures, kindsBuf.AsSpan(0, arguments.Length));
            if (variant is null)
            {
                // No variant matched — kinds-validation should have already
                // thrown above; defensive bail.
                _siteParameterChecks[function] = Array.Empty<ParameterCheckBinding>();
                return;
            }

            // Only the fixed-parameter prefix carries per-slot checks; variadic
            // slots use VariadicSpec which has no Metadata today.
            int fixedCount = Math.Min(arguments.Length, variant.Parameters.Count);
            List<ParameterCheckBinding>? bindings = null;
            for (int i = 0; i < fixedCount; i++)
            {
                ParameterCheck? check = variant.Parameters[i].Metadata?.Check;
                if (check is null) continue;
                bindings ??= [];
                bindings.Add(new ParameterCheckBinding(i, variant.Parameters[i].Name, check));
            }

            _siteParameterChecks[function] = bindings is null
                ? Array.Empty<ParameterCheckBinding>()
                : bindings.ToArray();
        }
        finally
        {
            ArrayPool<DataKind>.Shared.Return(kindsBuf);
        }
    }

    /// <summary>
    /// Per-row validation pass over the cached <see cref="ParameterCheck"/> bindings.
    /// Throws an <see cref="ExpressionEvaluationException"/> on the first failure,
    /// prefixed with the call site's line/column so the editor underlines the right
    /// place. NULL values pass any check (mirrors SQL <c>CHECK</c> semantics).
    /// </summary>
    private static void ValidateParameterChecksOrThrow(
        FunctionCallExpression function,
        ParameterCheckBinding[] bindings,
        ReadOnlySpan<ValueRef> arguments)
    {
        for (int i = 0; i < bindings.Length; i++)
        {
            ParameterCheckBinding binding = bindings[i];
            if (binding.ArgIndex >= arguments.Length) continue;
            string? error = binding.Check.Validate(arguments[binding.ArgIndex]);
            if (error is null) continue;

            SourceSpan? span = function.Span;
            string prefix = span is not null
                ? $"[Line {span.Line}, Col {span.Column}] "
                : string.Empty;
            FunctionArgumentException inner = new(
                function.CallName,
                $"parameter '{binding.ParamName}': {error}");
            throw new ExpressionEvaluationException($"{prefix}{inner.Message}", span, inner);
        }
    }

    private static void ValidateScalarCallSiteOrThrow(
        IScalarFunction scalarFunction,
        FunctionCallExpression function,
        ReadOnlySpan<ValueRef> arguments)
    {
        DataKind[] argumentKinds = ArrayPool<DataKind>.Shared.Rent(arguments.Length);
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                argumentKinds[i] = arguments[i].Kind;
            }

            try
            {
                scalarFunction.ValidateArguments(argumentKinds.AsSpan(0, arguments.Length));
            }
            catch (Exception ex) when (ex is ArgumentException || ex is FunctionArgumentException)
            {
                SourceSpan? span = function.Span;
                string prefix = span is not null
                    ? $"[Line {span.Line}, Col {span.Column}] "
                    : string.Empty;
                throw new ExpressionEvaluationException($"{prefix}{ex.Message}", span, ex);
            }
        }
        finally
        {
            ArrayPool<DataKind>.Shared.Return(argumentKinds);
        }
    }

    /// <summary>
    /// Fallback evaluation for <see cref="CurrentTimestampExpression"/> when the
    /// <see cref="TemporalConstantFolder"/> pass has not been applied (e.g. direct
    /// programmatic API usage). Uses <see cref="DateTimeOffset.UtcNow"/> as the clock.
    /// </summary>
    private static DataValue EvaluateTemporalConstant(CurrentTimestampExpression ct)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return ct.Kind switch
        {
            CurrentTimestampKind.CurrentDate => DataValue.FromDate(DateOnly.FromDateTime(now.UtcDateTime)),
            CurrentTimestampKind.CurrentTime => DataValue.FromTime(TimeOnly.FromTimeSpan(now.TimeOfDay)),
            // PG current_timestamp returns timestamptz.
            CurrentTimestampKind.CurrentTimestamp => DataValue.FromTimestampTz(now),
            _ => throw new InvalidOperationException($"Unknown CurrentTimestampKind: {ct.Kind}"),
        };
    }
}
