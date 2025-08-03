using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Optional extension interface for scalar functions whose return type depends on
/// the element kind of one or more <see cref="DataKind.Array"/> arguments.
/// </summary>
/// <remarks>
/// <para>
/// The base <see cref="IScalarFunction.ValidateArguments"/> method only receives
/// <see cref="DataKind"/> discriminators and therefore cannot distinguish
/// <c>Array&lt;Scalar&gt;</c> from <c>Array&lt;String&gt;</c>. Functions like
/// <c>ARRAY_GET</c>, <c>ARRAY_MIN</c>, and <c>ARRAY_MAX</c> must return the
/// element kind, not a fixed kind — so they require richer type information.
/// </para>
/// <para>
/// When <see cref="DatumIngest.Execution.ExpressionTypeResolver"/> encounters a
/// function that implements this interface, it calls
/// <see cref="ValidateArgumentsWithElementKinds"/> instead of the base
/// <see cref="IScalarFunction.ValidateArguments"/>, supplying the per-argument
/// array element kinds extracted from the source schema.
/// </para>
/// <para>
/// Implementations should handle the case where array element kinds are
/// <c>null</c> — this happens when the element kind of an
/// <see cref="DataKind.Array"/> argument is unknown at plan time (e.g. a subquery
/// or computed expression). In that scenario the function should return a
/// reasonable fallback kind rather than throw.
/// </para>
/// </remarks>
public interface IElementKindAwareFunction : IScalarFunction
{
    /// <summary>
    /// Validates argument types including array element kind metadata, and
    /// returns the result <see cref="DataKind"/>.
    /// </summary>
    /// <param name="argumentKinds">
    /// The kinds of the arguments being passed. Identical to the span passed to
    /// <see cref="IScalarFunction.ValidateArguments"/>.
    /// </param>
    /// <param name="arrayElementKinds">
    /// For each position in <paramref name="argumentKinds"/>: the array element
    /// kind when <c>argumentKinds[i] == DataKind.Array</c> and the element kind
    /// is known at plan time; otherwise <c>null</c>.
    /// </param>
    /// <returns>The kind of the result value.</returns>
    /// <exception cref="ArgumentException">The argument types are not valid for this function.</exception>
    DataKind ValidateArgumentsWithElementKinds(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataKind?> arrayElementKinds);
}


