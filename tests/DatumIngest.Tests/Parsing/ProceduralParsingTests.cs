using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// AST-shape tests for the procedural-flow grammar (BEGIN/END, IF/ELSE,
/// WHILE, FOR/IN, FOR ... TO, DECLARE, SET, and the <c>var</c> expression).
/// Slice 2 — parsing only; no execution semantics yet.
/// </summary>
public class ProceduralParsingTests : ServiceTestBase
{
    private static T Parse<T>(string sql) where T : Statement
    {
        Statement stmt = SqlParser.ParseStatement(sql);
        return Assert.IsType<T>(stmt);
    }

    // ───────────────────── var expression ─────────────────────

    [Fact]
    public void BareVariableReference_ParsesAsUnqualifiedColumnReference()
    {
        // A bare identifier in expression position parses as a
        // ColumnReference. The evaluator's variable-first precedence
        // resolves it against VariableScope before the row schema, so
        // procedural-batch usage like `SELECT count` reads the declared
        // variable, while non-batch usage reads the row's column.
        QueryExpression q = SqlParser.Parse("SELECT count");
        SelectQueryExpression sqe = Assert.IsType<SelectQueryExpression>(q);
        ColumnReference col = Assert.IsType<ColumnReference>(
            sqe.Statement.Columns[0].Expression);
        Assert.Null(col.TableName);
        Assert.Equal("count", col.ColumnName);
    }

    [Fact]
    public void BareReference_DistinctFromParameterReference()
    {
        // Variables / columns share the ColumnReference AST node; parameters
        // ($-prefix) get their own ParameterExpression node and bind at a
        // separate stage.
        QueryExpression qVar = SqlParser.Parse("SELECT x");
        QueryExpression qParam = SqlParser.Parse("SELECT $x");
        SelectQueryExpression sqVar = Assert.IsType<SelectQueryExpression>(qVar);
        SelectQueryExpression sqParam = Assert.IsType<SelectQueryExpression>(qParam);

        Assert.IsType<ColumnReference>(sqVar.Statement.Columns[0].Expression);
        Assert.IsType<ParameterExpression>(sqParam.Statement.Columns[0].Expression);
    }

    // ───────────────────── DECLARE ─────────────────────

    [Fact]
    public void Declare_TypedNoInitializer_CapturesNameAndType()
    {
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE count INT32");
        Assert.Equal("count", decl.VariableName);
        Assert.Equal("INT32", decl.TypeName, ignoreCase: true);
        Assert.Null(decl.Initializer);
    }

    [Fact]
    public void Declare_TypedWithLiteralInitializer_CapturesBoth()
    {
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE n INT32 = 5");
        Assert.Equal("n", decl.VariableName);
        Assert.Equal("INT32", decl.TypeName, ignoreCase: true);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(decl.Initializer);
        Assert.Equal(5L, Convert.ToInt64(lit.Value!));
    }

    [Fact]
    public void Declare_StringTypeWithStringLiteral_Works()
    {
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE greeting STRING = 'hi'");
        Assert.Equal("greeting", decl.VariableName);
        Assert.Equal("STRING", decl.TypeName, ignoreCase: true);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(decl.Initializer);
        Assert.Equal("hi", lit.Value);
    }

    [Fact]
    public void Declare_AngleBracketArrayType_CanonicalisesToArrayWrapper()
    {
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE players Array<STRING>");
        Assert.Equal("players", decl.VariableName);
        Assert.Equal("Array<STRING>", decl.TypeName);
        Assert.Null(decl.Initializer);
    }

    [Fact]
    public void Declare_PostfixBracketSugar_DesugarsToArrayWrapper()
    {
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE scores FLOAT32[]");
        Assert.Equal("scores", decl.VariableName);
        // Both syntaxes share one canonical form so downstream consumers
        // (the resolver, system_udfs, the catalog file) see one shape.
        Assert.Equal("Array<FLOAT32>", decl.TypeName);
    }

