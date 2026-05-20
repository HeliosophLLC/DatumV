using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.BatchPredicates;

/// <summary>
/// A predicate that can evaluate a whole <see cref="RowBatch"/> in one call,
/// filling a per-row boolean mask. The architectural contract:
/// <list type="bullet">
///   <item><description><c>mask[i] == true</c> iff row <c>batch[i]</c> passes the predicate.</description></item>
///   <item><description>NULL values evaluate to <see langword="false"/> — matches WHERE-clause SQL semantics
///     (UNKNOWN collapses to false). Inner-row arithmetic isn't this predicate's concern;
///     compilation only happens for shapes where NULL → false is correct.</description></item>
///   <item><description>The predicate is stateless across batches — column ordinals and literals
///     are resolved at compile time.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// The point of this abstraction is to amortise per-batch fixed costs — column
/// ordinal lookups, operator dispatch, type dispatch — over an entire 1024-row
/// chunk instead of paying them per row. The inner loop is a tight, monomorphic
/// loop over scalar data, which is the prerequisite for both interpreted speed
/// wins (~50× over per-row) and any future SIMD vectorisation.
/// </remarks>
internal interface IBatchPredicate
{
    void Evaluate(RowBatch batch, Span<bool> mask);
}

/// <summary>
/// Comparison operators supported by the v1 batch predicate compiler. Mirrors
/// the subset of <see cref="Heliosoph.DatumV.Parsing.Ast.BinaryOperator"/> that
/// reduces to a pure scalar comparison — no LIKE/REGEXP, no IS NULL (that
/// gets its own predicate shape).
/// </summary>
internal enum BatchComparison
{
    Equal,
    NotEqual,
    LessThan,
    LessEqual,
    GreaterThan,
    GreaterEqual,
}
