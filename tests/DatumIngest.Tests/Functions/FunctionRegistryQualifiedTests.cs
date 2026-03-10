using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Manifest;

namespace DatumIngest.Tests.Functions;

/// <summary>
/// S7a pins for <see cref="FunctionRegistry"/>'s qualified-name surface:
/// built-ins live in <c>system</c>, exact-qualified and search-path-aware
/// lookups round-trip, and the bare-string back-compat layer interprets
/// dotted names as <c>schema.fn</c> while bare names walk the default
/// <c>[public, system]</c> path. Existing function-call sites rely on
/// these semantics; this file keeps them from drifting in S7b–S7f.
/// </summary>
public sealed class FunctionRegistryQualifiedTests
{
    [Fact]
    public void BuiltIns_LiveInSystemSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetScalar(new QualifiedName("system", "len")));
        Assert.NotNull(registry.TryGetScalar(new QualifiedName("system", "concat")));
        // Aliases inherit the primary's schema.
        Assert.NotNull(registry.TryGetScalar(new QualifiedName("system", "gen_random_uuid")));
        Assert.NotNull(registry.TryGetScalar(new QualifiedName("system", "ceiling")));
    }

    [Fact]
    public void BuiltIns_NotInPublicSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.Null(registry.TryGetScalar(new QualifiedName("public", "len")));
    }

    [Fact]
    public void Aggregates_LiveInSystemSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetAggregate(new QualifiedName("system", "count")));
        Assert.NotNull(registry.TryGetAggregate(new QualifiedName("system", "sum")));
    }

    [Fact]
    public void Window_LiveInSystemSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetWindow(new QualifiedName("system", "row_number")));
    }

    [Fact]
    public void TableValued_LiveInSystemSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.NotNull(registry.TryGetTableValued(new QualifiedName("system", "range")));
    }

    // ──────────────────── Resolution-aware lookup ────────────────────

    [Fact]
    public void Lookup_WithExplicitSchema_GoesStraightToThatSchema()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        // search_path is irrelevant when an explicit schema is given.
        IReadOnlyList<string> wrongPath = new[] { "public" };

        Assert.NotNull(registry.TryGetScalar(explicitSchema: "system", "len", wrongPath));
        Assert.Null(registry.TryGetScalar(explicitSchema: "public", "len", wrongPath));
    }

    [Fact]
    public void Lookup_UnqualifiedWithDefaultPath_FindsBuiltInInSystem()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();
        IReadOnlyList<string> defaultPath = new[] { "public", "system" };

        Assert.NotNull(registry.TryGetScalar(explicitSchema: null, "len", defaultPath));
    }

    [Fact]
    public void Lookup_UnqualifiedWithEmptyPath_FailsToFindBuiltIn()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        Assert.Null(registry.TryGetScalar(explicitSchema: null, "len", Array.Empty<string>()));
    }

    [Fact]
    public void Lookup_UnqualifiedFirstHitWins()
    {
        FunctionRegistry registry = new();
        // Two registrations of the same name under different schemas;
        // the search path decides which one resolves.
        registry.RegisterScalar<DatumIngest.Functions.Scalar.Strings.LenFunction>(schema: "system");
        registry.RegisterScalar<DatumIngest.Functions.Scalar.Strings.LenFunction>(schema: "public");

        IScalarFunction? systemFirst = registry.TryGetScalar(
            explicitSchema: null, "len", new[] { "system", "public" });
        IScalarFunction? publicFirst = registry.TryGetScalar(
            explicitSchema: null, "len", new[] { "public", "system" });

        Assert.NotNull(systemFirst);
        Assert.NotNull(publicFirst);
        Assert.Same(
            registry.TryGetScalar(new QualifiedName("system", "len")),
            systemFirst);
        Assert.Same(
            registry.TryGetScalar(new QualifiedName("public", "len")),
            publicFirst);
    }

    // ──────────────────── Back-compat bare-string ────────────────────

    [Fact]
    public void BareString_NoDot_WalksDefaultPathAndFindsBuiltIn()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        // Pre-S7 call shape — every existing call site uses this.
        Assert.NotNull(registry.TryGetScalar("len"));
        Assert.NotNull(registry.TryGetAggregate("count"));
        Assert.NotNull(registry.TryGetWindow("row_number"));
        Assert.NotNull(registry.TryGetTableValued("range"));
    }

    [Fact]
    public void BareString_Dotted_SplitsAndExactMatches()
    {
        // RoutineRegistrar's procedural-UDF adapter passes
        // "udf.X" — the back-compat layer parses that as
        // (udf, X) and exact-matches. (Real schema membership for
        // UDFs lands in S7d.)
        FunctionRegistry registry = new();
        registry.RegisterScalarInstance(
            "udf.my_udf",
            new DatumIngest.Functions.Scalar.Strings.LenFunction());

        Assert.NotNull(registry.TryGetScalar("udf.my_udf"));
        Assert.NotNull(registry.TryGetScalar(new QualifiedName("udf", "my_udf")));
        Assert.Null(registry.TryGetScalar(new QualifiedName("system", "my_udf")));
    }

    [Fact]
    public void Descriptor_FollowsSchema_AndAliasShowsUpUnderPrimary()
    {
        FunctionRegistry registry = FunctionRegistry.CreateDefault();

        FunctionDescriptor? d = registry.TryGetScalarDescriptor(new QualifiedName("system", "uuidv4"));
        Assert.NotNull(d);
        Assert.Equal("uuidv4", d.PrimaryName);
        Assert.Contains("gen_random_uuid", d.Aliases);

        // The alias key also maps back to the primary descriptor.
        FunctionDescriptor? alias = registry.TryGetScalarDescriptor(new QualifiedName("system", "gen_random_uuid"));
        Assert.Same(d, alias);
    }

    [Fact]
    public void UnregisterScalar_RemovesAcrossDescriptor()
    {
        FunctionRegistry registry = new();
        registry.RegisterScalarInstance(
            "udf.tmp",
            new DatumIngest.Functions.Scalar.Strings.LenFunction(),
            descriptor: new FunctionDescriptor(
                "tmp",
                Array.Empty<string>(),
                FunctionCategory.Utility,
                "",
                Array.Empty<FunctionSignatureVariant>()));

        Assert.NotNull(registry.TryGetScalar("udf.tmp"));
        Assert.True(registry.UnregisterScalar("udf.tmp"));
        Assert.Null(registry.TryGetScalar("udf.tmp"));
    }
}
