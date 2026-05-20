using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Tests.Parsing;

/// <summary>
/// Tests covering the parser's handling of PG-style named function-call
/// arguments — <c>fn(name := value)</c> and <c>fn(name =&gt; value)</c>.
/// The parser's job is to capture the optional per-slot parameter name
/// into <see cref="FunctionCallExpression.ArgumentNames"/>; permutation
/// against signatures happens in the planner pass.
/// </summary>
public sealed class NamedArgumentParsingTests : ServiceTestBase
{
    private static FunctionCallExpression ParseCall(string sql)
    {
        SelectStatement stmt = ((SelectQueryExpression)SqlParser.Parse(sql)).Statement;
        return Assert.IsType<FunctionCallExpression>(stmt.Columns[0].Expression);
    }

    [Fact]
    public void Parse_PositionalOnly_LeavesArgumentNamesNull()
    {
        FunctionCallExpression call = ParseCall("SELECT abs(x) FROM t");

        Assert.Null(call.ArgumentNames);
        Assert.False(call.HasNamedArguments);
        Assert.Single(call.Arguments);
    }

    [Fact]
    public void Parse_AllNamedWithColonEquals_CapturesNames()
    {
        FunctionCallExpression call = ParseCall("SELECT foo(a := 1, b := 2) FROM t");

        Assert.True(call.HasNamedArguments);
        Assert.NotNull(call.ArgumentNames);
        Assert.Equal(2, call.ArgumentNames.Count);
        Assert.Equal("a", call.ArgumentNames[0]);
        Assert.Equal("b", call.ArgumentNames[1]);
        Assert.Equal(2, call.Arguments.Count);
    }

    [Fact]
    public void Parse_AllNamedWithFatArrow_CapturesNames()
    {
        FunctionCallExpression call = ParseCall("SELECT foo(a => 1, b => 2) FROM t");

        Assert.True(call.HasNamedArguments);
        Assert.NotNull(call.ArgumentNames);
        Assert.Equal("a", call.ArgumentNames[0]);
        Assert.Equal("b", call.ArgumentNames[1]);
    }

    [Fact]
    public void Parse_MixedColonEqualsAndFatArrow_Allowed()
    {
        FunctionCallExpression call = ParseCall("SELECT foo(a := 1, b => 2) FROM t");

        Assert.NotNull(call.ArgumentNames);
        Assert.Equal("a", call.ArgumentNames[0]);
        Assert.Equal("b", call.ArgumentNames[1]);
    }

    [Fact]
    public void Parse_PositionalThenNamed_CapturesNullForPositionalSlots()
    {
        FunctionCallExpression call = ParseCall("SELECT foo(42, b := 'msg') FROM t");

        Assert.True(call.HasNamedArguments);
        Assert.NotNull(call.ArgumentNames);
        Assert.Equal(2, call.ArgumentNames.Count);
        Assert.Null(call.ArgumentNames[0]);
        Assert.Equal("b", call.ArgumentNames[1]);
    }

    [Fact]
    public void Parse_NamedThenPositional_StillParsesAndDefersRejectionToPlanner()
    {
        // The parser allows the syntactic shape; the NamedArgPermuter
        // planner pass surfaces the diagnostic with a precise message.
        // This test pins the parse-time contract: we capture the slots
        // as-is and let downstream choose how to reject them.
        FunctionCallExpression call = ParseCall("SELECT foo(b := 1, 42) FROM t");

        Assert.NotNull(call.ArgumentNames);
        Assert.Equal("b", call.ArgumentNames[0]);
        Assert.Null(call.ArgumentNames[1]);
    }

    [Fact]
    public void Parse_BareIdentifierStillParsesAsColumnReference()
    {
        // Without the := / => operator the identifier resolves through
        // the expression grammar — the named-arg .Try() must backtrack
        // cleanly so a plain column reference still works as the first
        // argument of a positional call.
        FunctionCallExpression call = ParseCall("SELECT abs(x) FROM t");

        Assert.Null(call.ArgumentNames);
        Assert.IsType<ColumnReference>(call.Arguments[0]);
    }

    [Fact]
    public void Parse_NamedArgValueIsArbitraryExpression()
    {
        FunctionCallExpression call = ParseCall("SELECT foo(message := upper('hi')) FROM t");

        Assert.NotNull(call.ArgumentNames);
        Assert.Equal("message", call.ArgumentNames[0]);
        FunctionCallExpression inner = Assert.IsType<FunctionCallExpression>(call.Arguments[0]);
        Assert.Equal("upper", inner.FunctionName);
    }

    /// <summary>
    /// Pins the soft-keyword audit (2026-05-26): every keyword token
    /// that's used as a parameter name on a built-in scalar / TVF must
    /// parse on the named-argument side. New collisions discovered by
    /// future param renames should add the corresponding
    /// <see cref="Heliosoph.DatumV.Parsing.Tokens.SqlToken"/> to
    /// <c>IdentifierOrKeywordAsName</c> and extend this theory.
    /// </summary>
    [Theory]
    [InlineData("at")]        // SqlToken.At — Drawing.draw_image/draw_text/draw_circle slot
    [InlineData("end")]       // SqlToken.End — Shape.draw_line / range TVF terminator slot
    [InlineData("key")]       // SqlToken.Key — already accepted, pinned for regression
    [InlineData("message")]   // SqlToken.Message — assert_* trailing slot
    [InlineData("step")]      // SqlToken.Step — UDF / model parameter constraint keyword
    [InlineData("values")]    // SqlToken.Values — VariadicSpec slot
    [InlineData("duration")]  // SqlToken.TypeKeyword
    [InlineData("image")]     // SqlToken.TypeKeyword
    public void Parse_SoftKeywordParameterName_ParsesAsNamedArg(string name)
    {
        FunctionCallExpression call = ParseCall(
            $"SELECT foo({name} := 1) FROM t");

        Assert.NotNull(call.ArgumentNames);
        Assert.Single(call.ArgumentNames);
        Assert.Equal(name, call.ArgumentNames[0], ignoreCase: true);
    }

    [Fact]
    public void Parse_NamedArgOnWindowFunction_ThrowsWithSpecificMessage()
    {
        // Window functions don't propagate parameter-name metadata to
        // the planner, so the parser surfaces a precise diagnostic
        // rather than silently dropping the names and letting a
        // downstream arity check fail confusingly.
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() =>
            SqlParser.Parse("SELECT lag(x, n := 3) OVER () FROM t"));

        Assert.Contains("window function", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("lag", ex.Message);
    }
}
