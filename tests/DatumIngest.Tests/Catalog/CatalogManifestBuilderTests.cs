using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Functions.Scalar.Arrays;
using DatumIngest.Functions.Scalar.Drawing;
using DatumIngest.Functions.Scalar.Strings;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="CatalogManifestBuilder"/> — verifies that scalar
/// function signatures and model catalog entries flow into the language
/// server manifest with their argument shapes intact.
/// </summary>
public sealed class CatalogManifestBuilderTests : ServiceTestBase
{
    [Fact]
    public void Build_ScalarFunction_PopulatesParametersFromDescriptor()
    {
        // CastFunction's first signature variant is
        // (value: Any, target: OneOf(Type, String)) → String. Verify the
        // builder pulls the parameter spec into the manifest's
        // ParameterSignature shape.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<CastFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature cast = Assert.Single(
            manifest.Functions, f => f.Name.Equals("cast", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(2, cast.Parameters.Count);
        Assert.Equal("value", cast.Parameters[0].Name);
        Assert.Equal("Any", cast.Parameters[0].Kind);
        Assert.Equal("target", cast.Parameters[1].Name);
        // OneOf matchers describe themselves as "one of Type, String".
        Assert.Contains("Type", cast.Parameters[1].Kind);
        Assert.Contains("String", cast.Parameters[1].Kind);
        // ConstantRule(String) → return type "String".
        Assert.Equal("String", cast.ReturnType);
        Assert.Equal(FunctionCategory.Conversion, cast.Category);
    }

    [Fact]
    public void Build_ScalarFunction_VariadicLowersToEllipsisParameter()
    {
        // ConcatFunction declares VariadicTrailing("values", String, MinOccurrences=2).
        // The builder should render it as a single parameter named "...values"
        // (the ellipsis cues the editor that more args may follow). IsOptional
        // tracks the strict zero-allowed case — concat requires ≥2 so it
        // stays false here.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<ConcatFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature concat = Assert.Single(
            manifest.Functions, f => f.Name.Equals("concat", StringComparison.OrdinalIgnoreCase));

        ParameterSignature variadic = Assert.Single(concat.Parameters);
        Assert.Equal("...values", variadic.Name);
        Assert.Equal("String", variadic.Kind);
        Assert.False(variadic.IsOptional); // MinOccurrences=2 → not zero-allowed.
    }

    [Fact]
    public void Build_ScalarFunction_ZeroAllowedVariadicIsMarkedOptional()
    {
        // ArrayConstructorFunction has MinOccurrences:0 — calling array() with
        // no args is legal. The builder should flag the variadic optional so
        // the editor renders it with the trailing "?".
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<ArrayConstructorFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature array = Assert.Single(
            manifest.Functions, f => f.Name.Equals("array", StringComparison.OrdinalIgnoreCase));

        ParameterSignature variadic = Assert.Single(array.Parameters);
        Assert.True(variadic.IsOptional);
    }

    [Fact]
    public void Build_BodyScopedFunction_ExcludedFromManifest()
    {
        // infer() is body-scoped (only callable inside CREATE MODEL bodies).
        // It must NOT appear in the language-server manifest — surfacing it
        // in completion outside a model body would mislead. Discoverability
        // stays via datum_catalog.functions.body_scope; the plan-time gate
        // refuses out-of-context call sites if anyone types it by hand.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<UpperFunction>();
        functions.RegisterScalar<DatumIngest.Functions.InferFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        Assert.DoesNotContain(
            manifest.Functions,
            f => f.Name.Equals("infer", StringComparison.OrdinalIgnoreCase));
        // upper() (BodyScope = None) still surfaces — sanity check that
        // the filter only excludes body-scoped entries.
        Assert.Contains(
            manifest.Functions,
            f => f.Name.Equals("upper", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_NoModelCatalog_ModelsIsNull()
    {
        // Plain catalog with no ModelCatalog attached → manifest carries no
        // Models field at all. The CompletionProvider's null guard then
        // suppresses `models.X` completions cleanly.
        TableCatalog catalog = CreateCatalog();
        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, new FunctionRegistry());

        Assert.Null(manifest.Models);
    }

    [Fact]
    public void Build_WithModelCatalog_PopulatesModelEntriesWithParameters()
    {
        // Register an entry with one required input + two optional args
        // and verify the builder surfaces the full positional shape.
        TableCatalog catalog = CreateCatalog();
        ModelCatalog modelCatalog = new();
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "test_llm",
            Backend: "stub",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: false,
            Loader: _ => throw new InvalidOperationException("stub — never loaded in this test"),
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            Category: "llm",
            DisplayName: "Test LLM"));
        catalog.Models = modelCatalog;

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, new FunctionRegistry());

        Assert.NotNull(manifest.Models);
        ModelEntry model = Assert.Single(manifest.Models);
        Assert.Equal("test_llm", model.Name);
        Assert.Equal("String", model.OutputKind);
        Assert.Equal("llm", model.Category);
        Assert.Equal("Test LLM", model.DisplayName);

        Assert.NotNull(model.Parameters);
        Assert.Equal(3, model.Parameters.Count);

        // One required input → singular "input" name.
        Assert.Equal("input", model.Parameters[0].Name);
        Assert.Equal("String", model.Parameters[0].Kind);
        Assert.False(model.Parameters[0].IsOptional);

        // Two optionals → option1 / option2; both flagged optional.
        Assert.Equal("option1", model.Parameters[1].Name);
        Assert.Equal("Float64", model.Parameters[1].Kind);
        Assert.True(model.Parameters[1].IsOptional);

