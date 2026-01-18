using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for the top-level <see cref="Statement"/> overloads of
/// <see cref="ParameterBinder.Bind(Statement, IReadOnlyDictionary{string, ParameterValue})"/>
/// and the multi-statement batch variant — covers INSERT / UPDATE /
/// DELETE / EXEC / multi-statement union validation /
/// <see cref="BinaryParameter"/> / <see cref="StringParameter"/> shapes
/// added for the multipart endpoint.
/// </summary>
public sealed class StatementParameterBindingTests
{
    private static Statement ParseSingle(string sql)
    {
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(sql);
        Assert.Single(stmts);
        return stmts[0];
    }

    private static IReadOnlyList<Statement> ParseBatch(string sql) =>
        SqlParser.ParseBatch(sql);

    // ───────────────────── INSERT ─────────────────────

    [Fact]
    public void Bind_InsertValues_SubstitutesParameters()
    {
        InsertStatement stmt = (InsertStatement)ParseSingle(
            "INSERT INTO uploads (id, label) VALUES ($id, $label)");
        Dictionary<string, DataValue> parameters = new()
        {
            ["id"] = DataValue.FromInt32(42),
            ["label"] = DataValue.FromString("hello"),
        };

        InsertStatement bound = (InsertStatement)ParameterBinder.Bind((Statement)stmt, parameters);
        InsertValuesSource source = Assert.IsType<InsertValuesSource>(bound.Source);
        Assert.Single(source.Rows);
        IReadOnlyList<Expression> row = source.Rows[0];
        LiteralExpression idLit = Assert.IsType<LiteralExpression>(row[0]);
        LiteralExpression labelLit = Assert.IsType<LiteralExpression>(row[1]);
        Assert.Equal(42, idLit.Value);
        Assert.Equal("hello", labelLit.Value);
    }

    [Fact]
    public void Bind_InsertSelect_SubstitutesParameters()
    {
        InsertStatement stmt = (InsertStatement)ParseSingle(
            "INSERT INTO uploads (id) SELECT $id");
        Dictionary<string, DataValue> parameters = new() { ["id"] = DataValue.FromInt32(7) };

        InsertStatement bound = (InsertStatement)ParameterBinder.Bind((Statement)stmt, parameters);
        InsertQuerySource source = Assert.IsType<InsertQuerySource>(bound.Source);
        SelectQueryExpression sq = Assert.IsType<SelectQueryExpression>(source.Query);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(sq.Statement.Columns[0].Expression);
        Assert.Equal(7, lit.Value);
    }

    // ───────────────────── UPDATE ─────────────────────

    [Fact]
    public void Bind_Update_SubstitutesAssignmentAndWhereParameters()
    {
        UpdateStatement stmt = (UpdateStatement)ParseSingle(
            "UPDATE messages SET content = $text WHERE id = $id");
        Dictionary<string, DataValue> parameters = new()
        {
            ["text"] = DataValue.FromString("new"),
            ["id"] = DataValue.FromInt64(99),
        };

        UpdateStatement bound = (UpdateStatement)ParameterBinder.Bind((Statement)stmt, parameters);
        LiteralExpression assignLit = Assert.IsType<LiteralExpression>(bound.Assignments[0].Value);
        Assert.Equal("new", assignLit.Value);
        BinaryExpression whereExpr = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression idLit = Assert.IsType<LiteralExpression>(whereExpr.Right);
        Assert.Equal(99L, idLit.Value);
    }

    // ───────────────────── DELETE ─────────────────────

    [Fact]
    public void Bind_Delete_SubstitutesWhereParameter()
    {
        DeleteStatement stmt = (DeleteStatement)ParseSingle(
            "DELETE FROM uploads WHERE id = $id");
        Dictionary<string, DataValue> parameters = new() { ["id"] = DataValue.FromInt32(5) };

        DeleteStatement bound = (DeleteStatement)ParameterBinder.Bind((Statement)stmt, parameters);
        BinaryExpression whereExpr = Assert.IsType<BinaryExpression>(bound.Where);
        LiteralExpression idLit = Assert.IsType<LiteralExpression>(whereExpr.Right);
        Assert.Equal(5, idLit.Value);
    }

    // ───────────────────── Multi-statement ─────────────────────

    [Fact]
    public void Bind_MultiStatement_UnionValidationAcceptsParamUsedInOnlyOne()
    {
        IReadOnlyList<Statement> stmts = ParseBatch(
            "DELETE FROM uploads WHERE id = $a; SELECT 1;");
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["a"] = new InlineParameter(DataValue.FromInt32(1)),
        };

