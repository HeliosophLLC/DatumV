using DatumIngest.Catalog;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar;
using DatumIngest.Manifest;
using DatumIngest.Model;
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
}
