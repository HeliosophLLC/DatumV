using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

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
    public ValueRef Execute(ReadOnlySpan<ValueRef> arguments, in EvaluationFrame frame)
    {
        ValueRef input = arguments[0];
        DataKind targetKind = CastFunction.ResolveTargetKind(arguments[1]);

        if (input.IsNull)
        {
            return ValueRef.Null(targetKind);
        }

        if (input.Kind == targetKind)
        {
            return input;
        }

        if (CastFunction.TryCastCore(input, targetKind, out ValueRef result))
        {
            return result;
        }

        return ValueRef.Null(targetKind);
    }
}
