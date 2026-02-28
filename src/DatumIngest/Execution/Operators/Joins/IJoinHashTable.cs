using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Joins;

/// <summary>
/// Hash-table abstraction for in-memory equi-join build/probe. Hides the
/// single-key / composite-key split that <see cref="JoinOperator"/> repeats
/// across build, sequential probe, parallel probe, and bloom-pruning paths.
/// </summary>
/// <remarks>
/// <para>
/// Lifecycle is split into two phases:
/// </para>
/// <list type="number">
/// <item><description><b>Build (single-threaded)</b> — caller invokes
/// <see cref="TryEvaluateAndInsertAsync"/> for each build row. The table
/// owns its build-side scratch. Returns <see langword="false"/> when the
/// evaluated build key is null so the caller can flag
/// <c>hasNullKey</c> (NOT IN semantics).</description></item>
/// <item><description><b>Probe (thread-safe after build)</b> — multiple
/// workers may call <see cref="ProbeAsync"/> concurrently. Each worker
/// supplies its own scratch buffer (or <see langword="null"/> for the
/// single-key path). The result reports both whether the probe key was
/// null (for null-sensitive anti-semi early-exit) and the matched
/// bucket.</description></item>
/// </list>
/// </remarks>
internal interface IJoinHashTable
{
    /// <summary>Number of distinct keys currently materialized.</summary>
    int Count { get; }

    /// <summary>Number of GROUP BY key columns (1 for single-key tables).</summary>
    int KeyCount { get; }

    /// <summary>
    /// Build-side key expressions. The table evaluates these against build
    /// rows during <see cref="TryEvaluateAndInsertAsync"/>.
    /// </summary>
    IReadOnlyList<Expression> BuildKeyExpressions { get; }

    /// <summary>
    /// Probe-side key expressions. The table evaluates these against probe
    /// rows during <see cref="ProbeAsync"/>.
    /// </summary>
    IReadOnlyList<Expression> ProbeKeyExpressions { get; }

    /// <summary>
    /// Evaluates the build-side key for this row and, if non-null, inserts
    /// <c>(buildIndex, buildRow)</c> into the bucket for that key. Returns
    /// <see langword="false"/> when the key is null so the caller can flag
    /// <c>hasNullKey</c>.
    /// </summary>
    ValueTask<bool> TryEvaluateAndInsertAsync(
        ExpressionEvaluator evaluator,
        Row buildRow,
        int buildIndex,
        CancellationToken cancellationToken);

    /// <summary>
    /// Evaluates the probe-side key for this row, then looks it up in the
    /// table. Thread-safe — each worker passes its own
    /// <paramref name="probeKeyScratch"/> for composite keys.
    /// </summary>
    /// <param name="evaluator">Per-call (or per-worker) expression evaluator.</param>
    /// <param name="probeRow">The probe-side row whose key should be evaluated.</param>
    /// <param name="probeKeyScratch">
    /// Caller-owned buffer of length &gt;= <see cref="KeyCount"/>. Ignored
    /// for single-key tables.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// A struct reporting whether the key was null (so the caller can
    /// short-circuit on null-sensitive anti-semi) and the matched bucket
    /// (<see langword="null"/> when no matches).
    /// </returns>
    ValueTask<JoinHashProbeResult> ProbeAsync(
        ExpressionEvaluator evaluator,
        Row probeRow,
        DataValue[]? probeKeyScratch,
        CancellationToken cancellationToken);

    /// <summary>
    /// Adds every distinct value that appears at column index
    /// <paramref name="keyIndex"/> across all stored keys into
    /// <paramref name="destination"/>. Used by bloom / sorted-index pruning
    /// to push the build-side key set down to the probe-side scans.
    /// </summary>
    void CollectDistinctKeysAt(int keyIndex, HashSet<DataValue> destination);
}

/// <summary>
/// Result of an <see cref="IJoinHashTable.ProbeAsync"/> call.
/// </summary>
/// <param name="KeyIsNull">
/// True if any component of the probe key was null. Caller short-circuits
/// when this is set under null-sensitive anti-semi (NOT IN) semantics.
/// </param>
/// <param name="Matches">
/// The matched bucket, or <see langword="null"/> when no matches exist
/// (either because the key was null or no build row shared the key).
/// </param>
internal readonly record struct JoinHashProbeResult(
    bool KeyIsNull,
    List<(int Index, Row Row)>? Matches);
