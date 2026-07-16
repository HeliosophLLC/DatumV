using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Static-abstract metadata interface for registered aggregate functions.
/// Mirrors <see cref="IFunction"/> for scalars: carries the canonical name,
/// category, description, and accepted signature shapes so the catalog and
/// language-server tooling can describe aggregates without instantiating
/// their classes.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="IAggregateFunction"/> because interfaces
/// with static abstract members can't be used as generic type arguments
/// (Dictionary&lt;string, IAggregateFunction&gt; would fail with CS8920).
/// Implementing classes implement both <see cref="IAggregateFunctionMetadata"/>
/// (for metadata) and <see cref="IAggregateFunction"/> (for instance dispatch).
/// </remarks>
public interface IAggregateFunctionMetadata
{
    /// <summary>The aggregate's canonical name (case-insensitive).</summary>
    static abstract string Name { get; }

    /// <summary>Functional category (drives grouping in completion / catalog views).</summary>
    static abstract FunctionCategory Category { get; }

    /// <summary>Human-readable description for hover / catalog text.</summary>
    static abstract string Description { get; }

    /// <summary>
    /// Accepted argument shapes. Mirrors <see cref="IFunction.Signatures"/>;
    /// each variant's return rule resolves the per-element kind (for
    /// array-producing aggregates, array-ness lives on the variant's
    /// <see cref="FunctionSignatureVariant.ReturnType"/>).
    /// </summary>
    static abstract IReadOnlyList<FunctionSignatureVariant> Signatures { get; }
}

/// <summary>
/// How a SQL aggregate uses the <c>WITHIN GROUP (ORDER BY …)</c> clause.
/// Two SQL-standard semantics are encoded; aggregates that don't model
/// either declare <see cref="NotSupported"/> and the planner rejects
/// the syntax.
/// </summary>
public enum WithinGroupSemantics
{
    /// <summary>
    /// The clause specifies sort order only. The aggregate's data
    /// arguments come from inside the parens. Examples:
    /// <c>STRING_AGG(expr, sep) WITHIN GROUP (ORDER BY x)</c>,
    /// <c>ARRAY_AGG(expr) WITHIN GROUP (ORDER BY x)</c>. The planner
    /// uses the ORDER BY to sort rows before accumulation; arguments
    /// are not modified.
    /// </summary>
    SortModifier,

    /// <summary>
    /// The clause supplies the <em>data</em> being aggregated.
    /// Arguments inside the parens are configuration (or empty).
    /// Examples: <c>MODE() WITHIN GROUP (ORDER BY col)</c>,
    /// <c>PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY salary)</c>.
    /// The planner prepends the ORDER BY expressions to the
    /// argument list before validation, so the aggregate sees its
    /// data column at <c>arguments[0]</c>.
    /// </summary>
    OrderedSet,

    /// <summary>
    /// The aggregate doesn't accept <c>WITHIN GROUP</c>. Using the
    /// clause raises a plan-time error. Default for regular
    /// aggregates like <c>SUM</c> / <c>AVG</c> / <c>COUNT</c> if they
    /// opt in (or stick with the interface default).
    /// </summary>
    NotSupported,
}

/// <summary>
/// Interface for aggregate SQL functions that accumulate values across
/// multiple rows and produce a single result per group.
/// </summary>
public interface IAggregateFunction
{
    /// <summary>The SQL function name (case-insensitive matching).</summary>
    string Name { get; }

    /// <summary>
    /// Validates the argument types and returns the result kind. For aggregates
    /// whose <see cref="ReturnRule"/> reports
    /// <see cref="ReturnTypeRule.ProducesArray"/> = <see langword="true"/>, this
    /// returns the <em>element</em> kind; array-ness is communicated through
    /// <see cref="ReturnRule"/>. Scalar aggregates return their result kind
    /// directly.
    /// </summary>
    /// <param name="argumentKinds">The kinds of the arguments being passed.</param>
    /// <returns>
    /// The kind of the (per-element, when array-producing) result value.
    /// </returns>
    /// <exception cref="ArgumentException">The argument types are not valid for this function.</exception>
    DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>
    /// Creates a new accumulator instance for a single group. Each group in a
    /// GROUP BY query gets its own accumulator. Per-call context (source/target
    /// stores, sidecar registry) flows in through the
    /// <see cref="InvocationFrame"/> passed to <c>Accumulate</c> and
    /// <c>Result</c>; the accumulator does not need it at construction time.
    /// </summary>
    IAggregateAccumulator CreateAccumulator();

    /// <summary>
    /// Optional rule describing the result shape. Aggregates that produce a
    /// typed array (e.g. <c>ARRAY_AGG</c>) override this with
    /// <see cref="ReturnTypeRule.ArrayOf"/> so the type resolver can detect
    /// array-ness without a separate boolean flag. Scalar aggregates leave it
    /// <see langword="null"/>; <see cref="ValidateArguments"/> remains
    /// authoritative for the result kind.
    /// </summary>
    ReturnTypeRule? ReturnRule => null;

