using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Per-call context for <see cref="ExpressionEvaluator"/>. Carries the current row
/// together with the two arenas involved in expression evaluation:
/// <list type="bullet">
///   <item><description>
///     <see cref="Source"/> — the arena backing the current row's non-inline values.
///     Used whenever the evaluator reads a string, JSON, vector, array, or other
///     reference-type payload from a row column.
///   </description></item>
///   <item><description>
///     <see cref="Target"/> — the arena where newly-materialized values are written
///     during this evaluation (e.g. a string literal in a predicate, a concatenation
///     result, a substring slice). Callers that need the result to outlive the
///     current row batch should pass a long-lived arena here; callers that write
///     their result straight into an output batch should pass that batch's arena.
///   </description></item>
/// </list>
/// The two are passed separately because the streaming pipeline typically reads
/// from one batch's arena and writes into another — mixing them would either pin
/// the source batch or write results into a soon-to-be-recycled arena.
/// </summary>
public readonly struct EvaluationFrame
{
    /// <summary>The row being evaluated.</summary>
    public Row Row { get; }

    /// <summary>Arena backing the row's non-inline column values. Read path.</summary>
    public IValueStore Source { get; }

    /// <summary>Arena into which newly-materialized values should be written. Write path.</summary>
    public IValueStore Target { get; }

    /// <summary>
    /// Optional outer row for correlated-subquery column resolution. Column references
    /// that cannot be resolved against <see cref="Row"/> fall back to this row.
    /// </summary>
    public Row? OuterRow { get; }

    /// <summary>
    /// Creates an evaluation frame. Pass the same store for <paramref name="source"/>
    /// and <paramref name="target"/> when the distinction doesn't matter (e.g. predicates
    /// that produce only inline boolean results and don't allocate strings).
    /// </summary>
    public EvaluationFrame(Row row, IValueStore source, IValueStore target, Row? outerRow = null)
    {
        Row = row;
        Source = source;
        Target = target;
        OuterRow = outerRow;
    }

    /// <summary>
    /// Returns a new frame with a different <see cref="Row"/>, preserving the arenas
    /// and outer-row context. Used when the evaluator descends into a derived row
    /// (e.g. a lambda body's augmented row).
    /// </summary>
    public EvaluationFrame WithRow(Row row) => new(row, Source, Target, OuterRow);
}
