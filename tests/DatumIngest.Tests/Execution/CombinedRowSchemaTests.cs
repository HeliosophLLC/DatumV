using DatumIngest.Execution.Operators;
using DatumIngest.Model;

namespace DatumIngest.Tests.Execution;

/// <summary>
/// Tests for <see cref="JoinOperator.CombinedRowSchema"/> to verify that
/// unqualified column shortcuts are preserved through join row merging.
/// </summary>
public sealed class CombinedRowSchemaTests
{
    /// <summary>
    /// When only one side has a column, the unqualified name should resolve in the
    /// combined row so that expressions like <c>image_to_tensor_chw(image)</c> work
    /// after a JOIN without requiring explicit table qualification.
    /// </summary>
    [Fact]
    public void Build_UnambiguousColumns_AddUnqualifiedShortcuts()
    {
        Row left = new(
            ["left_alias.index", "left_alias.label"],
            [DataValue.FromScalar(0), DataValue.FromScalar(5)]);

        Row right = new(
            ["right_alias.index", "right_alias.image"],
            [DataValue.FromScalar(0), DataValue.FromScalar(42)]);

        JoinOperator.CombinedRowSchema schema =
            JoinOperator.CombinedRowSchema.Build(left, right);
        Row combined = schema.Combine(left, right);

        // Unique columns should be reachable by unqualified name.
        Assert.True(combined.TryGetValue("label", out DataValue? label));
        Assert.Equal(5.0, label!.AsScalar());

        Assert.True(combined.TryGetValue("image", out DataValue? image));
        Assert.Equal(42.0, image!.AsScalar());

        // Qualified names should still work.
        Assert.True(combined.TryGetValue("left_alias.label", out _));
        Assert.True(combined.TryGetValue("right_alias.image", out _));
    }

    /// <summary>
    /// When both sides have a column with the same unqualified name (e.g. "index"),
    /// the unqualified shortcut must NOT be added because it is ambiguous.
    /// </summary>
    [Fact]
    public void Build_AmbiguousColumns_NoUnqualifiedShortcut()
    {
        Row left = new(
            ["left_alias.index", "left_alias.label"],
            [DataValue.FromScalar(0), DataValue.FromScalar(5)]);

        Row right = new(
            ["right_alias.index", "right_alias.image"],
            [DataValue.FromScalar(0), DataValue.FromScalar(42)]);

        JoinOperator.CombinedRowSchema schema =
            JoinOperator.CombinedRowSchema.Build(left, right);
        Row combined = schema.Combine(left, right);

        // "index" exists on both sides — unqualified lookup should fail.
        Assert.False(combined.TryGetValue("index", out _));

        // But qualified lookups should succeed.
        Assert.True(combined.TryGetValue("left_alias.index", out _));
        Assert.True(combined.TryGetValue("right_alias.index", out _));
    }

    /// <summary>
    /// When columns have no alias dot-prefix, they are not aliased and no
    /// unqualified shortcut is needed (name is already unqualified).
    /// </summary>
    [Fact]
    public void Build_UnaliasedColumns_PreservesOriginalNames()
    {
        Row left = new(["id", "name"], [DataValue.FromScalar(1), DataValue.FromScalar(2)]);
        Row right = new(["value"], [DataValue.FromScalar(3)]);

        JoinOperator.CombinedRowSchema schema =
            JoinOperator.CombinedRowSchema.Build(left, right);
        Row combined = schema.Combine(left, right);

        Assert.True(combined.TryGetValue("id", out _));
        Assert.True(combined.TryGetValue("name", out _));
        Assert.True(combined.TryGetValue("value", out _));
    }
}
