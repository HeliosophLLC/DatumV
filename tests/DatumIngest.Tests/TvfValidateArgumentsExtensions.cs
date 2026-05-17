using DatumIngest.Functions;
using DatumIngest.Model;

namespace DatumIngest.Tests;

/// <summary>
/// Test-only convenience overload for
/// <see cref="ITableValuedFunction.ValidateArguments"/>: when a test only
/// cares about kind-based validation (the common case for fixed-schema
/// TVFs that ignore the constant-args hook), it can call the kinds-only
/// shape and this extension builds a matching-length all-null
/// <c>constantArguments</c> span for it. TVFs that read the constants
/// have their own per-test plumbing.
/// </summary>
internal static class TvfValidateArgumentsExtensions
{
    internal static Schema ValidateArguments(
        this ITableValuedFunction function,
        ReadOnlySpan<DataKind> argumentKinds)
    {
        DataValue?[] noConstants = new DataValue?[argumentKinds.Length];
        return function.ValidateArguments(argumentKinds, noConstants, cancellationToken: default);
    }
}