    [Theory]
    [InlineData("offset")]
    [InlineData("OFFSET")]
    [InlineData("limit")]
    [InlineData("where")]
    [InlineData("order")]
    [InlineData("group")]
    [InlineData("having")]
    [InlineData("desc")]
    [InlineData("asc")]
    public void Declare_ReservedKeywordAsName_ProducesTargetedError(string reserved)
    {
        // Without the targeted check, the failure backtracks out of DECLARE
        // silently and surfaces at a far-downstream block boundary (typically
        // the next nested WHILE/IF), with a "unexpected X, expected end"
        // message that doesn't point at the actual DECLARE line.
        ParseException ex = Assert.Throws<ParseException>(() =>
            SqlParser.ParseStatement($"DECLARE {reserved} INT32 = 1"));

        Assert.Contains("reserved keyword", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(reserved, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DECLARE", ex.Message);
        // Position should point at the offending token, not a downstream block boundary.
        Assert.Equal(9, ex.ErrorPosition.Column);
    }

    [Fact]
    public void Declare_DoubleQuotedReservedWord_Works()
    {
        // The targeted error suggests double-quoting as an escape hatch;
        // verify it actually parses.
        DeclareStatement decl = Parse<DeclareStatement>("DECLARE \"offset\" INT32 = 1");
        Assert.Equal("offset", decl.VariableName);
    }

    // ───────────────────── SET ─────────────────────

    [Fact]
    public void Set_LiteralAssignment_CapturesNameAndValue()
    {
        SetStatement set = Parse<SetStatement>("SET x = 42");
        Assert.Equal("x", set.VariableName);
        LiteralExpression lit = Assert.IsType<LiteralExpression>(set.Value);
        Assert.Equal(42L, Convert.ToInt64(lit.Value!));
    }

    [Fact]
    public void Set_ExpressionAssignment_BinaryExpressionPreserved()
    {
        // Confirms the RHS is parsed as a full expression, not just an atom.
        SetStatement set = Parse<SetStatement>("SET x = x + 1");
        Assert.Equal("x", set.VariableName);
        BinaryExpression bin = Assert.IsType<BinaryExpression>(set.Value);
        ColumnReference lhs = Assert.IsType<ColumnReference>(bin.Left);
        Assert.Equal("x", lhs.ColumnName);
    }

    // ───────────────────── BEGIN/END block ─────────────────────

    [Fact]
    public void Block_SingleStatement_ProducesOneElementBlock()
    {
        BlockStatement block = Parse<BlockStatement>("BEGIN SELECT 1 END");
        Assert.Single(block.Statements);
        Assert.IsType<QueryStatement>(block.Statements[0]);
    }

    [Fact]
    public void Block_MultipleStatements_PreservesOrder()
    {
        BlockStatement block = Parse<BlockStatement>(
            "BEGIN DECLARE x INT32 = 1; SET x = 2; SELECT x END");
        Assert.Equal(3, block.Statements.Count);
        Assert.IsType<DeclareStatement>(block.Statements[0]);
        Assert.IsType<SetStatement>(block.Statements[1]);
        Assert.IsType<QueryStatement>(block.Statements[2]);
    }

    [Fact]
    public void Block_TrailingSemicolonBeforeEnd_Tolerated()
    {
        BlockStatement block = Parse<BlockStatement>("BEGIN SELECT 1; END");
        Assert.Single(block.Statements);
    }

    [Fact]
    public void Block_NestedBlock_RecursesCleanly()
    {
        BlockStatement outer = Parse<BlockStatement>("BEGIN BEGIN SELECT 1 END END");
        Assert.Single(outer.Statements);
        BlockStatement inner = Assert.IsType<BlockStatement>(outer.Statements[0]);
        Assert.Single(inner.Statements);
    }

    // ───────────────────── IF / ELSE ─────────────────────

    [Fact]
    public void If_NoElse_ProducesIfWithNullElse()
    {
        IfStatement ifs = Parse<IfStatement>("IF x > 0 SELECT x");
        Assert.IsType<BinaryExpression>(ifs.Predicate);
        Assert.IsType<QueryStatement>(ifs.Then);
        Assert.Null(ifs.Else);
    }

    [Fact]
    public void If_WithElse_BothBranchesParsed()
    {
        IfStatement ifs = Parse<IfStatement>("IF x > 0 SELECT 'pos' ELSE SELECT 'neg'");
        Assert.NotNull(ifs.Else);
        Assert.IsType<QueryStatement>(ifs.Then);
        Assert.IsType<QueryStatement>(ifs.Else);
    }

    [Fact]
    public void If_ElseIf_ParsesAsNestedIfInElseBranch()
    {
        // ELSE IF is just ELSE followed by an IF statement — no special syntax.
        // The parser produces a recursive IfStatement-in-Else shape.
        IfStatement outer = Parse<IfStatement>(
            "IF x > 0 SELECT 'pos' ELSE IF x < 0 SELECT 'neg' ELSE SELECT 'zero'");

        IfStatement middle = Assert.IsType<IfStatement>(outer.Else);
        Assert.IsType<QueryStatement>(middle.Then);
        Assert.IsType<QueryStatement>(middle.Else);
    }

    [Fact]
    public void If_BlockBody_ParsesBlockAsThenBranch()
    {
        IfStatement ifs = Parse<IfStatement>(
            "IF x > 0 BEGIN SET x = 0; SELECT x END");
        BlockStatement block = Assert.IsType<BlockStatement>(ifs.Then);
        Assert.Equal(2, block.Statements.Count);
    }

    // ───────────────────── WHILE ─────────────────────

    [Fact]
    public void While_PredicateAndBody_Parsed()
    {
        WhileStatement loop = Parse<WhileStatement>(
            "WHILE i < 10 BEGIN SET i = i + 1 END");
        Assert.IsType<BinaryExpression>(loop.Predicate);
        Assert.IsType<BlockStatement>(loop.Body);
    }

    // ───────────────────── FOR (counter / IN) ─────────────────────

    [Fact]
    public void For_CounterForm_ParsesAsForCounterStatement()
    {
        ForCounterStatement loop = Parse<ForCounterStatement>(
            "FOR i = 1 TO 10 SELECT i");
        Assert.Equal("i", loop.VariableName);
        LiteralExpression startLit = Assert.IsType<LiteralExpression>(loop.Start);
        LiteralExpression endLit = Assert.IsType<LiteralExpression>(loop.End);
        Assert.Equal(1L, Convert.ToInt64(startLit.Value!));
        Assert.Equal(10L, Convert.ToInt64(endLit.Value!));
        Assert.Null(loop.Step);
    }

    [Fact]
    public void For_InForm_ParsesAsForInStatement()
    {
        ForInStatement loop = Parse<ForInStatement>(
            "FOR row IN (SELECT * FROM t) SELECT row");
        Assert.Equal("row", loop.VariableName);
        Assert.IsType<SelectQueryExpression>(loop.Source);
    }

    [Fact]
    public void For_DispatchesToCorrectVariantByLookahead()
    {
        // Same FOR var prefix; the next token decides. This exercises the
        // .Try() backtrack between ForCounterStatementParser and
        // ForInStatementParser.
        Statement counter = SqlParser.ParseStatement("FOR i = 1 TO 5 SELECT i");
        Statement cursor = SqlParser.ParseStatement("FOR i IN (SELECT 1) SELECT i");

        Assert.IsType<ForCounterStatement>(counter);
        Assert.IsType<ForInStatement>(cursor);
    }

    // ───────────────────── Batch + procedural ─────────────────────

    [Fact]
    public void Batch_MixesQueriesAndProcedural()
    {
        // ParseBatch should accept procedural and query statements interleaved,
        // separated by `;`. Confirms BatchParser dispatches via the same
        // SingleStatementParser .Or() chain.
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(
            "DECLARE x INT32 = 1; IF x > 0 SELECT x; SELECT 'done'");

        Assert.Equal(3, stmts.Count);
        Assert.IsType<DeclareStatement>(stmts[0]);
        Assert.IsType<IfStatement>(stmts[1]);
        Assert.IsType<QueryStatement>(stmts[2]);
    }

    [Fact]
    public void Batch_ProceduralBlocksContainingSemicolons_DoNotSplitAtTopLevel()
    {
        // The semicolons inside BEGIN/END are interior to the BlockStatement;
        // the top-level batch sees a single statement, not three.
        IReadOnlyList<Statement> stmts = SqlParser.ParseBatch(
            "BEGIN DECLARE x INT32 = 1; SET x = 2; SELECT x END");
        Assert.Single(stmts);
        BlockStatement block = Assert.IsType<BlockStatement>(stmts[0]);
        Assert.Equal(3, block.Statements.Count);
    }
}
