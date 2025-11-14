using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Per-call context shared by every function invocation in the pipeline. Carries
/// the value stores involved in resolving and materializing reference-type payloads
/// plus the optional sidecar registry. Both scalar functions (via
/// <c>EvaluationFrame</c>, which composes this struct) and aggregate functions
/// (which take it directly) receive an <see cref="InvocationFrame"/> at every call
/// boundary so they can read arena-backed inputs and emit results into a caller-
/// chosen target arena rather than guessing where to write.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Source"/> backs <em>input</em> values: arena-backed payloads in
/// argument <see cref="DataValue"/>s resolve via <see cref="DataValue.AsString(IValueStore)"/>
/// and friends against this store. For aggregate <c>Accumulate</c> calls,
/// <see cref="Source"/> is typically the input batch's arena.
/// </para>
/// <para>
/// <see cref="Target"/> is the home for any newly-materialized values the function
/// produces — string concatenations, array constructions, etc. Aggregate
/// <c>Result</c> calls receive a frame whose <see cref="Target"/> is the output
/// batch's arena (or a per-emit-batch arena), so result payloads land directly in
/// the right place.
/// </para>
/// <para>
/// <see cref="SidecarRegistry"/> resolves <c>FlagInSidecar</c> DataValues whose
/// payloads live in <c>.datum-blob</c> sidecars. Threaded through from
/// <c>ExecutionContext</c>; null outside the query pipeline.
/// </para>
/// </remarks>
public readonly struct InvocationFrame
{
    /// <summary>Store backing input values' non-inline payloads. Read path.</summary>
    public IValueStore Source { get; }

    /// <summary>Store into which newly-materialized result values are written. Write path.</summary>
    public IValueStore Target { get; }

    /// <summary>
    /// Optional registry mapping <c>storeId</c> bytes to <see cref="IBlobSource"/>
    /// instances for <c>FlagInSidecar</c> DataValues. Null when the call is
    /// outside a sidecar-bound query (e.g. unit tests, expression evaluation
    /// over inline-only data).
    /// </summary>
    public SidecarRegistry? SidecarRegistry { get; }

    /// <summary>
    /// Creates an invocation frame. Pass the same store for <paramref name="source"/>
    /// and <paramref name="target"/> when the distinction doesn't matter (e.g.
    /// numeric-only aggregates). Pass <paramref name="sidecarRegistry"/> when the
    /// query touches sidecar-bound tables so accessors like <c>AsImage</c> can
    /// resolve sidecar-backed values.
    /// </summary>
    public InvocationFrame(
        IValueStore source,
        IValueStore target,
        SidecarRegistry? sidecarRegistry = null)
    {
        Source = source;
        Target = target;
        SidecarRegistry = sidecarRegistry;
    }

    /// <summary>
    /// Returns a frame that uses the same store for both reading and writing.
    /// Convenience for call sites that don't care to distinguish source from target.
    /// </summary>
    public static InvocationFrame Symmetric(IValueStore store, SidecarRegistry? sidecarRegistry = null)
        => new(store, store, sidecarRegistry);
}
