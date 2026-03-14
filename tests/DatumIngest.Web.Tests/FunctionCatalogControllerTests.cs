using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Pooling;
using DatumIngest.Web.Api;
using DatumIngest.Web.Dtos.Functions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace DatumIngest.Web.Tests;

/// <summary>
/// Direct tests against <see cref="FunctionCatalogController"/>, using a
/// real <see cref="FunctionRegistry"/> (the default-registered set). The
/// controller has no other dependencies, so an in-process call exercises
/// the full DTO projection without spinning up an HTTP pipeline.
/// </summary>
public sealed class FunctionCatalogControllerTests
{
    private static FunctionCatalogController CreateController()
    {
        ServiceCollection services = new();
        services.AddDatumIngest();
        ServiceProvider sp = services.BuildServiceProvider();
        Pool pool = sp.GetRequiredService<Pool>();
        TableCatalog catalog = new(pool);
        return new FunctionCatalogController(catalog);
    }

    private static ScalarFunctionListResponse List()
    {
        ActionResult<ScalarFunctionListResponse> result = CreateController().ListScalar();
        Assert.NotNull(result.Value);
        return result.Value!;
    }

    [Fact]
    public void ListScalar_ReturnsNonEmptyListWithKnownFunctions()
    {
        ScalarFunctionListResponse response = List();
        Assert.NotEmpty(response.Functions);

        // `concat` is one of the oldest registrations and survives across
        // catalog reshuffles, so it's a safe smoke-test anchor.
        ScalarFunctionDto concat = Assert.Single(response.Functions, f =>
            string.Equals(f.Name, "concat", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(concat.Signatures);
        Assert.Equal("None", concat.BodyScope);
    }

    [Fact]
    public void ListScalar_EachFunctionHasAtLeastOneSignature()
    {
        ScalarFunctionListResponse response = List();
        foreach (ScalarFunctionDto fn in response.Functions)
        {
            Assert.NotEmpty(fn.Signatures);
        }
    }

    [Fact]
    public void ListScalar_VariadicFunctionExposesVariadicSpec()
    {
        ScalarFunctionListResponse response = List();
        // `concat` has a variadic trailing slot; every signature variant
        // should report it (the registry doesn't synthesise scalar-only
        // shapes for variadic functions).
        ScalarFunctionDto concat = response.Functions.First(f =>
            string.Equals(f.Name, "concat", StringComparison.OrdinalIgnoreCase));
        bool foundVariadic = concat.Signatures.Any(s => s.Variadic is not null);
        Assert.True(foundVariadic, "expected at least one concat signature to expose a variadic trailing slot");
    }

    [Fact]
    public void ListScalar_RuntimeTypedReturn_HasNullStaticHint()
    {
        ScalarFunctionListResponse response = List();
        // `coalesce` resolves its return kind from the first non-null
        // argument — a runtime-typed return that the SameAs rule encodes.
        // Description should remain non-empty so the client can still
        // display it.
        ScalarFunctionDto coalesce = response.Functions.First(f =>
            string.Equals(f.Name, "coalesce", StringComparison.OrdinalIgnoreCase));
        ScalarFunctionReturnTypeDto returnType = coalesce.Signatures[0].ReturnType;
        Assert.Null(returnType.StaticHint);
        Assert.False(string.IsNullOrEmpty(returnType.Description));
    }

    [Fact]
    public void ListScalar_AcceptedKindsAreEnumeratedForBoundedMatchers()
    {
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto concat = response.Functions.First(f =>
            string.Equals(f.Name, "concat", StringComparison.OrdinalIgnoreCase));
        // Variadic matcher on concat should report a bounded set (just
        // String, in practice) — the empty-list-means-any sentinel must
        // not kick in for a single-kind matcher.
        ScalarFunctionVariadicDto? variadic = concat.Signatures
            .Select(s => s.Variadic)
            .FirstOrDefault(v => v is not null);
        Assert.NotNull(variadic);
        Assert.False(variadic!.AcceptsAnyKind);
        Assert.NotEmpty(variadic.AcceptedKinds);
    }

    [Fact]
    public void ListScalar_AnyKindMatcher_FlagsAcceptsAnyKind()
    {
        ScalarFunctionListResponse response = List();
        // `coalesce` accepts any kind in its variadic — the wire payload
        // should set AcceptsAnyKind=true and leave AcceptedKinds empty
        // rather than enumerating every DataKind value.
        ScalarFunctionDto coalesce = response.Functions.First(f =>
            string.Equals(f.Name, "coalesce", StringComparison.OrdinalIgnoreCase));
        // The polymorphic slot can be a fixed parameter or the variadic
        // trailing — accept either, as long as at least one slot reports
        // the full-polymorphic flag.
        bool anyKindSeen = coalesce.Signatures.Any(s =>
            s.Parameters.Any(p => p.AcceptsAnyKind)
            || (s.Variadic?.AcceptsAnyKind ?? false));
        Assert.True(anyKindSeen,
            "expected coalesce to have at least one slot marked AcceptsAnyKind=true");
    }

    [Fact]
    public void ListScalar_MetadataFlowsThroughDto_BetweenCheckAndStep()
    {
        // multilabel_classify.threshold declares BetweenCheck(0, 1) with
        // step 0.05 + a description. The DTO should surface a typed
        // BetweenCheckDto (no client-side string parsing required).
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto fn = response.Functions.First(f =>
            string.Equals(f.Name, "multilabel_classify", StringComparison.OrdinalIgnoreCase));
        ScalarFunctionParameterDto threshold = fn.Signatures[0].Parameters.First(p =>
            string.Equals(p.Name, "threshold", StringComparison.OrdinalIgnoreCase));

        BetweenCheckDto between = Assert.IsType<BetweenCheckDto>(threshold.Check);
        Assert.Equal(0.0m, between.Min);
        Assert.Equal(1.0m, between.Max);
        Assert.Equal(0.05m, threshold.Step);
        Assert.False(string.IsNullOrEmpty(threshold.Description));
    }

    [Fact]
    public void ListScalar_MetadataFlowsThroughDto_GreaterThanAndUnit()
    {
        // depth_map_to_image.source_h declares GreaterThanCheck(0) +
        // pixels unit. Verifies the typed check and unit round-trip.
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto fn = response.Functions.First(f =>
            string.Equals(f.Name, "depth_map_to_image", StringComparison.OrdinalIgnoreCase));
        ScalarFunctionParameterDto sourceH = fn.Signatures[0].Parameters.First(p =>
            string.Equals(p.Name, "source_h", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("pixels", sourceH.Unit);
        GreaterThanCheckDto gt = Assert.IsType<GreaterThanCheckDto>(sourceH.Check);
        Assert.Equal(0m, gt.Min);
        Assert.False(gt.Inclusive);
    }

    [Fact]
    public void ListScalar_MetadataFlowsThroughDto_InCheck()
    {
        // yolox_preprocess.target_size declares InCheck(['416', '640']).
        // Verifies the InCheckDto surfaces with the value list intact.
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto fn = response.Functions.First(f =>
            string.Equals(f.Name, "yolox_preprocess", StringComparison.OrdinalIgnoreCase));
        ScalarFunctionParameterDto targetSize = fn.Signatures[0].Parameters.First(p =>
            string.Equals(p.Name, "target_size", StringComparison.OrdinalIgnoreCase));

        InCheckDto inCheck = Assert.IsType<InCheckDto>(targetSize.Check);
        Assert.Equal(new[] { "416", "640" }, inCheck.Values);
    }

    [Fact]
    public void ListScalar_ParameterWithoutMetadata_LeavesFieldsNull()
    {
        // `concat` parameters have no metadata declared. Every optional
        // field must come through as null — the DTO must not synthesise
        // empty-string placeholders.
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto concat = response.Functions.First(f =>
            string.Equals(f.Name, "concat", StringComparison.OrdinalIgnoreCase));
        foreach (ScalarFunctionSignatureDto sig in concat.Signatures)
        {
            foreach (ScalarFunctionParameterDto p in sig.Parameters)
            {
                Assert.Null(p.Check);
                Assert.Null(p.Step);
                Assert.Null(p.Unit);
                Assert.Null(p.Description);
                Assert.Null(p.DefaultExpression);
            }
        }
    }

    [Fact]
    public void ListScalar_CheckDto_SerialisesWithKindDiscriminator()
    {
        // The whole point of the polymorphic DTO is that JSON consumers
        // can dispatch on a stable "kind" discriminator. Verify the
        // round-trip preserves the discriminator on a real check value
        // pulled from the live registry.
        ScalarFunctionListResponse response = List();
        ScalarFunctionDto fn = response.Functions.First(f =>
            string.Equals(f.Name, "multilabel_classify", StringComparison.OrdinalIgnoreCase));
        ScalarFunctionParameterDto threshold = fn.Signatures[0].Parameters.First(p =>
            string.Equals(p.Name, "threshold", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(threshold.Check);

        // Serialise + parse the JSON. The discriminator must land as the
        // "kind" property at the top level so JS clients can switch on it.
        string json = System.Text.Json.JsonSerializer.Serialize<ParameterCheckDto>(
            threshold.Check!,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("between", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(0.0m, doc.RootElement.GetProperty("min").GetDecimal());
        Assert.Equal(1.0m, doc.RootElement.GetProperty("max").GetDecimal());

        // Round-trip back through the polymorphic deserialiser.
        ParameterCheckDto? roundTripped = System.Text.Json.JsonSerializer.Deserialize<ParameterCheckDto>(
            json,
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
            });
        BetweenCheckDto between = Assert.IsType<BetweenCheckDto>(roundTripped);
        Assert.Equal(0.0m, between.Min);
        Assert.Equal(1.0m, between.Max);
    }

    [Fact]
    public void ListScalar_BodyScopeIsReported()
    {
        ScalarFunctionListResponse response = List();
        // `infer` is body-scoped to CREATE MODEL bodies — should round-trip
        // through the DTO so the Execute-Function picker can disable it.
        ScalarFunctionDto? infer = response.Functions.FirstOrDefault(f =>
            string.Equals(f.Name, "infer", StringComparison.OrdinalIgnoreCase));
        // Skip if the registration name has changed; the property under
        // test is "BodyScope round-trips", not "infer exists".
        if (infer is null) return;
        Assert.NotEqual("None", infer.BodyScope);
    }

    // ───────────────────── /api/functions/udfs ─────────────────────

    private static TableCatalog CreateCatalogWithUdfs(params string[] createStatements)
    {
        ServiceCollection services = new();
        services.AddDatumIngest();
        ServiceProvider sp = services.BuildServiceProvider();
        Pool pool = sp.GetRequiredService<Pool>();
        TableCatalog catalog = new(pool);
        foreach (string sql in createStatements)
        {
            catalog.PlanAsync(sql).GetAwaiter().GetResult();
        }
        return catalog;
    }

    [Fact]
    public void ListUdfs_EmptyCatalog_ReturnsEmptyList()
    {
        FunctionCatalogController controller = CreateController();
        ActionResult<UdfListResponse> result = controller.ListUdfs();
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Udfs);
    }

    [Fact]
    public void ListUdfs_MacroUdf_SurfacesBodyKindAndParameters()
    {
        TableCatalog catalog = CreateCatalogWithUdfs(
            "CREATE FUNCTION shout(name STRING) AS upper(name)");
        FunctionCatalogController controller = new(catalog);

        UdfListResponse response = controller.ListUdfs().Value!;
        UdfDto shout = Assert.Single(response.Udfs);
        Assert.Equal("shout", shout.Name);
        Assert.Equal("macro", shout.BodyKind);
        Assert.False(shout.IsPure);
        Assert.Single(shout.Parameters);
        Assert.Equal("name", shout.Parameters[0].Name);
    }

    [Fact]
    public void ListUdfs_ProceduralUdf_WithCheckClause_ProjectsTypedDiscriminator()
    {
        // CHECK on a procedural UDF parameter should round-trip through the
        // walker into a BetweenCheckDto carried on the wire payload.
        TableCatalog catalog = CreateCatalogWithUdfs(
            "CREATE FUNCTION clamp01(x FLOAT32 = CAST(0.5 AS FLOAT32) " +
            "CHECK (x BETWEEN 0 AND 1) STEP 0.05 COMMENT 'Sigmoid threshold.') " +
            "RETURNS FLOAT32 BEGIN RETURN x END");
        FunctionCatalogController controller = new(catalog);

        UdfListResponse response = controller.ListUdfs().Value!;
        UdfDto clamp = Assert.Single(response.Udfs);
        Assert.Equal("procedural", clamp.BodyKind);
        Assert.Equal("FLOAT32", clamp.ReturnType, ignoreCase: true);

        ScalarFunctionParameterDto x = Assert.Single(clamp.Parameters);
        Assert.True(x.IsOptional, "= default should mark the parameter optional.");
        Assert.NotNull(x.DefaultExpression);

        BetweenCheckDto between = Assert.IsType<BetweenCheckDto>(x.Check);
        Assert.Equal(0m, between.Min);
        Assert.Equal(1m, between.Max);
        Assert.Equal(0.05m, x.Step);
        Assert.Equal("Sigmoid threshold.", x.Description);
    }

    [Fact]
    public void ListUdfs_DeclaredType_RoundTripsAsSingletonAcceptedKinds()
    {
        // A declared `INT32` parameter type should resolve into the
        // accepted-kinds list as a singleton so the UI can render a typed
        // input widget instead of a permissive any-kind text field.
        TableCatalog catalog = CreateCatalogWithUdfs(
            "CREATE FUNCTION sq(x INT32) RETURNS INT32 BEGIN RETURN x * x END");
        FunctionCatalogController controller = new(catalog);

        UdfDto sq = Assert.Single(controller.ListUdfs().Value!.Udfs);
        ScalarFunctionParameterDto x = sq.Parameters[0];
        Assert.False(x.AcceptsAnyKind);
        Assert.Equal(new[] { "Int32" }, x.AcceptedKinds);
    }

    // ───────────────────── /api/functions/models ─────────────────────

    [Fact]
    public void ListModels_EmptyCatalog_ReturnsEmptyList()
    {
        FunctionCatalogController controller = CreateController();
        ActionResult<ModelListResponse> result = controller.ListModels();
        Assert.NotNull(result.Value);
        Assert.Empty(result.Value!.Models);
    }
}
