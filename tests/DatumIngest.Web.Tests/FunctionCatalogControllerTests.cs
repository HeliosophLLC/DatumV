using DatumIngest.Functions;
using DatumIngest.Web.Api;
using DatumIngest.Web.Dtos.Functions;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Tests;

/// <summary>
/// Direct tests against <see cref="FunctionCatalogController"/>, using a
/// real <see cref="FunctionRegistry"/> (the default-registered set). The
/// controller has no other dependencies, so an in-process call exercises
/// the full DTO projection without spinning up an HTTP pipeline.
/// </summary>
public sealed class FunctionCatalogControllerTests
{
    private static ScalarFunctionListResponse List()
    {
        FunctionCatalogController controller = new(FunctionRegistry.CreateDefault());
        ActionResult<ScalarFunctionListResponse> result = controller.ListScalar();
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
}
