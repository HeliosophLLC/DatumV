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
    public void CreateModel_NoUsing_FailsToParse()
    {
        // USING is required — there's no ambient default for the model
        // file and the registrar wouldn't know what to load.
        Assert.ThrowsAny<Exception>(() => SqlParser.ParseStatement(
            "CREATE MODEL classify(img IMAGE) RETURNS INT32 " +
            "AS BEGIN RETURN 1 END"));
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
