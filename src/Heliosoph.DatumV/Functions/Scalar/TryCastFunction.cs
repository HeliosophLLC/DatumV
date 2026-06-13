using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar;

/// <summary>
/// Returns <c>cast(value, type)</c> on success, or a typed null when the
/// conversion would otherwise throw. Wraps <see cref="CastFunction"/>'s
/// dispatch table and returns null on the same set of failure cases —
/// unsupported pairs and parse failures.
/// </summary>
/// <remarks>
/// Rebuilt clean per <c>project_function_rebuild.md</c>: behaviour beyond
/// the pairs <see cref="CastFunction"/> supports is intentionally not
/// carried forward from the prior implementation. Add pairs to
/// <see cref="CastFunction"/> first; both functions get them.
/// </remarks>
public sealed class TryCastFunction : IFunction, IScalarFunction
{
    /// <inheritdoc />
    public static string Name => "try_cast";

    /// <inheritdoc />
    public static FunctionCategory Category => FunctionCategory.Conversion;

    /// <inheritdoc />
    public static string Description =>
        "Like cast(), but returns the typed null of the target kind instead of throwing on failure.";

    /// <inheritdoc />
    public static IReadOnlyList<FunctionSignatureVariant> Signatures => CastFunction.Signatures;

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds) =>
        FunctionMetadata.Validate<TryCastFunction>(argumentKinds);

    /// <inheritdoc />
    public ValueTask<ValueRef> ExecuteAsync(
        ReadOnlyMemory<ValueRef> arguments,
        EvaluationFrame frame,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<ValueRef> args = arguments.Span;
        ValueRef input = args[0];
        (DataKind targetKind, bool targetIsArray) = CastFunction.ResolveTarget(args[1]);

        if (targetIsArray)
        {
            if (input.IsNull)
                return new ValueTask<ValueRef>(ValueRef.NullArray(targetKind));
            if (input.IsArray && input.Kind == targetKind)
                return new ValueTask<ValueRef>(input);
            return new ValueTask<ValueRef>(ValueRef.NullArray(targetKind));
        }

        // Scalar target: arrays cannot be flattened. Without this, TryCastCore
        // can succeed via the array's underlying numeric carrier (TryToDouble
        // reads the first element / offset) and emit a misleading scalar.
        // Exception: Array<UInt8> → encoded-media-blob falls through so
        // TryCastCore performs the zero-copy retag.
        if (input.IsArray && !CastFunction.IsByteArrayToBlobConversion(input, targetKind))
        {
            return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
        }

        if (input.IsNull)
        {
            return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
        }

        if (input.Kind == targetKind)
        {
            return new ValueTask<ValueRef>(input);
        }

        if (CastFunction.TryCastCore(input, targetKind, out ValueRef result))
        {
            return new ValueTask<ValueRef>(result);
        }

        return new ValueTask<ValueRef>(ValueRef.Null(targetKind));
    }
}