        Assert.Equal("option2", model.Parameters[2].Name);
        Assert.Equal("Int32", model.Parameters[2].Kind);
        Assert.True(model.Parameters[2].IsOptional);
    }

    [Fact]
    public void Build_CatalogVocabulary_SurfacesDiscoveredIdentifiersAlongsideRegistered()
    {
        // Two catalog-declared identifiers: one already registered in the
        // live ModelCatalog ("registered_llm") and one that exists only in
        // the catalog manifest ("discovered_llm"). Both should appear in
        // the manifest's Models list — registered with Status=Available,
        // catalog-only with Status=Discovered. Without this union, the
        // language-server autocomplete would only see installed models
        // and uninstalled catalog entries would be invisible until first
        // download.
        TableCatalog catalog = CreateCatalog();

        ModelCatalog modelCatalog = new();
        modelCatalog.Register(new ModelCatalogEntry(
            Name: "registered_llm",
            Backend: "stub",
            RelativePath: null,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: false,
            Loader: _ => throw new InvalidOperationException("stub — never loaded in this test"),
            Category: "llm",
            DisplayName: "Registered LLM"));
        catalog.Models = modelCatalog;

        CatalogVersion v = new(
            Version: "2026-05-30",
            Sources: [new HuggingFaceSource("repo", "main", [])],
            InstallSql: "sql/entry-a/2026-05-30.sql",
            Models: [
                new CatalogVersionModel("registered_llm"),
                new CatalogVersionModel("discovered_llm")]);
        CatalogModel entry = new(
            Id: "entry-a",
            DisplayName: "Entry A",
            Summary: "test.",
            Description: "test.",
            Tasks: ["TextEmbedder"],
            Tags: [],
            LicenseIds: [],
            Attributions: [],
            Hardware: new CatalogHardware(MinRamMb: 0, MinVramMb: 0, Preferred: "cpu"),
            Versions: [v],
            ApproxSizeMb: 100);
        CatalogManifest manifest = new(
            SchemaVersion: 2,
            Models: [entry]);
        catalog.CatalogVocabulary = new CatalogVocabulary(manifest);

        LanguageServerManifest result = CatalogManifestBuilder.Build(catalog, new FunctionRegistry());

        Assert.NotNull(result.Models);
        ModelEntry registered = Assert.Single(
            result.Models, m => m.Name == "registered_llm");
        Assert.Equal(ModelInstallStatus.Available, registered.Status);

        ModelEntry discovered = Assert.Single(
            result.Models, m => m.Name == "discovered_llm");
        Assert.Equal(ModelInstallStatus.Discovered, discovered.Status);
        Assert.Equal("Entry A", discovered.DisplayName);
        Assert.NotNull(discovered.Parameters);
        Assert.Empty(discovered.Parameters);
    }

    [Fact]
    public void Build_LambdaParameterSlot_PopulatesLambdaContextName()
    {
        // Critical end-to-end check that the runtime manifest carries the
        // lambda context name through to consumers (hover provider's lambda-
        // param card, completion provider's context-aware whitelist). If
        // this slot is null, both features silently degrade.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<AnimateFramesFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature af = Assert.Single(
            manifest.Functions, f => f.Name.Equals("animate_frames", StringComparison.OrdinalIgnoreCase));
        // The render_frame parameter must carry LambdaContextName = "animation".
        ParameterSignature renderFrame = af.Parameters[^1];
        Assert.Equal("render_frame", renderFrame.Name);
        Assert.Equal("animation", renderFrame.LambdaContextName);
    }

    [Fact]
    public void Build_FunctionContexts_Populated()
    {
        // The manifest's FunctionContexts list must include the engine's
        // built-in contexts so the LS resolver can walk parent chains.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<AnimateFramesFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        Assert.NotNull(manifest.FunctionContexts);
        Assert.Contains(manifest.FunctionContexts, c => c.Name == "animation");
        Assert.Contains(manifest.FunctionContexts, c => c.Name == "pure");
    }

    [Fact]
    public void Build_StringEnumParameter_PopulatesEnumValues()
    {
        // BlendFunction's `mode` parameter uses a StringEnumMatcher. The
        // manifest builder must surface its values via
        // ParameterSignature.EnumValues so the LS completion provider can
        // suggest 'add' / 'multiply' / … inside the string literal.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<BlendFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature blend = Assert.Single(
            manifest.Functions, f => f.Name.Equals("blend", StringComparison.OrdinalIgnoreCase));
        // The `mode` parameter is at index 1.
        ParameterSignature mode = blend.Parameters[1];
        Assert.Equal("mode", mode.Name);
        Assert.NotNull(mode.EnumValues);
        Assert.Contains("add", mode.EnumValues);
        Assert.Contains("multiply", mode.EnumValues);
        Assert.Contains("screen", mode.EnumValues);
    }

    [Fact]
    public void Build_ContextRestrictedFunction_PopulatesContexts()
    {
        // Animation curves should carry Contexts = ["animation"] in the
        // manifest so the completion provider's filter knows to hide them
        // from non-animation expression positions.
        TableCatalog catalog = CreateCatalog();
        FunctionRegistry functions = new();
        functions.RegisterScalar<OscillateFunction>();

        LanguageServerManifest manifest = CatalogManifestBuilder.Build(catalog, functions);

        FunctionSignature osc = Assert.Single(
            manifest.Functions, f => f.Name.Equals("oscillate", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(osc.Contexts);
        Assert.Contains("animation", osc.Contexts);
    }
}
