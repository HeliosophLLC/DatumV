using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Catalog;

/// <summary>
/// PR2: <see cref="LiteralCoercion.EnforceFixedShape"/> attaches the declared
/// multi-dim shape onto a flat array source when the target column declares
/// <c>FixedShape</c> with <c>ndim ≥ 2</c>. 1-D fixed-shape columns stay flat.
/// </summary>
public sealed class FixedShapeMultiDimAttachTests : ServiceTestBase
{
    private static ColumnInfo Column(string name, DataKind kind, int[]? fixedShape) =>
        new ColumnInfo(name, kind, nullable: true)
        {
            IsArray = true,
            FixedShape = fixedShape,
        };

    [Fact]
    public void MultiDimColumn_AttachesShapeAndFlagToFlatSource()
    {
        Arena arena = CreateArena();
        // Source: flat 4-element Float32 array.
        DataValue source = DataValue.FromArenaArray<float>([1f, 2f, 3f, 4f], DataKind.Float32, arena);
        Assert.False(source.IsMultiDim);

        ColumnInfo target = Column("matrix", DataKind.Float32, fixedShape: [2, 2]);
        DataValue promoted = LiteralCoercion.EnforceFixedShape(source, target, "matrix", arena);

        Assert.True(promoted.IsMultiDim);
        Assert.Equal(2, promoted.Ndim);
        Assert.Equal([2, 2], promoted.GetShape(arena).ToArray());
        Assert.Equal([1f, 2f, 3f, 4f], promoted.AsArraySpan<float>(arena).ToArray());
    }

    [Fact]
    public void OneDimColumn_LeavesValueFlat()
    {
        Arena arena = CreateArena();
        DataValue source = DataValue.FromArenaArray<float>([1f, 2f, 3f, 4f], DataKind.Float32, arena);

        ColumnInfo target = Column("vec", DataKind.Float32, fixedShape: [4]);
        DataValue result = LiteralCoercion.EnforceFixedShape(source, target, "vec", arena);

        Assert.False(result.IsMultiDim);
        Assert.Equal(0, result.Ndim);
    }

    [Fact]
    public void VariableLengthColumn_LeavesValueFlat()
    {
        Arena arena = CreateArena();
        DataValue source = DataValue.FromArenaArray<float>([1f, 2f, 3f], DataKind.Float32, arena);

        ColumnInfo target = Column("vec", DataKind.Float32, fixedShape: null);
        DataValue result = LiteralCoercion.EnforceFixedShape(source, target, "vec", arena);

        Assert.False(result.IsMultiDim);
    }

    [Fact]
    public void NullSource_PassesThrough()
    {
        Arena arena = CreateArena();
        DataValue source = DataValue.Null(DataKind.Float32);

        ColumnInfo target = Column("matrix", DataKind.Float32, fixedShape: [2, 2]);
        DataValue result = LiteralCoercion.EnforceFixedShape(source, target, "matrix", arena);

        Assert.True(result.IsNull);
        Assert.False(result.IsMultiDim);
    }

    [Fact]
    public void MultiDimSource_AlreadyAttached_PassesThrough()
    {
        Arena arena = CreateArena();
        DataValue source = DataValue.FromArenaMultiDimArray<float>(
            [1f, 2f, 3f, 4f], [2, 2], DataKind.Float32, arena);

        ColumnInfo target = Column("matrix", DataKind.Float32, fixedShape: [2, 2]);
        DataValue result = LiteralCoercion.EnforceFixedShape(source, target, "matrix", arena);

        Assert.True(result.IsMultiDim);
        Assert.Equal([2, 2], result.GetShape(arena).ToArray());
    }

    [Fact]
    public void NoArena_SkipsAttachmentEvenForMultiDimColumn()
    {
        // UPDATE fast-path passes arena=null to preserve sidecar identity;
        // validation still runs, but multi-dim attachment is skipped.
        Arena arena = CreateArena();
        DataValue source = DataValue.FromArenaArray<float>([1f, 2f, 3f, 4f], DataKind.Float32, arena);

        ColumnInfo target = Column("matrix", DataKind.Float32, fixedShape: [2, 2]);
        DataValue result = LiteralCoercion.EnforceFixedShape(source, target, "matrix", arena: null);

        Assert.False(result.IsMultiDim);
        // Validation passed — 4 elements matches product([2,2]).
    }

    [Fact]
    public void ElementCount_Mismatch_StillThrows()
    {
        Arena arena = CreateArena();
        DataValue source = DataValue.FromArenaArray<float>([1f, 2f, 3f], DataKind.Float32, arena);

        ColumnInfo target = Column("matrix", DataKind.Float32, fixedShape: [2, 2]);
        Assert.Throws<Heliosoph.DatumV.Execution.ColumnValueConstraintException>(() =>
            LiteralCoercion.EnforceFixedShape(source, target, "matrix", arena));
    }
}
