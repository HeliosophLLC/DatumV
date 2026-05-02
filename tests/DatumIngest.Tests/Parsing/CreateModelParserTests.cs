using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Grammar tests for <c>CREATE MODEL</c> / <c>DROP MODEL</c>. Covers the
/// happy path, every modifier (<c>OR REPLACE</c>, <c>IF NOT EXISTS</c>,
/// <c>IS NOT NULL</c>), schema qualification, and the <c>USING</c>
/// contextual keyword. Also locks down that <c>MODEL</c> stays usable as
/// an ordinary identifier outside the CREATE/DROP MODEL positions —
/// adding a hard-keyword tokenizer entry for it once broke
/// <c>CREATE TABLE model</c> and <c>JOIN model</c>; this file pins that
/// surface in place.
/// </summary>
public sealed class CreateModelParserTests : ServiceTestBase
{
    [Fact]
    public void CreateModel_HappyPath_ParsesAllFields()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 USING 'classify.onnx' " +
            "AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("classify", create.Name);
        Assert.Null(create.SchemaName);
        Assert.Equal("INT32", create.ReturnTypeName);
        Assert.Equal("classify.onnx", create.UsingPath);
        Assert.False(create.OrReplace);
        Assert.False(create.IfNotExists);
        Assert.False(create.ReturnIsNotNull);
        Assert.Single(create.Parameters);
        Assert.Equal("img", create.Parameters[0].Name);
        Assert.Single(create.StatementBody);
        Assert.IsType<ReturnStatement>(create.StatementBody[0]);
    }

    [Fact]
    public void CreateModel_WithoutAs_ParsesBareBeginEnd()
    {
        // The grammar accepts BEGIN…END with or without a leading AS.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 USING 'classify.onnx' " +
            "BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("classify", create.Name);
    }

    [Fact]
    public void CreateModel_OrReplace_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE OR REPLACE MODEL classify(img IMAGE) RETURNS INT32 " +
            "USING 'classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.True(create.OrReplace);
    }

    [Fact]
    public void CreateModel_IfNotExists_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL IF NOT EXISTS classify(img IMAGE) RETURNS INT32 " +
            "USING 'classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.True(create.IfNotExists);
    }

    [Fact]
    public void CreateModel_ReturnsIsNotNull_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 IS NOT NULL " +
            "USING 'classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.True(create.ReturnIsNotNull);
    }

    [Fact]
    public void CreateModel_SchemaQualified_PopulatesSchema()
    {
        // Per the registrar's schema lockdown the only legal qualifier is
        // `models`, but the parser is permissive — it surfaces whatever
        // qualifier the user wrote and the registrar enforces the rule.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL models.classify(img IMAGE) RETURNS INT32 " +
            "USING 'classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("models", create.SchemaName);
        Assert.Equal("classify", create.Name);
    }

    [Fact]
    public void CreateModel_UsingFilePrefix_PreservedRaw()
    {
        // The parser hands the USING path through verbatim; `file://`
        // resolution is the registrar's job.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
            "USING 'file:///tmp/classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("file:///tmp/classify.onnx", create.UsingPath);
    }

    [Fact]
    public void CreateModel_StructReturnWithFields_ParsesCanonicalAnnotation()
    {
        // `RETURNS Struct<depth Array<Float32>, intrinsics Array<Float32>>`
        // — the SQL surface uses bare `name Type` pairs; the canonical
        // form the parser emits inserts `: ` so the LS can re-parse it
        // unambiguously without re-running the full SQL grammar.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL depth_full(img IMAGE) " +
            "RETURNS Struct<depth Array<Float32>, intrinsics Array<Float32>> " +
            "USING 'depth.onnx' AS BEGIN RETURN { depth: 1, intrinsics: 1 } END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal(
            "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
            create.ReturnTypeName);
    }

    [Fact]
    public void CreateModel_StructReturnWithColonSyntax_NormalisesToCanonical()
    {
        // The canonical rendered form (`Struct<name: Kind, ...>`) uses
        // colons, so a user reading hover output and pasting it back into
        // SQL hits the colon form. Accept it; canonicalise to match the
        // non-colon source form.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL depth_full(img IMAGE) " +
            "RETURNS Struct<depth: Array<Float32>, intrinsics: Array<Float32>> " +
            "USING 'depth.onnx' AS BEGIN RETURN { depth: 1, intrinsics: 1 } END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal(
            "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
            create.ReturnTypeName);
    }

    [Fact]
    public void CreateModel_BareStructReturn_StillParses()
    {
        // The opaque `RETURNS Struct` form (no field list) must keep
        // working — every existing SQL-defined struct-returning model uses
        // it today, and the LS treats it as opaque.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL opaque_struct(img IMAGE) RETURNS Struct " +
            "USING 'x.onnx' AS BEGIN RETURN { a: 1 } END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("Struct", create.ReturnTypeName);
    }

    [Fact]
    public void CreateModel_UsingLowercase_AcceptedContextually()
    {
        // USING is a contextual identifier (Identifier+Where), so case
        // doesn't matter and the lowercase form must parse.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
            "using 'classify.onnx' AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Equal("classify.onnx", create.UsingPath);
    }

    [Fact]
    public void CreateModel_EmptyUsingPath_ThrowsParseError()
    {
        // BuildCreateModelStatement rejects whitespace-only USING so the
        // bad statement surfaces as a clean parse error rather than
        // crashing the registrar later.
        FormatException ex = Assert.Throws<FormatException>(
            () => SqlParser.ParseStatement(
                "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
                "USING '' AS BEGIN RETURN 1 END"));
        Assert.Contains("USING clause", ex.Message);
    }

    [Fact]
    public void CreateModel_NoReturns_FailsToParse()
    {
        // RETURNS is required on MODEL — the planner needs a known scalar
        // shape to dispatch through `infer()`.
        Assert.ThrowsAny<Exception>(() => SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) USING 'classify.onnx' " +
            "AS BEGIN RETURN 1 END"));
    }

    [Fact]
    public void CreateModel_NoUsing_ParsesAsDelegatingModel()
    {
        // No USING clause is accepted: a delegating model whose body
        // produces its result by calling another model or UDF, with no
        // weights of its own. UsingPath and UsingFiles both stay null;
        // the registrar binds zero sessions for these.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
            "AS BEGIN RETURN 1 END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Null(create.UsingPath);
        Assert.Null(create.UsingFiles);
        Assert.Equal("classify", create.Name);
        Assert.Equal("INT32", create.ReturnTypeName);
    }

    [Fact]
    public void CreateModel_NoUsing_BodyCanDelegateToAnotherModel()
    {
        // The realistic delegating shape: a TextGenerator surface that
        // wraps a sibling ChatCompleter model. No USING — both surfaces
        // share the underlying weights via the sibling's session, and
        // the simple form costs zero additional VRAM. Struct literals
        // use the curly-brace syntax (`{ role: 'user', content: prompt }`);
        // arrays of structs wrap them in the standard `[...]` array
        // literal.
        Statement stmt = SqlParser.ParseStatement(
            "CREATE MODEL phi35_mini(prompt STRING) RETURNS STRING " +
            "IMPLEMENTS TextGenerator " +
            "AS BEGIN " +
            "  RETURN models.phi35_mini_chat([{ role: 'user', content: prompt }]) " +
            "END");

        CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
        Assert.Null(create.UsingPath);
        Assert.Null(create.UsingFiles);
        Assert.Equal("TextGenerator", create.ImplementsTaskName);
    }

    [Fact]
    public void CreateModel_NoBody_FailsToParse()
    {
        // Body is always procedural — no expression-body form because the
        // only legal use of a model body is to call infer().
        Assert.ThrowsAny<Exception>(() => SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
            "USING 'classify.onnx'"));
    }

    [Fact]
    public void DropModel_HappyPath_PopulatesName()
    {
        Statement stmt = SqlParser.ParseStatement("DROP MODEL classify");

        DropModelStatement drop = Assert.IsType<DropModelStatement>(stmt);
        Assert.Equal("classify", drop.Name);
        Assert.Null(drop.SchemaName);
        Assert.False(drop.IfExists);
    }

    [Fact]
    public void DropModel_IfExists_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement("DROP MODEL IF EXISTS classify");

        DropModelStatement drop = Assert.IsType<DropModelStatement>(stmt);
        Assert.True(drop.IfExists);
        Assert.Equal("classify", drop.Name);
    }

    [Fact]
    public void DropModel_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement("DROP MODEL models.classify");

        DropModelStatement drop = Assert.IsType<DropModelStatement>(stmt);
        Assert.Equal("models", drop.SchemaName);
        Assert.Equal("classify", drop.Name);
    }

    [Fact]
    public void EvictModel_HappyPath_PopulatesName()
    {
        Statement stmt = SqlParser.ParseStatement("EVICT MODEL classify");

        EvictModelStatement evict = Assert.IsType<EvictModelStatement>(stmt);
        Assert.Equal("classify", evict.Name);
        Assert.Null(evict.SchemaName);
        Assert.False(evict.IfExists);
    }

    [Fact]
    public void EvictModel_IfExists_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement("EVICT MODEL IF EXISTS classify");

        EvictModelStatement evict = Assert.IsType<EvictModelStatement>(stmt);
        Assert.True(evict.IfExists);
        Assert.Equal("classify", evict.Name);
    }

    [Fact]
    public void EvictModel_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement("EVICT MODEL models.classify");

        EvictModelStatement evict = Assert.IsType<EvictModelStatement>(stmt);
        Assert.Equal("models", evict.SchemaName);
        Assert.Equal("classify", evict.Name);
    }

    [Fact]
    public void ResetCalibration_HappyPath_PopulatesName()
    {
        Statement stmt = SqlParser.ParseStatement("RESET CALIBRATION classify");

        ResetCalibrationStatement reset = Assert.IsType<ResetCalibrationStatement>(stmt);
        Assert.Equal("classify", reset.Name);
        Assert.Null(reset.SchemaName);
        Assert.False(reset.IfExists);
    }

    [Fact]
    public void ResetCalibration_IfExists_SetsFlag()
    {
        Statement stmt = SqlParser.ParseStatement("RESET CALIBRATION IF EXISTS classify");

        ResetCalibrationStatement reset = Assert.IsType<ResetCalibrationStatement>(stmt);
        Assert.True(reset.IfExists);
        Assert.Equal("classify", reset.Name);
    }

    [Fact]
    public void ResetCalibration_SchemaQualified_PopulatesSchema()
    {
        Statement stmt = SqlParser.ParseStatement("RESET CALIBRATION models.classify");

        ResetCalibrationStatement reset = Assert.IsType<ResetCalibrationStatement>(stmt);
        Assert.Equal("models", reset.SchemaName);
        Assert.Equal("classify", reset.Name);
    }

    // ───────────────────── `model` as identifier regression ─────────────────────
    //
    // MODEL must remain a *contextual* identifier — it's only consumed as
    // a keyword inside CREATE/DROP MODEL. A previous attempt to make it
    // a hard keyword broke `CREATE TABLE model`, `INSERT INTO model`,
    // `JOIN model AS m`, and `SELECT model.col`. These tests pin the
    // contextual surface in place.

    [Fact]
    public void Model_AsBareTableName_ParsesAsCreateTable()
    {
        Statement stmt = SqlParser.ParseStatement(
            "CREATE TABLE model (id Int32, weight Float32)");

        CreateTableStatement create = Assert.IsType<CreateTableStatement>(stmt);
        Assert.Equal("model", create.TableName);
    }

    [Fact]
    public void Model_AsTableName_InInsert_Parses()
    {
        Statement stmt = SqlParser.ParseStatement(
            "INSERT INTO model (id, weight) VALUES (1, 0.5)");

        InsertStatement insert = Assert.IsType<InsertStatement>(stmt);
        Assert.Equal("model", insert.TableName);
    }

    [Fact]
    public void Model_AsTableName_InUpdateJoin_Parses()
    {
        // The exact shape that surfaced the regression in
        // DdlParsingTests.UpdateWithFromAndJoin.
        Statement stmt = SqlParser.ParseStatement(
            "UPDATE features SET score = raw.value * m.weight " +
            "FROM raw JOIN model AS m ON raw.model_id = m.id " +
            "WHERE features.id = raw.id");

        UpdateStatement update = Assert.IsType<UpdateStatement>(stmt);
        Assert.Equal("features", update.TableName);
        Assert.NotNull(update.Joins);
        Assert.Single(update.Joins!);
    }

    [Fact]
    public void Model_AsColumnName_InSelect_Parses()
    {
        QueryExpression q = SqlParser.Parse("SELECT model FROM t");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        ColumnReference col = Assert.IsType<ColumnReference>(
            sqe.Statement.Columns[0].Expression);
        Assert.Equal("model", col.ColumnName);
    }
}
