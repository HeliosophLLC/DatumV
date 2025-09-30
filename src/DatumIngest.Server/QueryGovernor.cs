namespace DatumIngest.Server;

/// <summary>
/// Immutable set of resource governance limits for a session. Each limit is
/// optional: <see langword="null"/> means the limit is not enforced.
/// </summary>
/// <param name="QueryTimeoutSeconds">
/// Maximum wall-clock time a query may run before the server cancels it,
/// or <see langword="null"/> for no deadline.
/// </param>
/// <param name="MaxOutputRows">
/// Maximum number of rows the server will stream back for a single query,
/// or <see langword="null"/> for no limit.
/// </param>
/// <param name="ThrottleDelayMilliseconds">
/// Artificial delay injected every <see cref="ThrottleBatchSize"/> rows to
/// yield CPU to other sessions, or <see langword="null"/> for no throttle.
/// </param>
/// <param name="MaxQueryUnits">
/// Maximum Query Units a single query may accumulate before the server
/// rejects it, or <see langword="null"/> for no limit.
/// </param>
/// <param name="MemoryBudgetBytes">
/// Memory budget in bytes for spill-to-disk joins. When set, hash joins
/// that exceed this budget will partition and spill to temporary files.
/// <see langword="null"/> means no budget (joins are fully in-memory).
/// </param>
/// <param name="MaxConcurrentQueries">
/// Maximum number of queries that may execute simultaneously on a single
/// session. <see langword="null"/> means no limit.
/// </param>
/// <param name="MaxStratifyClasses">
/// Maximum number of distinct classes allowed in a TABLESAMPLE BALANCED
/// stratification column. Limits the number of per-class reservoirs to
/// bound memory usage. <see langword="null"/> means use the operator's
/// internal default (10,000).
/// </param>
public sealed record QueryGovernor(
    int? QueryTimeoutSeconds,
    long? MaxOutputRows,
    int? ThrottleDelayMilliseconds,
    long? MaxQueryUnits = null,
    long? MemoryBudgetBytes = null,
    int? MaxConcurrentQueries = null,
    int? MaxStratifyClasses = null)
{
    /// <summary>
    /// Number of rows between throttle delays. When <see cref="ThrottleDelayMilliseconds"/>
    /// is set, the server pauses every this many rows.
    /// </summary>
    public const int ThrottleBatchSize = 100;

    /// <summary>
    /// A governor with no limits. Used when no governance is configured.
    /// </summary>
    public static readonly QueryGovernor Unlimited = new(null, null, null);

    /// <summary>
    /// Merges per-session request values with server-wide defaults. The merge
    /// follows three-state semantics for each field:
    /// <list type="bullet">
    ///   <item><description><c>0</c> — use the server default.</description></item>
    ///   <item><description>Positive — override with the request value.</description></item>
    ///   <item><description>Negative — explicitly disable (no limit).</description></item>
    /// </list>
    /// </summary>
    /// <param name="serverDefaults">Server-wide default governor built from configuration.</param>
    /// <param name="requestTimeoutSeconds">Timeout override from the client request.</param>
    /// <param name="requestMaxOutputRows">Row budget override from the client request.</param>
    /// <param name="requestThrottleDelayMilliseconds">Throttle override from the client request.</param>
    /// <param name="requestMaxQueryUnits">Query Unit budget override from the client request.</param>
    /// <param name="requestMemoryBudgetBytes">Memory budget override from the client request.</param>
    /// <param name="requestMaxConcurrentQueries">Concurrent query limit override from the client request.</param>
    /// <param name="requestMaxStratifyClasses">Stratify class limit override from the client request.</param>
    /// <returns>A merged governor reflecting the effective limits for the session.</returns>
    public static QueryGovernor Merge(
        QueryGovernor serverDefaults,
        int requestTimeoutSeconds,
        long requestMaxOutputRows,
        int requestThrottleDelayMilliseconds,
        long requestMaxQueryUnits = 0,
        long requestMemoryBudgetBytes = 0,
        int requestMaxConcurrentQueries = 0,
        int requestMaxStratifyClasses = 0)
    {
        return new QueryGovernor(
            ResolveInt(requestTimeoutSeconds, serverDefaults.QueryTimeoutSeconds),
            ResolveLong(requestMaxOutputRows, serverDefaults.MaxOutputRows),
            ResolveInt(requestThrottleDelayMilliseconds, serverDefaults.ThrottleDelayMilliseconds),
            ResolveLong(requestMaxQueryUnits, serverDefaults.MaxQueryUnits),
            ResolveLong(requestMemoryBudgetBytes, serverDefaults.MemoryBudgetBytes),
            ResolveInt(requestMaxConcurrentQueries, serverDefaults.MaxConcurrentQueries),
            ResolveInt(requestMaxStratifyClasses, serverDefaults.MaxStratifyClasses));
    }

    /// <summary>
    /// Resolves an <see cref="int"/> governance field using three-state semantics.
    /// </summary>
    private static int? ResolveInt(int requestValue, int? serverDefault)
    {
        return requestValue switch
        {
            < 0 => null,
            0 => serverDefault,
            _ => requestValue,
        };
    }

    /// <summary>
    /// Resolves a <see cref="long"/> governance field using three-state semantics.
    /// </summary>
    private static long? ResolveLong(long requestValue, long? serverDefault)
    {
        return requestValue switch
        {
            < 0 => null,
            0 => serverDefault,
            _ => requestValue,
        };
    }
}
