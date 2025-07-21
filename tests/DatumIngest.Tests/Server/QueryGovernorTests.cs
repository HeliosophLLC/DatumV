using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="QueryGovernor"/> merge semantics.
/// </summary>
public sealed class QueryGovernorTests
{
    // ─────────────────── Unlimited ───────────────────

    /// <summary>
    /// The static Unlimited instance has all limits set to null.
    /// </summary>
    [Fact]
    public void Unlimited_HasNoLimits()
    {
        QueryGovernor governor = QueryGovernor.Unlimited;

        Assert.Null(governor.QueryTimeoutSeconds);
        Assert.Null(governor.MaxOutputRows);
        Assert.Null(governor.ThrottleDelayMilliseconds);
    }

    // ─────────────────── Merge: zero uses server default ───────────────────

    /// <summary>
    /// When request values are zero, server defaults are used.
    /// </summary>
    [Fact]
    public void Merge_ZeroValues_UsesServerDefaults()
    {
        QueryGovernor serverDefaults = new(QueryTimeoutSeconds: 60, MaxOutputRows: 1000, ThrottleDelayMilliseconds: 10);

        QueryGovernor merged = QueryGovernor.Merge(serverDefaults, 0, 0, 0);

        Assert.Equal(60, merged.QueryTimeoutSeconds);
        Assert.Equal(1000, merged.MaxOutputRows);
        Assert.Equal(10, merged.ThrottleDelayMilliseconds);
    }

    /// <summary>
    /// When request values are zero and server defaults are null, limits remain null.
    /// </summary>
    [Fact]
    public void Merge_ZeroValues_NullServerDefaults_RemainsNull()
    {
        QueryGovernor merged = QueryGovernor.Merge(QueryGovernor.Unlimited, 0, 0, 0);

        Assert.Null(merged.QueryTimeoutSeconds);
        Assert.Null(merged.MaxOutputRows);
        Assert.Null(merged.ThrottleDelayMilliseconds);
    }

    // ─────────────────── Merge: positive overrides ───────────────────

    /// <summary>
    /// Positive request values override server defaults.
    /// </summary>
    [Fact]
    public void Merge_PositiveValues_OverridesServerDefaults()
    {
        QueryGovernor serverDefaults = new(QueryTimeoutSeconds: 60, MaxOutputRows: 1000, ThrottleDelayMilliseconds: 10);

        QueryGovernor merged = QueryGovernor.Merge(serverDefaults, 30, 500, 5);

        Assert.Equal(30, merged.QueryTimeoutSeconds);
        Assert.Equal(500, merged.MaxOutputRows);
        Assert.Equal(5, merged.ThrottleDelayMilliseconds);
    }

    /// <summary>
    /// Positive request values work even when server defaults are null.
    /// </summary>
    [Fact]
    public void Merge_PositiveValues_NullServerDefaults_UsesRequestValues()
    {
        QueryGovernor merged = QueryGovernor.Merge(QueryGovernor.Unlimited, 120, 5000, 20);

        Assert.Equal(120, merged.QueryTimeoutSeconds);
        Assert.Equal(5000, merged.MaxOutputRows);
        Assert.Equal(20, merged.ThrottleDelayMilliseconds);
    }

    // ─────────────────── Merge: negative disables ───────────────────

    /// <summary>
    /// Negative request values explicitly disable the limit, even when server defaults are set.
    /// </summary>
    [Fact]
    public void Merge_NegativeValues_ExplicitlyDisablesLimits()
    {
        QueryGovernor serverDefaults = new(QueryTimeoutSeconds: 60, MaxOutputRows: 1000, ThrottleDelayMilliseconds: 10);

        QueryGovernor merged = QueryGovernor.Merge(serverDefaults, -1, -1, -1);

        Assert.Null(merged.QueryTimeoutSeconds);
        Assert.Null(merged.MaxOutputRows);
        Assert.Null(merged.ThrottleDelayMilliseconds);
    }

    // ─────────────────── Merge: mixed values ───────────────────

    /// <summary>
    /// Different merge behaviors can be mixed in a single merge call.
    /// </summary>
    [Fact]
    public void Merge_MixedValues_AppliesCorrectSemantics()
    {
        QueryGovernor serverDefaults = new(QueryTimeoutSeconds: 60, MaxOutputRows: 1000, ThrottleDelayMilliseconds: 10);

        // Timeout: override to 30, MaxRows: use server default (1000), Throttle: disable.
        QueryGovernor merged = QueryGovernor.Merge(serverDefaults, 30, 0, -1);

        Assert.Equal(30, merged.QueryTimeoutSeconds);
        Assert.Equal(1000, merged.MaxOutputRows);
        Assert.Null(merged.ThrottleDelayMilliseconds);
    }
}
