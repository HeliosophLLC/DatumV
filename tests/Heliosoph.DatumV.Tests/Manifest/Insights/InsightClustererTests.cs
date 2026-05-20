namespace Heliosoph.DatumV.Tests.Manifest.Insights;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Tests for <see cref="InsightClusterer"/> — action routing, apply mode derivation,
/// conflict resolution, and syndrome detection.
/// </summary>
public sealed class InsightClustererTests : ServiceTestBase
{
    // ── ApplyMode derivation ──

    [Fact]
    public void ComputeApplyMode_HighConfidenceLossless_ReturnsAutoSafe()
    {
        RawFinding finding = MakeFinding(
            InsightKind.NormalizationNeeded,
            confidence: 0.96,
            actions: [MakeAction(ActionKind.Replace, lossy: false)]);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.AutoSafe, mode);
    }

    [Fact]
    public void ComputeApplyMode_HighConfidenceConstantDrop_ReturnsAutoSafe()
    {
        RawFinding finding = MakeFinding(
            InsightKind.ConstantFeature,
            confidence: 0.99,
            actions: [MakeAction(ActionKind.Drop, lossy: true)]);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.AutoSafe, mode);
    }

    [Fact]
    public void ComputeApplyMode_DropNonConstant_ReturnsManualOnly()
    {
        RawFinding finding = MakeFinding(
            InsightKind.PossibleIdentifier,
            confidence: 0.95,
            actions: [MakeAction(ActionKind.Drop, lossy: true)]);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.ManualOnly, mode);
    }

    [Fact]
    public void ComputeApplyMode_MediumConfidenceLossy_ReturnsSuggest()
    {
        RawFinding finding = MakeFinding(
            InsightKind.RightSkewed,
            confidence: 0.75,
            actions: [MakeAction(ActionKind.Replace, lossy: true)]);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.Suggest, mode);
    }

    [Fact]
    public void ComputeApplyMode_LowConfidenceLossy_ReturnsManualOnly()
    {
        RawFinding finding = MakeFinding(
            InsightKind.HeavyTailed,
            confidence: 0.5,
            actions: [MakeAction(ActionKind.Replace, lossy: true)]);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.ManualOnly, mode);
    }

    [Fact]
    public void ComputeApplyMode_NoActions_ReturnsSuggest()
    {
        RawFinding finding = MakeFinding(
            InsightKind.PossibleOrdinal,
            confidence: 0.90,
            actions: []);

        ApplyMode mode = InsightClusterer.ComputeApplyMode(finding);

        Assert.Equal(ApplyMode.Suggest, mode);
    }

    // ── Action routing ──

    [Fact]
    public void ToInsight_AutoSafe_ActionsPopulated_ProposedActionsNull()
    {
        RawFinding finding = MakeFinding(
            InsightKind.ConstantFeature,
            confidence: 0.99,
            actions: [MakeAction(ActionKind.Drop, lossy: true)]);

        DatasetInsight insight = InsightClusterer.ToInsight(finding);

        Assert.Equal(ApplyMode.AutoSafe, insight.RecommendedApplyMode);
        Assert.Single(insight.Actions);
        Assert.Null(insight.ProposedActions);
    }

    [Fact]
    public void ToInsight_ManualOnly_ActionsEmpty_ProposedActionsPopulated()
    {
        RawFinding finding = MakeFinding(
            InsightKind.PossibleIdentifier,
            confidence: 0.92,
            actions: [MakeAction(ActionKind.Drop, lossy: true)]);

        DatasetInsight insight = InsightClusterer.ToInsight(finding);

        Assert.Equal(ApplyMode.ManualOnly, insight.RecommendedApplyMode);
        Assert.Empty(insight.Actions);
        Assert.NotNull(insight.ProposedActions);
        Assert.Single(insight.ProposedActions);
    }

    // ── Conflict resolution ──

    [Fact]
    public void Cluster_ConflictGroup_HighestConfidenceWins()
    {
        RawFinding low = MakeFinding(
            InsightKind.NearDuplicateNumeric,
            confidence: 0.80,
            actions: [MakeAction(ActionKind.Drop, lossy: true, column: "colB")],
            conflictGroup: "dup-colA-colB",
            features: ["colA", "colB"]);

        RawFinding high = MakeFinding(
            InsightKind.NearDuplicateNumeric,
            confidence: 0.95,
            actions: [MakeAction(ActionKind.Drop, lossy: true, column: "colA")],
            conflictGroup: "dup-colA-colB",
            features: ["colA", "colB"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([low, high]);

        // Only the higher-confidence insight survives.
        Assert.Single(result);
        Assert.Equal(0.95, result[0].Confidence);
    }

    [Fact]
    public void Cluster_NoConflictGroup_AllSurvive()
    {
        RawFinding a = MakeFinding(InsightKind.HighMissingness, confidence: 0.8, features: ["colA"]);
        RawFinding b = MakeFinding(InsightKind.RightSkewed, confidence: 0.7, features: ["colB"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([a, b]);

        Assert.Equal(2, result.Count);
    }

    // ── Sorting ──

    [Fact]
    public void Cluster_SortedBySeverityThenConfidence()
    {
        RawFinding info = MakeFinding(InsightKind.PossibleOrdinal, confidence: 0.99, severity: InsightSeverity.Info, features: ["colA"]);
        RawFinding warning = MakeFinding(InsightKind.HighMissingness, confidence: 0.80, severity: InsightSeverity.Warning, features: ["colB"]);
        RawFinding critical = MakeFinding(InsightKind.CriticalMissingness, confidence: 0.70, severity: InsightSeverity.Critical, features: ["colC"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([info, warning, critical]);

        Assert.Equal(InsightSeverity.Critical, result[0].Severity);
        Assert.Equal(InsightSeverity.Warning, result[1].Severity);
        Assert.Equal(InsightSeverity.Info, result[2].Severity);
    }

    [Fact]
    public void Cluster_SameSeverity_HigherConfidenceFirst()
    {
        RawFinding low = MakeFinding(InsightKind.HighMissingness, confidence: 0.60, features: ["a"]);
        RawFinding high = MakeFinding(InsightKind.RightSkewed, confidence: 0.95, features: ["b"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([low, high]);

        Assert.Equal(0.95, result[0].Confidence);
        Assert.Equal(0.60, result[1].Confidence);
    }

    // ── Syndrome detection ──

    [Fact]
    public void Cluster_ZeroInflatedAndSkewed_MergedIntoSyndrome()
    {
        RawFinding zeroInflated = MakeFinding(
            InsightKind.ZeroInflated, confidence: 0.90, features: ["income"]);

        RawFinding skewed = MakeFinding(
            InsightKind.RightSkewed, confidence: 0.85, features: ["income"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([zeroInflated, skewed]);

        // Should merge into ZeroInflatedSkewedNumeric syndrome.
        Assert.Single(result);
        Assert.Equal(InsightKind.ZeroInflatedSkewedNumeric, result[0].Kind);
        Assert.Equal(0.90, result[0].Confidence); // max of components
    }

    [Fact]
    public void Cluster_SyndromeComponents_OnDifferentFeatures_NotMerged()
    {
        RawFinding zeroInflated = MakeFinding(
            InsightKind.ZeroInflated, confidence: 0.90, features: ["income"]);

        RawFinding skewed = MakeFinding(
            InsightKind.RightSkewed, confidence: 0.85, features: ["age"]);

        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([zeroInflated, skewed]);

        // Different features — no syndrome, both stay atomic.
        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, i => i.Kind == InsightKind.ZeroInflatedSkewedNumeric);
    }

    [Fact]
    public void Cluster_EmptyFindings_ReturnsEmpty()
    {
        IReadOnlyList<DatasetInsight> result = InsightClusterer.Cluster([]);
        Assert.Empty(result);
    }

    // ── Helpers ──

    private static RawFinding MakeFinding(
        InsightKind kind,
        double confidence = 0.90,
        IReadOnlyList<InsightAction>? actions = null,
        IReadOnlyList<string>? features = null,
        string? conflictGroup = null,
        InsightSeverity severity = InsightSeverity.Warning)
    {
        return new RawFinding(
            kind,
            InsightCategory.DataQuality,
            severity,
            confidence,
            InsightScope.Feature,
            "Test observation.",
            "Test risk.",
            "Test recommendation.",
            Rationale: null,
            Alternatives: null,
            features ?? ["testCol"],
            actions ?? [],
            conflictGroup,
            Evidence: null);
    }

    private static InsightAction MakeAction(
        ActionKind kind,
        bool lossy = false,
        string? column = null,
        string? bundleIdentifier = null)
    {
        return new InsightAction(
            kind,
            column ?? "testCol",
            kind == ActionKind.Drop ? null : "EXPR",
            Alias: null,
            lossy,
            Reversible: !lossy,
            bundleIdentifier);
    }
}
