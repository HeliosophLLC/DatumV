using System.Diagnostics.CodeAnalysis;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.ModelLibrary;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Exercises <see cref="PreFlightWalker"/> against a hand-rolled catalog
/// + model surface. Validates the three model-reference reasons
/// (ModelNotInstalled / PinnedVersionNotInstalled / PinnedVersionUnknown),
/// the bare-name typo path, dedupe of repeated call sites, and the
/// no-op happy path when references are already installed.
/// </summary>
public sealed class PreFlightWalkerTests : ServiceTestBase
{
    private static CatalogManifest BuildManifestWithEntry(
        string entryId,
        string identifier,
        string versionString,
        string? pinnedAsOverride = null)
    {
        CatalogVersion v = new(
            Version: versionString,
            Sources: [new HuggingFaceSource("repo", "main", [])],
            InstallSql: $"sql/{entryId}/{versionString}.sql",
            Models: [new CatalogVersionModel(identifier, pinnedAsOverride)]);
        CatalogModel entry = new(
            Id: entryId,
            DisplayName: entryId,
            Summary: "Test entry.",
            Description: "Test.",
            Tasks: ["TextEmbedder"],
            Tags: [],
            LicenseIds: [],
            Attributions: [],
            Hardware: new CatalogHardware(MinRamMb: 0, MinVramMb: 0, Preferred: "cpu"),
            Versions: [v],
            ApproxSizeMb: 42);
        return new CatalogManifest(
            SchemaVersion: 2,
            Models: [entry]);
    }

    private static QueryExpression Parse(string sql)
    {
        Statement stmt = SqlParser.ParseStatement(sql);
        QueryStatement qs = Assert.IsType<QueryStatement>(stmt);
        return qs.Query;
    }

