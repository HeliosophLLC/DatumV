using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Tests for <see cref="ColumnNameResolver"/> covering raw name derivation
/// from expressions and deduplication of colliding auto-generated names.
/// </summary>
public class ColumnNameResolverTests
{
    // ───────────────────── GetRawName ─────────────────────

    [Fact]
    public void GetRawName_ColumnReference_ReturnsColumnName()
    {
        string name = ColumnNameResolver.GetRawName(new ColumnReference("col1"));
        Assert.Equal("col1", name);
    }

    [Fact]
    public void GetRawName_FunctionCall_ReturnsFunctionName()
    {
        string name = ColumnNameResolver.GetRawName(
            new FunctionCallExpression("resize", [new ColumnReference("image")]));
        Assert.Equal("resize", name);
    }

    [Fact]
    public void GetRawName_BinaryExpression_ReturnsExpression()
    {
        string name = ColumnNameResolver.GetRawName(
            new BinaryExpression(
                new ColumnReference("x"),
                BinaryOperator.Add,
                new ColumnReference("y")));
        Assert.Equal("expression", name);
    }

    [Fact]
    public void GetRawName_Literal_ReturnsExpression()
    {
        string name = ColumnNameResolver.GetRawName(new LiteralExpression(42));
        Assert.Equal("expression", name);
    }

    // ───────────────────── DeduplicateNames — no collisions ─────────────────────

    [Fact]
    public void DeduplicateNames_NoDuplicates_NamesUnchanged()
    {
        string[] names = ["index", "resize", "uuid4"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["index", "resize", "uuid4"], names);
    }

    // ───────────────────── DeduplicateNames — collisions ─────────────────────

    [Fact]
    public void DeduplicateNames_DuplicateFunctionNames_SuffixedWithOrdinals()
    {
        string[] names = ["index", "resize", "resize", "uuid4", "uuid7"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["index", "resize_1", "resize_2", "uuid4", "uuid7"], names);
    }

    [Fact]
    public void DeduplicateNames_ThreeDuplicates_AllSuffixed()
    {
        string[] names = ["resize", "resize", "resize"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["resize_1", "resize_2", "resize_3"], names);
    }

    [Fact]
    public void DeduplicateNames_MultipleCollisionGroups_EachGroupSuffixedIndependently()
    {
        string[] names = ["resize", "uuid4", "resize", "uuid4"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["resize_1", "uuid4_1", "resize_2", "uuid4_2"], names);
    }

    [Fact]
    public void DeduplicateNames_ExpressionFallbacks_Deduplicated()
    {
        string[] names = ["expression", "expression"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["expression_1", "expression_2"], names);
    }

    // ───────────────────── DeduplicateNames — aliased positions ─────────────────────

    [Fact]
    public void DeduplicateNames_AliasedPositionExcludedFromCollision()
    {
        // Positions 0 and 2 both have "resize", but position 0 is aliased.
        // Only position 2 is auto-generated, so no collision — no suffix needed.
        string[] names = ["resize", "uuid4", "resize"];
        HashSet<int> aliased = [0];
        ColumnNameResolver.DeduplicateNames(names, aliased);
        Assert.Equal(["resize", "uuid4", "resize"], names);
    }

    [Fact]
    public void DeduplicateNames_AliasedPositionNeverRenamed()
    {
        // Two auto-generated "resize" plus one aliased "resize".
        // Only the two non-aliased ones collide and get suffixed.
        string[] names = ["resize", "resize", "resize"];
        HashSet<int> aliased = [1];
        ColumnNameResolver.DeduplicateNames(names, aliased);
        Assert.Equal(["resize_1", "resize", "resize_2"], names);
    }

    // ───────────────────── DeduplicateNames — case insensitivity ─────────────────────

    [Fact]
    public void DeduplicateNames_CaseInsensitiveCollision()
    {
        string[] names = ["Resize", "resize"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["Resize_1", "resize_2"], names);
    }

    // ───────────────────── DeduplicateNames — single element ─────────────────────

    [Fact]
    public void DeduplicateNames_SingleName_Unchanged()
    {
        string[] names = ["resize"];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Equal(["resize"], names);
    }

    [Fact]
    public void DeduplicateNames_EmptyArray_NoOp()
    {
        string[] names = [];
        ColumnNameResolver.DeduplicateNames(names);
        Assert.Empty(names);
    }
}