        IReadOnlyList<Statement> bound = ParameterBinder.Bind(stmts, parameters);
        Assert.Equal(2, bound.Count);
        DeleteStatement del = Assert.IsType<DeleteStatement>(bound[0]);
        BinaryExpression whereExpr = Assert.IsType<BinaryExpression>(del.Where);
        LiteralExpression idLit = Assert.IsType<LiteralExpression>(whereExpr.Right);
        Assert.Equal(1, idLit.Value);
    }

    [Fact]
    public void Bind_MultiStatement_RejectsUnusedParameter()
    {
        IReadOnlyList<Statement> stmts = ParseBatch("SELECT 1; SELECT 2;");
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["unused"] = new InlineParameter(DataValue.FromInt32(0)),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ParameterBinder.Bind(stmts, parameters));
        Assert.Contains("$unused", ex.Message);
    }

    [Fact]
    public void Bind_MultiStatement_RejectsMissingParameter()
    {
        IReadOnlyList<Statement> stmts = ParseBatch(
            "SELECT $a; SELECT $b;");
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["a"] = new InlineParameter(DataValue.FromInt32(1)),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(
            () => ParameterBinder.Bind(stmts, parameters));
        Assert.Contains("$b", ex.Message);
    }

    // ───────────────────── BinaryParameter ─────────────────────

    [Fact]
    public void Bind_BinaryParameter_ProducesLiteralCarryingBinaryParameter()
    {
        // SELECT models.depth_estimation($img) is the canonical multipart
        // use case. The binder doesn't need to know about kind-specific
        // materialisation — it just stashes the BinaryParameter in the
        // LiteralExpression for the evaluator to handle later.
        Statement stmt = ParseSingle("SELECT $img");
        byte[] payload = [1, 2, 3, 4, 5];
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["img"] = new BinaryParameter(DataKind.Image, payload),
        };

        QueryStatement bound = (QueryStatement)ParameterBinder.Bind(stmt, parameters);
        SelectQueryExpression sq = Assert.IsType<SelectQueryExpression>(bound.Query);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(sq.Statement.Columns[0].Expression);
        BinaryParameter carried = Assert.IsType<BinaryParameter>(lit.Value);
        Assert.Equal(DataKind.Image, carried.Kind);
        Assert.Equal(payload, carried.Bytes);
    }

    [Fact]
    public void Bind_BinaryParameter_PreservesAcrossNestedExpressions()
    {
        Statement stmt = ParseSingle(
            "SELECT * FROM t WHERE matches(col, $needle)");
        byte[] payload = new byte[1024];
        for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i & 0xFF);
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["needle"] = new BinaryParameter(DataKind.UInt8, payload),
        };

        QueryStatement bound = (QueryStatement)ParameterBinder.Bind(stmt, parameters);
        SelectStatement select = ((SelectQueryExpression)bound.Query).Statement;
        FunctionCallExpression call = Assert.IsType<FunctionCallExpression>(select.Where);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(call.Arguments[1]);
        BinaryParameter carried = Assert.IsType<BinaryParameter>(lit.Value);
        Assert.Equal(payload, carried.Bytes);
    }

    // ───────────────────── StringParameter ─────────────────────

    [Fact]
    public void Bind_StringParameter_LongString_SurvivesAsRawString()
    {
        // > 16 UTF-8 bytes — would fail through DataValue.FromString(s)
        // without a store. StringParameter's deferred materialisation
        // path lets the endpoint hand off the raw string and have
        // ExpressionEvaluator's LiteralExpression-string case build the
        // DataValue against the active query store.
        string longString = new('x', 256);
        Statement stmt = ParseSingle("SELECT $msg");
        Dictionary<string, ParameterValue> parameters = new()
        {
            ["msg"] = new StringParameter(longString),
        };

        QueryStatement bound = (QueryStatement)ParameterBinder.Bind(stmt, parameters);
        SelectQueryExpression sq = Assert.IsType<SelectQueryExpression>(bound.Query);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(sq.Statement.Columns[0].Expression);
        Assert.Equal(longString, lit.Value);
    }

    // ───────────────────── Identity for unsupported statements ─────────────────────

    [Fact]
    public void Bind_DropTable_NoParametersIsIdentity()
    {
        Statement stmt = ParseSingle("DROP TABLE foo");
        Dictionary<string, ParameterValue> parameters = new();

        Statement bound = ParameterBinder.Bind(stmt, parameters);
        Assert.Same(stmt, bound);
    }

    [Fact]
    public void CollectParameterNames_Statement_RecursesIntoNestedConstructs()
    {
        // A WHILE around an UPDATE inside a TRY: parameter names from the
        // predicate, the UPDATE body, and the FINALLY block all surface.
        Statement stmt = ParseSingle(
            "BEGIN " +
            "  WHILE $more BEGIN UPDATE t SET v = $v WHERE id = $id; END; " +
            "END");

        HashSet<string> names = ParameterBinder.CollectParameterNames(stmt);
        Assert.Equal(3, names.Count);
        Assert.Contains("more", names);
        Assert.Contains("v", names);
        Assert.Contains("id", names);
    }
}
