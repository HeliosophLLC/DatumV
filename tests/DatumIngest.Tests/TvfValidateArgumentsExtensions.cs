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
        // Tests that go through this overload don't pass constants, so the
        // store is never read — handing in a fresh ByteArrayValueStore is
        // strictly defensive against future TVFs that might dereference it
        // regardless of whether constants populate.
        ByteArrayValueStore noopStore = new();
        return function.ValidateArguments(argumentKinds, noConstants, noopStore, cancellationToken: default);
    }
}