    [Fact]
    public void BareReference_NotInstalled_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "depth-anything-v3-large",
            identifier: "depth_anything_v3_large_meters",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.depth_anything_v3_large_meters(img) FROM frames");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("models.depth_anything_v3_large_meters", req.TypedReference);
        Assert.Equal("depth_anything_v3_large_meters", req.Identifier);
        Assert.Equal("depth-anything-v3-large", req.CatalogEntryId);
        Assert.Equal("2026-05-29", req.Version);
        Assert.False(req.VersionPinned);
        Assert.Equal(PreFlightReason.ModelNotInstalled, req.Reason);
        Assert.Equal(42, req.ApproxSizeMb);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void PinnedReference_VersionExistsButNotOnDisk_EmitsPinnedNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.foo@20260529(x)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("models.foo@20260529", req.TypedReference);
        Assert.Equal("foo", req.Identifier);
        Assert.Equal("foo-entry", req.CatalogEntryId);
        Assert.Equal("2026-05-29", req.Version);
        Assert.True(req.VersionPinned);
        Assert.Equal(PreFlightReason.PinnedVersionNotInstalled, req.Reason);
    }

    [Fact]
    public void PinnedReference_UnknownVersion_EmitsPinnedVersionUnknown()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.foo@20240101(x)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal(PreFlightReason.PinnedVersionUnknown, req.Reason);
        Assert.True(req.VersionPinned);
        Assert.Null(req.Version);
        Assert.Equal("foo", req.Identifier);
    }

    [Fact]
    public void TypoBareName_EmitsSuggestion()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo_meters",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        // Unknown bare-name with no models. schema → suggestion path
        // against the catalog-known identifier `foo_meters`.
        QueryExpression q = Parse("SELECT models.foo_metres(x)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        Assert.Empty(result.Models);
        PreFlightSuggestion s = Assert.Single(result.Suggestions);
        Assert.Equal("models.foo_metres", s.TypedName);
        Assert.Equal("models.foo_meters", s.Suggestion);
    }

    [Fact]
    public void RepeatedReference_DedupedToOneRequirement()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.foo(a), models.foo(b)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        Assert.Single(result.Models);
    }

    [Fact]
    public void Cte_BodyReference_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "depth-anything-v3-large",
            identifier: "depth_anything_v3_large_full",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse(
            "WITH frames AS (SELECT models.depth_anything_v3_large_full(img) AS d FROM vid) "
            + "SELECT d FROM frames");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("depth_anything_v3_large_full", req.Identifier);
        Assert.Equal(PreFlightReason.ModelNotInstalled, req.Reason);
    }

    [Fact]
    public void Cte_RecursiveMember_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "step-entry",
            identifier: "step_fn",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse(
            "WITH RECURSIVE walk AS ("
            + " SELECT 0 AS n, 1 AS v"
            + " UNION ALL"
            + " SELECT n + 1, models.step_fn(v) FROM walk WHERE n < 5"
            + ") SELECT * FROM walk");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("step_fn", req.Identifier);
        Assert.Equal(PreFlightReason.ModelNotInstalled, req.Reason);
    }

    [Fact]
    public void UnknownReference_NoCatalog_NoSuggestion_StaysSilent()
    {
        // No vocabulary at all + no scalar functions registered: the walker
        // has no candidates to suggest against, so it silently passes the
        // call through and lets PlanTimeFunctionGate surface the
        // "Unknown function" error downstream.
        FunctionRegistry functions = new();
        QueryExpression q = Parse("SELECT random_thing()");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocabulary: null, functions);

        Assert.Empty(result.Models);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void PinSuffixTokenizesAsOneIdentifier()
    {
        // Parser-level sanity: `models.foo@20260529(x)` tokenizes as one
        // identifier `foo@20260529` under the namespace `models`.
        // Validating here so a tokenizer regression surfaces alongside
        // the pre-flight contract that depends on it.
        QueryExpression q = Parse("SELECT models.foo@20260529(x)");
        SelectQueryExpression sel = Assert.IsType<SelectQueryExpression>(q);
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(sel.Statement.Columns[0].Expression);
        Assert.Equal("models", call.SchemaName);
        Assert.Equal("foo@20260529", call.FunctionName);
    }

    [Fact]
    public void Vocabulary_ByPinnedAs_ResolvesSuffixToEntry()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);

        Assert.True(vocab.ByPinnedAs.TryGetValue("foo@20260529", out CatalogPinnedReference? pin));
        Assert.Equal("foo", pin.Identifier);
        Assert.Equal("foo-entry", pin.Entry.CatalogEntryId);
        Assert.Equal("2026-05-29", pin.Version.VersionString);
    }

    [Fact]
    public void PinnedReference_NumericTypo_EmitsSuggestionAgainstByPinnedAs()
    {
        // `foo@2026529` is one digit short of `foo@20260529` — Levenshtein
        // finds the materialised pinnedAs and the walker emits a
        // Suggestion instead of PinnedVersionUnknown. The user fixes
        // the typo and resubmits, then gets a real residency check.
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.foo@2026529(x)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        Assert.Empty(result.Models);
        PreFlightSuggestion s = Assert.Single(result.Suggestions);
        Assert.Equal("models.foo@2026529", s.TypedName);
        Assert.Equal("models.foo@20260529", s.Suggestion);
    }

    [Fact]
    public void Update_WhereClause_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "is-blurry-entry",
            identifier: "is_blurry",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "UPDATE photos SET tagged = true WHERE models.is_blurry(image)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("is_blurry", req.Identifier);
        Assert.Equal(PreFlightReason.ModelNotInstalled, req.Reason);
    }

    [Fact]
    public void Update_SetAssignment_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "captioner-entry",
            identifier: "captioner",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "UPDATE photos SET caption = models.captioner(image)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("captioner", req.Identifier);
    }

    [Fact]
    public void Delete_WhereClause_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "is-blurry-entry",
            identifier: "is_blurry",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "DELETE FROM photos WHERE models.is_blurry(image)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("is_blurry", req.Identifier);
    }

    [Fact]
    public void Delete_ReturningProjection_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "embedder-entry",
            identifier: "embed_text",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "DELETE FROM docs WHERE id = 1 RETURNING models.embed_text(body)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("embed_text", req.Identifier);
    }

    [Fact]
    public void Insert_ValuesTuple_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "captioner-entry",
            identifier: "captioner",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "INSERT INTO photos (caption) VALUES (models.captioner('hi'))");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("captioner", req.Identifier);
    }

    [Fact]
    public void Insert_ReturningProjection_EmitsModelNotInstalled()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "embedder-entry",
            identifier: "embed_text",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "INSERT INTO docs (body) VALUES ('hi') RETURNING models.embed_text(body)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal("embed_text", req.Identifier);
    }

    [Fact]
    public void Ddl_NoModelReferences_EmitsNothing()
    {
        // CREATE TABLE / CREATE FUNCTION / CREATE MODEL stay opaque to
        // pre-flight — the walker returns empty for them so DDL is never
        // blocked by an as-yet-uninstalled model the body might reference.
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        Statement stmt = SqlParser.ParseStatement(
            "CREATE TABLE t (id INT32, caption STRING)");
        PreFlightRequirements result = PreFlightWalker.WalkStatement(stmt, models: null, vocab, functions);

        Assert.Empty(result.Models);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void BareReference_PopulatesLicenseIds()
    {
        // Hand-rolled entry declaring two license ids. The walker should
        // surface both on the emitted requirement so the install modal
        // can prompt acceptance up front instead of letting per-install
        // 412 retries race to open separate dialogs.
        CatalogVersion v = new(
            Version: "2026-05-29",
            Sources: [new HuggingFaceSource("repo", "main", [])],
            InstallSql: "sql/x.sql",
            Models: [new CatalogVersionModel("foo", null)]);
        CatalogModel entry = new(
            Id: "foo-entry",
            DisplayName: "Foo",
            Summary: "Test entry.",
            Description: "Test.",
            Tasks: ["TextEmbedder"],
            Tags: [],
            LicenseIds: ["openrail-pp", "stability-ai-community"],
            Attributions: [],
            Hardware: new CatalogHardware(MinRamMb: 0, MinVramMb: 0, Preferred: "cpu"),
            Versions: [v],
            ApproxSizeMb: 42);
        CatalogManifest manifest = new(
            SchemaVersion: 2,
            Models: [entry]);
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT models.foo(x)");
        PreFlightRequirements result = PreFlightWalker.Walk(q, models: null, vocab, functions);

        PreFlightModelRequirement req = Assert.Single(result.Models);
        Assert.Equal(2, req.LicenseIds.Count);
        Assert.Contains("openrail-pp", req.LicenseIds);
        Assert.Contains("stability-ai-community", req.LicenseIds);
    }

    [Fact]
    public void DatasetReference_UninstalledVariant_EmitsDatasetRequirement()
    {
        FunctionRegistry functions = new();
        StubDatasetSource source = new(
            schemas: ["datasets"],
            candidates: new()
            {
                [("datasets", "coco_test2017")] = new PreFlightDatasetCandidate(
                    VariantId: "coco_test2017",
                    EntryName: "COCO 2017",
                    DisplayName: "test2017 (images)",
                    Version: "2017",
                    ApproxArchiveBytes: 6_646_972_416,
                    LicenseIds: ["cc-by-4.0"],
                    IsInstalled: false),
            });

        QueryExpression q = Parse("SELECT * FROM datasets.coco_test2017");
        PreFlightRequirements result = PreFlightWalker.Walk(
            q, models: null, vocabulary: null, functions, source);

        PreFlightDatasetRequirement req = Assert.Single(result.Datasets);
        Assert.Equal("datasets.coco_test2017", req.TypedReference);
        Assert.Equal("coco_test2017", req.Identifier);
        Assert.Equal("coco_test2017", req.VariantId);
        Assert.Equal("COCO 2017", req.EntryName);
        Assert.Equal("2017", req.Version);
        Assert.Equal(6_646_972_416, req.ApproxArchiveBytes);
        Assert.Single(req.LicenseIds);
        Assert.Empty(result.Models);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void DatasetReference_InstalledVariant_EmitsNothing()
    {
        FunctionRegistry functions = new();
        StubDatasetSource source = new(
            schemas: ["datasets"],
            candidates: new()
            {
                [("datasets", "coco_test2017")] = new PreFlightDatasetCandidate(
                    VariantId: "coco_test2017",
                    EntryName: "COCO 2017",
                    DisplayName: "test2017 (images)",
                    Version: "2017",
                    ApproxArchiveBytes: 6_646_972_416,
                    LicenseIds: ["cc-by-4.0"],
                    IsInstalled: true),
            });

        QueryExpression q = Parse("SELECT * FROM datasets.coco_test2017");
        PreFlightRequirements result = PreFlightWalker.Walk(
            q, models: null, vocabulary: null, functions, source);

        Assert.Empty(result.Datasets);
        Assert.Empty(result.Models);
        Assert.Empty(result.Suggestions);
    }

    [Fact]
    public void DatasetReference_NoDatasetSource_EmitsNothing()
    {
        FunctionRegistry functions = new();

        QueryExpression q = Parse("SELECT * FROM datasets.coco_test2017");
        PreFlightRequirements result = PreFlightWalker.Walk(
            q, models: null, vocabulary: null, functions);

        Assert.Empty(result.Datasets);
    }

    [Fact]
    public void DatasetReference_RepeatedSiteInOneQuery_DedupesToOneRequirement()
    {
        FunctionRegistry functions = new();
        StubDatasetSource source = new(
            schemas: ["datasets"],
            candidates: new()
            {
                [("datasets", "coco_test2017")] = new PreFlightDatasetCandidate(
                    VariantId: "coco_test2017",
                    EntryName: "COCO 2017",
                    DisplayName: "test2017 (images)",
                    Version: "2017",
                    ApproxArchiveBytes: 6_646_972_416,
                    LicenseIds: [],
                    IsInstalled: false),
            });

        QueryExpression q = Parse(
            "SELECT * FROM datasets.coco_test2017 AS a "
            + "JOIN datasets.coco_test2017 AS b ON a.name = b.name");
        PreFlightRequirements result = PreFlightWalker.Walk(
            q, models: null, vocabulary: null, functions, source);

        Assert.Single(result.Datasets);
    }

    [Fact]
    public void Vocabulary_ByPinnedAs_RespectsExplicitOverride()
    {
        CatalogManifest manifest = BuildManifestWithEntry(
            entryId: "foo-entry",
            identifier: "foo",
            versionString: "2026-05-29",
            pinnedAsOverride: "foo@9999");
        ICatalogVocabulary vocab = new CatalogVocabulary(manifest);

        Assert.True(vocab.ByPinnedAs.TryGetValue("foo@9999", out _));
        Assert.False(vocab.ByPinnedAs.TryGetValue("foo@20260529", out _));
    }

    // Hand-rolled IPreFlightDatasetSource for the dataset-reference tests.
    // The real binder builds its candidate dict from the dataset manifest;
    // the stub lets each test plant exactly the (schema, name) entries
    // it cares about without standing up a full manifest store.
    private sealed class StubDatasetSource : IPreFlightDatasetSource
    {
        private readonly HashSet<string> _schemas;
        private readonly Dictionary<(string, string), PreFlightDatasetCandidate> _candidates;

        public StubDatasetSource(
            IReadOnlyCollection<string> schemas,
            Dictionary<(string, string), PreFlightDatasetCandidate> candidates)
        {
            _schemas = new HashSet<string>(schemas, StringComparer.OrdinalIgnoreCase);
            _candidates = candidates;
        }

        public bool IsDatasetSchema(string schema)
            => _schemas.Contains(schema);

        public bool TryDescribe(
            string schema,
            string name,
            [NotNullWhen(true)] out PreFlightDatasetCandidate? candidate)
        {
            if (_candidates.TryGetValue((schema, name), out PreFlightDatasetCandidate? c))
            {
                candidate = c;
                return true;
            }
            candidate = null;
            return false;
        }
    }
}