    /// <summary>
    /// Optional plan-time declaration of the result's struct field layout, for
    /// aggregates whose result (or result element, when <see cref="ReturnRule"/>
    /// declares an array) is a struct with a signature-determined shape.
    /// Returning a field list lets the schema resolver type field access over
    /// the result — and CTAS persist extracted fields with concrete kinds —
    /// instead of falling back to an opaque, fields-less Struct. Return
    /// <see langword="null"/> (the default) when the shape is unknown or
    /// runtime-dependent.
    /// </summary>
    IReadOnlyList<ColumnInfo>? ResolveResultFields(ReadOnlySpan<DataKind> argumentKinds) => null;

    /// <summary>
    /// How this aggregate consumes <c>WITHIN GROUP (ORDER BY …)</c>.
    /// Default <see cref="WithinGroupSemantics.NotSupported"/> matches
    /// PostgreSQL strictness — most aggregates (<c>SUM</c> / <c>AVG</c> /
    /// <c>COUNT</c> / <c>MIN</c> / <c>MAX</c> / etc.) reject the clause,
    /// and surfacing a clear error is more LLM-friendly than silently
    /// accepting the syntax. Aggregates that take an ordered set as
    /// data (<c>MODE</c>, <c>PERCENTILE_CONT</c>, <c>PERCENTILE_DISC</c>)
    /// declare <see cref="WithinGroupSemantics.OrderedSet"/>; aggregates
    /// that support sort-only modification (<c>STRING_AGG</c>,
    /// <c>ARRAY_AGG</c>) declare <see cref="WithinGroupSemantics.SortModifier"/>.
    /// </summary>
    WithinGroupSemantics WithinGroupSemantics => WithinGroupSemantics.NotSupported;
}

/// <summary>
/// Mutable accumulator that collects row values for a single aggregate
/// function within a single group. Created by
/// <see cref="IAggregateFunction.CreateAccumulator"/> and used by the
/// <c>GroupByOperator</c> to compute per-group results.
/// </summary>
public interface IAggregateAccumulator
{
    /// <summary>
    /// Incorporates one row's argument values into the running aggregate. The
    /// <paramref name="frame"/> resolves arena-backed argument payloads through
    /// <see cref="InvocationFrame.Source"/> and provides
    /// <see cref="InvocationFrame.Target"/> for any state the accumulator must
    /// stabilise across the source batch's lifetime (e.g. running min/max
    /// strings, accumulated string lists).
    /// </summary>
    /// <param name="arguments">The evaluated argument values for this row.</param>
    /// <param name="frame">Per-call invocation context.</param>
    void Accumulate(ReadOnlySpan<DataValue> arguments, in InvocationFrame frame);

    /// <summary>
    /// Merges the state of another accumulator (of the same concrete type) into
    /// this one. Used by parallel hash aggregate to combine thread-local partial
    /// aggregations into a single result per group. The <paramref name="other"/>
    /// accumulator must not be used after merging.
    /// <para>
    /// Both accumulators were constructed and accumulated against the same Target
    /// store (per the parallel-aggregate contract — workers share
    /// <c>context.Store</c>), so any arena-backed payloads in either side resolve
    /// against <paramref name="frame"/>'s stores. Frame-aware Merge lets
    /// implementations like <c>Min</c>/<c>Max</c>/<c>ArgMax</c> use the store-aware
    /// <c>DataValueComparer.Compare</c> overload when comparing captured values
    /// across the merge.
    /// </para>
    /// <para>
    /// Returns <see cref="ValueTask"/> so accumulators that drain spilled state
    /// (e.g. <c>DistinctAccumulatorDecorator</c>) can do so without sync-over-async
    /// bridging. Most accumulators complete synchronously and return
    /// <see cref="ValueTask.CompletedTask"/>.
    /// </para>
    /// </summary>
    /// <param name="other">
    /// The accumulator to merge into this one. Must be the same concrete type.
    /// </param>
    /// <param name="frame">Per-call invocation context.</param>
    ValueTask MergeAsync(IAggregateAccumulator other, InvocationFrame frame);

    /// <summary>
    /// Computes the current aggregate result. The <paramref name="frame"/>'s
    /// <see cref="InvocationFrame.Target"/> is the home for any non-inline
    /// payloads in the returned <see cref="DataValue"/> — string concatenations,
    /// array constructions, etc. Inline-result accumulators (Sum, Count, Avg)
    /// may ignore the frame.
    /// <para>
    /// Returns <see cref="ValueTask{DataValue}"/> so accumulators that drain
    /// spilled state can do so without sync-over-async bridging. Most accumulators
    /// complete synchronously and return <c>new ValueTask&lt;DataValue&gt;(...)</c>.
    /// </para>
    /// </summary>
    /// <param name="frame">Per-emit invocation context.</param>
    ValueTask<DataValue> ResultAsync(InvocationFrame frame);

    /// <summary>
    /// Resets the accumulator to its initial (empty) state so it can be reused
    /// for a different group without allocating a new instance. Implementations
    /// must clear all mutable state but should retain allocated collection
    /// capacity to avoid re-allocation on the next group.
    /// </summary>
    void Reset();
}
