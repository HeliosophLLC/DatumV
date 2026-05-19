namespace Heliosoph.DatumV.Tests.Manifest.Insights;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// Tests for <see cref="QuerySynthesizer"/> — query generation, bundle atomicity,
/// conflict resolution, and annotation generation.
/// </summary>
public sealed class QuerySynthesizerTests : ServiceTestBase
{
    private static readonly QuerySynthesisOptions DefaultOptions = new();
    private static readonly IReadOnlyList<string> ThreeColumns = ["age", "income", "status"];

    // ── Recommended vs. Full ──

    [Fact]
    public void SynthesizeRecommended_OnlyIncludesAutoSafeAndSuggestActions()
    {
        DatasetInsight autoSafe = MakeInsight(
            InsightKind.ConstantFeature,
            ApplyMode.AutoSafe,
            actions: [new InsightAction(ActionKind.Drop, "status", null, null, true, true, null)]);

        DatasetInsight manualOnly = MakeInsight(
            InsightKind.PossibleIdentifier,
            ApplyMode.ManualOnly,
            proposedActions: [new InsightAction(ActionKind.Drop, "age", null, null, true, false, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [autoSafe, manualOnly], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        // "status" should be dropped (AutoSafe).
        Assert.DoesNotContain("status", query);
        // "age" should be present (ManualOnly not included in recommended).
        Assert.Contains("age", query);
    }

    [Fact]
    public void SynthesizeFull_IncludesProposedActions()
    {
        DatasetInsight manualOnly = MakeInsight(
            InsightKind.PossibleIdentifier,
            ApplyMode.ManualOnly,
            proposedActions: [new InsightAction(ActionKind.Drop, "age", null, null, true, false, null)]);

        string? query = QuerySynthesizer.SynthesizeFull(
            [manualOnly], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        // "age" should be dropped in the full query.
        Assert.DoesNotContain("age", query);
        Assert.Contains("income", query);
    }

    [Fact]
    public void SynthesizeRecommended_NoActions_ReturnsNull()
    {
        DatasetInsight informational = MakeInsight(
            InsightKind.PossibleOrdinal,
            ApplyMode.Suggest,
            actions: []);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [informational], ThreeColumns, DefaultOptions);

        Assert.Null(query);
    }

    // ── Replace action ──

    [Fact]
    public void Synthesize_ReplaceAction_TransformsColumn()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.RightSkewed,
            ApplyMode.Suggest,
            actions: [new InsightAction(ActionKind.Replace, "income", "LOG(income)", null, true, true, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        Assert.Contains("LOG(income) AS income", query);
        Assert.Contains("age", query);
    }

    // ── Append action ──

    [Fact]
    public void Synthesize_AppendAction_AddsNewColumn()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.ZeroInflated,
            ApplyMode.Suggest,
            actions: [new InsightAction(ActionKind.Append, null, "CASE WHEN income != 0 THEN 1 ELSE 0 END", "income_nonzero", false, true, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        Assert.Contains("income_nonzero", query);
        // Original columns all still present.
        Assert.Contains("age", query);
        Assert.Contains("income", query);
    }

    // ── Filter action ──

    [Fact]
    public void Synthesize_FilterAction_AddsWhereClause()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.ExtremeOutliers,
            ApplyMode.Suggest,
            actions: [new InsightAction(ActionKind.Filter, null, "income BETWEEN 0 AND 500000", null, true, false, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        Assert.Contains("WHERE", query);
        Assert.Contains("income BETWEEN 0 AND 500000", query);
    }

    // ── Bundle atomicity ──

    [Fact]
    public void SynthesizeRecommended_IncompleteBundleExcluded()
    {
        // Bundle has 2 actions: one in actions (AutoSafe), one in proposedActions (ManualOnly).
        // SynthesizeRecommended can't see the proposed one → bundle incomplete → both excluded.
        DatasetInsight insight = MakeInsight(
            InsightKind.ZeroInflated,
            ApplyMode.ManualOnly,
            proposedActions:
            [
                new InsightAction(ActionKind.Append, null, "CASE WHEN x != 0 THEN 1 ELSE 0 END", "x_nonzero", false, true, "bundle_x"),
                new InsightAction(ActionKind.Replace, "x", "LOG(NULLIF(x, 0))", null, true, true, "bundle_x")
            ]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ["x", "y"], DefaultOptions);

        // ManualOnly proposed actions not included in recommended → null.
        Assert.Null(query);
    }

    [Fact]
    public void SynthesizeFull_CompleteBundleIncluded()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.ZeroInflated,
            ApplyMode.ManualOnly,
            proposedActions:
            [
                new InsightAction(ActionKind.Append, null, "CASE WHEN x != 0 THEN 1 ELSE 0 END", "x_nonzero", false, true, "bundle_x"),
                new InsightAction(ActionKind.Replace, "x", "LOG(NULLIF(x, 0))", null, true, true, "bundle_x")
            ]);

        string? query = QuerySynthesizer.SynthesizeFull(
            [insight], ["x", "y"], DefaultOptions);

        Assert.NotNull(query);
        // Both bundle members present.
        Assert.Contains("x_nonzero", query);
        Assert.Contains("LOG(NULLIF(x, 0)) AS x", query);
    }

    // ── Source expression ──

    [Fact]
    public void Synthesize_CustomSourceExpression_UsedInFromClause()
    {
        QuerySynthesisOptions options = new() { SourceExpression = "my_table" };

        DatasetInsight insight = MakeInsight(
            InsightKind.ConstantFeature,
            ApplyMode.AutoSafe,
            actions: [new InsightAction(ActionKind.Drop, "status", null, null, true, true, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ThreeColumns, options);

        Assert.NotNull(query);
        Assert.Contains("FROM my_table", query);
    }

    // ── Annotations ──

    [Fact]
    public void GenerateAnnotations_MapsActionsToInsights()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.RightSkewed,
            ApplyMode.Suggest,
            actions: [new InsightAction(ActionKind.Replace, "income", "LOG(income)", null, true, true, null)],
            confidence: 0.88);

        IReadOnlyList<QueryAnnotation> annotations = QuerySynthesizer.GenerateAnnotations([insight]);

        Assert.Single(annotations);
        Assert.Equal("income", annotations[0].Column);
        Assert.Equal(InsightKind.RightSkewed, annotations[0].InsightKind);
        Assert.Equal(0.88, annotations[0].Confidence);
    }

    [Fact]
    public void GenerateAnnotations_IncludesProposedActions()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.PossibleIdentifier,
            ApplyMode.ManualOnly,
            proposedActions: [new InsightAction(ActionKind.Drop, "id", null, null, true, false, null)]);

        IReadOnlyList<QueryAnnotation> annotations = QuerySynthesizer.GenerateAnnotations([insight]);

        Assert.Single(annotations);
        Assert.Equal("id", annotations[0].Column);
    }

    [Fact]
    public void GenerateAnnotations_EmptyInsights_ReturnsEmpty()
    {
        IReadOnlyList<QueryAnnotation> annotations = QuerySynthesizer.GenerateAnnotations([]);
        Assert.Empty(annotations);
    }

    // ── Clean SQL ──

    [Fact]
    public void Synthesize_ProducesCleanSql_NoComments()
    {
        DatasetInsight insight = MakeInsight(
            InsightKind.ConstantFeature,
            ApplyMode.AutoSafe,
            actions: [new InsightAction(ActionKind.Drop, "status", null, null, true, true, null)]);

        string? query = QuerySynthesizer.SynthesizeRecommended(
            [insight], ThreeColumns, DefaultOptions);

        Assert.NotNull(query);
        Assert.DoesNotContain("--", query);
        Assert.DoesNotContain("/*", query);
        Assert.StartsWith("SELECT", query);
    }

    // ── Helpers ──

    private static DatasetInsight MakeInsight(
        InsightKind kind,
        ApplyMode mode,
        IReadOnlyList<InsightAction>? actions = null,
        IReadOnlyList<InsightAction>? proposedActions = null,
        double confidence = 0.90)
    {
        return new DatasetInsight
        {
            Kind = kind,
            Category = InsightCategory.DataQuality,
            Severity = InsightSeverity.Warning,
            Confidence = confidence,
            Scope = InsightScope.Feature,
            Observation = "Test observation.",
            Risk = "Test risk.",
            Recommendation = "Test recommendation.",
            AffectedFeatures = ["testCol"],
            Actions = actions ?? [],
            ProposedActions = proposedActions,
            RecommendedApplyMode = mode
        };
    }
}
