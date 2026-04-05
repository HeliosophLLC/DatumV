using DatumIngest.Models.Calibration;

namespace DatumIngest.Tests.Models.Calibration;

/// <summary>
/// Unit tests for <see cref="CalibrationCoordinator.ProjectNextRampStep"/>
/// — the pure projection algorithm that decides whether the calibration
/// ramp should halt before its next doubling. Tests cover the regimes
/// that real models exhibit: linear (most CNNs / depth models),
/// sub-linear (small models with fixed overhead dominating), and
/// super-linear (attention-heavy transformers, diffusion U-Nets at
/// high resolution).
/// </summary>
public sealed class CalibrationRampProjectionTests
{
    private const long GB = 1024L * 1024 * 1024;
    private const long MB = 1024L * 1024;

    [Fact]
    public void FirstStep_NoPriorMeasurement_UsesTwoXFloor()
    {
        // lastMarginal == 0 means we're at the first ramp step. Projection
        // multiplier should default to 2× since we have no observed ratio.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 1 * GB,
            lastTotal: 0,
            weightCost: 0,
            currentUsed: 4 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(2.0, p.GrowthMultiplier);
        Assert.Equal(2 * GB, p.ProjectedNext);
        Assert.False(p.ShouldHalt);
    }

    [Fact]
    public void LinearGrowth_ProjectsTwoX()
    {
        // Model that doubles VRAM with each batch doubling (ratio ≈ 2).
        // CNN-style models without sequence dependency typically behave
        // this way.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 2 * GB,    // batch=N
            lastTotal: 1 * GB, // batch=N/2
            weightCost: 0,
            currentUsed: 5 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(2.0, p.GrowthMultiplier);
        Assert.Equal(4 * GB, p.ProjectedNext);
        Assert.False(p.ShouldHalt);
    }

    [Fact]
    public void SubLinearGrowth_StillProjectsTwoX_NoOptimisticShortcut()
    {
        // Model whose total grew LESS than 2× — could mean it's near a
        // ceiling and the allocator is overlapping buffers, OR it's just
        // a small model with fixed overhead amortising. Either way we
        // don't optimistically project a sub-linear next step; floor at
        // 2× keeps us honest.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 1100 * MB,
            lastTotal: 1000 * MB,  // ratio 1.1 (sub-linear)
            weightCost: 0,
            currentUsed: 4 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(2.0, p.GrowthMultiplier);
        Assert.Equal(2 * 1100L * MB, p.ProjectedNext);
    }

    [Fact]
    public void QuadraticGrowth_ProjectsObservedRatio()
    {
        // Attention-heavy transformer: total[N] / total[N/2] ≈ 4
        // (sequence-length attention is O(N²) — doubling batch
        // quadruples activation memory). With weight_cost=0 the totals
        // ARE activations and the projection ratio is direct.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 4 * GB,
            lastTotal: 1 * GB,  // ratio 4.0 (quadratic)
            weightCost: 0,
            currentUsed: 5 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(4.0, p.GrowthMultiplier);
        Assert.Equal(16 * GB, p.ProjectedNext);
        // Available = 24 - 5 - 1.2 (5%) = 17.8 GB. 16 GB still fits.
        Assert.False(p.ShouldHalt);
    }

    [Fact]
    public void QuadraticGrowth_OverBudget_HaltsBeforeSpill()
    {
        // Same quadratic model, but now we're already deeper into the
        // ramp — batch=N consumed 8 GB, projection of 32 GB (4× = 8×4)
        // doesn't fit a 24 GB card. This is the case where the
        // pre-look-ahead behaviour would have attempted batch=2N and
        // spilled into shared memory.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 8 * GB,
            lastTotal: 2 * GB,  // ratio 4.0
            weightCost: 0,
            currentUsed: 10 * GB,  // weights + prior dispatch's residue
            currentTotal: 24 * GB);

        Assert.Equal(4.0, p.GrowthMultiplier);
        Assert.Equal(32 * GB, p.ProjectedNext);
        // Available = 24 - 10 - 1.2 = 12.8 GB. 32 GB doesn't fit.
        Assert.True(p.ShouldHalt);
    }

    [Fact]
    public void CubicGrowth_ProjectsObservedRatio()
    {
        // Some attention configurations with KV-cache + sequence-length
        // interactions can scale cubically per batch doubling.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 8 * GB,
            lastTotal: 1 * GB,  // ratio 8.0 (cubic)
            weightCost: 0,
            currentUsed: 4 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(8.0, p.GrowthMultiplier);
        Assert.Equal(64 * GB, p.ProjectedNext);
        Assert.True(p.ShouldHalt);
    }

    [Fact]
    public void LinearGrowth_NearCapacity_HaltsBeforeOverrun()
    {
        // Linear model with prior dispatches having filled most of the
        // device. Next 2× doubling overruns despite the model being
        // linear — guard works regardless of growth class.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 6 * GB,
            lastTotal: 3 * GB,  // ratio 2.0 (linear)
            weightCost: 0,
            currentUsed: 13 * GB,
            currentTotal: 24 * GB);

        Assert.Equal(2.0, p.GrowthMultiplier);
        Assert.Equal(12 * GB, p.ProjectedNext);
        // Available = 24 - 13 - 1.2 = 9.8 GB. 12 GB doesn't fit.
        Assert.True(p.ShouldHalt);
    }

    [Fact]
    public void SafetyMargin_IsFivePercentOfTotal()
    {
        // Probe the safety margin directly: with marginal=0 and no
        // current usage, ProjectedAvailable should be total × 0.95.
        var p = CalibrationCoordinator.ProjectNextRampStep(
            total: 1,
            lastTotal: 0,
            weightCost: 0,
            currentUsed: 0,
            currentTotal: 24 * GB);

        // 24 GB × 0.95 = 22.8 GB
        long expectedAvail = 24 * GB - (24 * GB / 20);
        Assert.Equal(expectedAvail, p.ProjectedAvailable);
    }
}
