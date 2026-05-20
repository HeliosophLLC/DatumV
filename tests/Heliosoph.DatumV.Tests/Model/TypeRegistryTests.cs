using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

public class TypeRegistryTests
{
    // ── structural interning ───────────────────────────────────────────────

    [Fact]
    public void SameStructShape_ReturnsSameTypeId()
    {
        var reg = new TypeRegistry();
        var fields = new[] { new StructFieldDescriptor("label", TypeRegistry.NoType) };

        int id1 = reg.InternStructType(fields);
        int id2 = reg.InternStructType(fields);

        Assert.Equal(id1, id2);
        Assert.NotEqual(TypeRegistry.NoType, id1);
    }

    [Fact]
    public void DifferentStructShapes_ReturnDifferentTypeIds()
    {
        var reg = new TypeRegistry();

        int id1 = reg.InternStructType([new("label", TypeRegistry.NoType)]);
        int id2 = reg.InternStructType([new("score", TypeRegistry.NoType)]);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void StructShapes_DifferByFieldCount_ReturnDifferentTypeIds()
    {
        var reg = new TypeRegistry();

        int id1 = reg.InternStructType([new("a", TypeRegistry.NoType)]);
        int id2 = reg.InternStructType([new("a", TypeRegistry.NoType), new("b", TypeRegistry.NoType)]);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SameScalarKind_ReturnsSameTypeId()
    {
        var reg = new TypeRegistry();

        int id1 = reg.InternScalarType(DataKind.Float32);
        int id2 = reg.InternScalarType(DataKind.Float32);

        Assert.Equal(id1, id2);
    }

    [Fact]
    public void DifferentScalarKinds_ReturnDifferentTypeIds()
    {
        var reg = new TypeRegistry();

        int id1 = reg.InternScalarType(DataKind.Float32);
        int id2 = reg.InternScalarType(DataKind.Int32);

        Assert.NotEqual(id1, id2);
    }

    [Fact]
    public void SameArrayType_ReturnsSameTypeId()
    {
        var reg = new TypeRegistry();

        int id1 = reg.InternArrayType(DataKind.Float32);
        int id2 = reg.InternArrayType(DataKind.Float32);

        Assert.Equal(id1, id2);
    }

    // ── nested struct interning ────────────────────────────────────────────

    [Fact]
    public void NestedStruct_InternedRecursively()
    {
        var reg = new TypeRegistry();

        int innerTypeId = reg.InternStructType([new("x", TypeRegistry.NoType), new("y", TypeRegistry.NoType)]);
        int outerTypeId = reg.InternStructType([new("point", innerTypeId)]);

        Assert.NotEqual(TypeRegistry.NoType, innerTypeId);
        Assert.NotEqual(TypeRegistry.NoType, outerTypeId);
        Assert.NotEqual(innerTypeId, outerTypeId);
    }

    [Fact]
    public void NestedStruct_SameShapeReturnsSameId()
    {
        var reg = new TypeRegistry();

        int inner1 = reg.InternStructType([new("x", TypeRegistry.NoType)]);
        int inner2 = reg.InternStructType([new("x", TypeRegistry.NoType)]);
        int outer1 = reg.InternStructType([new("pt", inner1)]);
        int outer2 = reg.InternStructType([new("pt", inner2)]);

        Assert.Equal(inner1, inner2);
        Assert.Equal(outer1, outer2);
    }

    // ── GetDescriptor ──────────────────────────────────────────────────────

    [Fact]
    public void GetDescriptor_NoType_ReturnsNull()
    {
        var reg = new TypeRegistry();
        Assert.Null(reg.GetDescriptor(TypeRegistry.NoType));
    }

    [Fact]
    public void GetDescriptor_RoundTrips_StructShape()
    {
        var reg = new TypeRegistry();
        var fields = new[]
        {
            new StructFieldDescriptor("label", TypeRegistry.NoType),
            new StructFieldDescriptor("score", TypeRegistry.NoType),
        };

        int id = reg.InternStructType(fields, nullable: true);
        TypeDescriptor? desc = reg.GetDescriptor(id);

        Assert.NotNull(desc);
        Assert.Equal(DataKind.Struct, desc.Kind);
        Assert.False(desc.IsArray);
        Assert.True(desc.Nullable);
        Assert.Equal(2, desc.Fields!.Count);
        Assert.Equal("label", desc.Fields[0].Name);
        Assert.Equal("score", desc.Fields[1].Name);
    }

    [Fact]
    public void GetDescriptor_RoundTrips_ScalarShape()
    {
        var reg = new TypeRegistry();
        int id = reg.InternScalarType(DataKind.Int64);
        TypeDescriptor? desc = reg.GetDescriptor(id);

        Assert.NotNull(desc);
        Assert.Equal(DataKind.Int64, desc.Kind);
        Assert.False(desc.IsArray);
        Assert.Null(desc.Fields);
    }

    [Fact]
    public void GetDescriptor_UnregisteredId_Throws()
    {
        var reg = new TypeRegistry();
        Assert.Throws<ArgumentOutOfRangeException>(() => reg.GetDescriptor(999));
    }

    // ── FindFieldIndex ─────────────────────────────────────────────────────

    [Fact]
    public void FindFieldIndex_CaseInsensitive()
    {
        var reg = new TypeRegistry();
        int id = reg.InternStructType([new("Label", TypeRegistry.NoType), new("Score", TypeRegistry.NoType)]);
        TypeDescriptor desc = reg.GetDescriptor(id)!;

        Assert.Equal(0, desc.FindFieldIndex("label"));
        Assert.Equal(0, desc.FindFieldIndex("LABEL"));
        Assert.Equal(1, desc.FindFieldIndex("score"));
        Assert.Equal(-1, desc.FindFieldIndex("missing"));
    }

    [Fact]
    public void FindFieldIndex_OnNonStruct_ReturnsMinusOne()
    {
        var reg = new TypeRegistry();
        int id = reg.InternScalarType(DataKind.Float32);
        TypeDescriptor desc = reg.GetDescriptor(id)!;

        Assert.Equal(-1, desc.FindFieldIndex("anything"));
    }

    // ── ColumnInfo convenience ─────────────────────────────────────────────

    [Fact]
    public void InternStructFromColumnInfoFields_BuildsCorrectDescriptor()
    {
        var reg = new TypeRegistry();
        var fields = new[]
        {
            new ColumnInfo("label", DataKind.String, nullable: false),
            new ColumnInfo("score", DataKind.Float32, nullable: false),
        };

        int id = reg.InternStructFromColumnInfoFields(fields);
        TypeDescriptor? desc = reg.GetDescriptor(id);

        Assert.NotNull(desc);
        Assert.Equal(DataKind.Struct, desc.Kind);
        Assert.Equal(2, desc.Fields!.Count);
        Assert.Equal("label", desc.Fields[0].Name);
        Assert.Equal("score", desc.Fields[1].Name);
    }

    [Fact]
    public void InternStructFromColumnInfoFields_IsDeterministic()
    {
        var reg = new TypeRegistry();
        var fields = new[]
        {
            new ColumnInfo("x", DataKind.Int32, nullable: false),
        };

        int id1 = reg.InternStructFromColumnInfoFields(fields);
        int id2 = reg.InternStructFromColumnInfoFields(fields);

        Assert.Equal(id1, id2);
    }

    // ── Count ─────────────────────────────────────────────────────────────

    [Fact]
    public void Count_ReflectsRegisteredShapes()
    {
        var reg = new TypeRegistry();
        // The constructor pre-interns NamedTypeRegistry.Entries, so a fresh
        // registry isn't empty. Capture the baseline and assert deltas.
        int baseline = reg.Count;
        Assert.True(baseline >= NamedTypeRegistry.Entries.Count,
            $"baseline {baseline} must include the {NamedTypeRegistry.Entries.Count} named-type vocabulary entries");

        // Int32 is a primitive that named-type recipes already intern via
        // InternScalarType(Int32) — re-interning it is a no-op. Use a kind
        // that none of the named types reference so the count actually
        // moves: Decimal isn't in any vocabulary entry.
        reg.InternScalarType(DataKind.Decimal);
        Assert.Equal(baseline + 1, reg.Count);

        // Same shape again — no new registration.
        reg.InternScalarType(DataKind.Decimal);
        Assert.Equal(baseline + 1, reg.Count);

        // Different shape — count moves again.
        reg.InternStructType([new("only_field", reg.InternScalarType(DataKind.Boolean))]);
        Assert.Equal(baseline + 2, reg.Count);
    }

}
