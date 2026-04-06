namespace DatumIngest.Tests.Manifest;

using DatumIngest.Manifest;

/// <summary>
/// Tests for <see cref="StructTypeAnnotation"/> — the canonical
/// <c>Struct&lt;name: Kind, …&gt;</c> string parser shared by the catalog
/// manifest builder and the LanguageServer. The engine produces these
/// strings from a model's <c>RETURNS Struct&lt;…&gt;</c> annotation; the
/// LS reads them back to resolve field accesses at hover / completion
/// time.
/// </summary>
public sealed class StructTypeAnnotationTests : ServiceTestBase
{
    [Fact]
    public void TryParse_FlatStruct_ReturnsFields()
    {
        Assert.True(StructTypeAnnotation.TryParse(
            "Struct<depth: Array<Float32>, intrinsics: Array<Float32>>",
            out IReadOnlyList<StructFieldShape> fields));

        Assert.Equal(2, fields.Count);
        Assert.Equal("depth", fields[0].Name);
        Assert.Equal("Array<Float32>", fields[0].Kind);
        Assert.Equal("intrinsics", fields[1].Name);
        Assert.Equal("Array<Float32>", fields[1].Kind);
    }

    [Fact]
    public void TryParse_BareStruct_ReturnsFalse()
    {
        // Opaque `Struct` (no field list) — the LS treats it as opaque and
        // shouldn't pretend to know field shapes.
        Assert.False(StructTypeAnnotation.TryParse(
            "Struct", out IReadOnlyList<StructFieldShape> fields));
        Assert.Empty(fields);
    }

    [Fact]
    public void TryParse_NestedStruct_SplitsAtTopLevelComma()
    {
        // The comma inside the nested `Struct<x: Int32, y: Int32>` lives at
        // angle-bracket depth 1 and must not split the outer field list.
        Assert.True(StructTypeAnnotation.TryParse(
            "Struct<point: Struct<x: Int32, y: Int32>, label: String>",
            out IReadOnlyList<StructFieldShape> fields));

        Assert.Equal(2, fields.Count);
        Assert.Equal("point", fields[0].Name);
        Assert.Equal("Struct<x: Int32, y: Int32>", fields[0].Kind);
        Assert.Equal("label", fields[1].Name);
        Assert.Equal("String", fields[1].Kind);
    }

    [Fact]
    public void TryParse_NestedStructInField_HandlesInnerColon()
    {
        // Nested struct's inner colons (between field name and field kind)
        // sit at angle-bracket depth > 0 and must not be picked up as the
        // outer name/kind boundary.
        Assert.True(StructTypeAnnotation.TryParse(
            "Struct<wrapped: Struct<inner: Float32>>",
            out IReadOnlyList<StructFieldShape> fields));

        Assert.Single(fields);
        Assert.Equal("wrapped", fields[0].Name);
        Assert.Equal("Struct<inner: Float32>", fields[0].Kind);
    }

    [Fact]
    public void TryParse_FieldsWithDimSuffix_DoesNotSplitOnDimComma()
    {
        // `Array<Float32>(518, 518)` carries a comma between dim args at
        // angle-bracket depth zero. Without paren-depth tracking the
        // splitter would shred the field list on the inner comma.
        Assert.True(StructTypeAnnotation.TryParse(
            "Struct<depth: Array<Float32>(518,518), intrinsics: Array<Float32>(1,1,3,4)>",
            out IReadOnlyList<StructFieldShape> fields));

        Assert.Equal(2, fields.Count);
        Assert.Equal("depth", fields[0].Name);
        Assert.Equal("Array<Float32>(518,518)", fields[0].Kind);
        Assert.Equal("intrinsics", fields[1].Name);
        Assert.Equal("Array<Float32>(1,1,3,4)", fields[1].Kind);
    }

    [Fact]
    public void TryParse_MalformedInput_ReturnsFalse()
    {
        Assert.False(StructTypeAnnotation.TryParse(null, out _));
        Assert.False(StructTypeAnnotation.TryParse("Struct<>", out _));
        Assert.False(StructTypeAnnotation.TryParse("NotStruct<a: Int32>", out _));
        Assert.False(StructTypeAnnotation.TryParse("Array<Float32>", out _));
        // Missing colon between name and type — analyzer-style malformed
        // input that the manifest builder shouldn't be emitting but we
        // shouldn't crash on either.
        Assert.False(StructTypeAnnotation.TryParse("Struct<just_a_name>", out _));
    }

    [Fact]
    public void Format_RoundTripsThroughTryParse()
    {
        StructFieldShape[] input =
        [
            new("depth", "Array<Float32>"),
            new("intrinsics", "Array<Float32>"),
            new("frame_index", "Int32"),
        ];

        string formatted = StructTypeAnnotation.Format(input);

        Assert.Equal(
            "Struct<depth: Array<Float32>, intrinsics: Array<Float32>, frame_index: Int32>",
            formatted);

        Assert.True(StructTypeAnnotation.TryParse(formatted, out IReadOnlyList<StructFieldShape> roundTrip));
        Assert.Equal(input.Length, roundTrip.Count);
        for (int i = 0; i < input.Length; i++)
        {
            Assert.Equal(input[i].Name, roundTrip[i].Name);
            Assert.Equal(input[i].Kind, roundTrip[i].Kind);
        }
    }

    [Fact]
    public void Format_EmptyFieldList_ReturnsBareStruct()
    {
        // An empty field list has no useful shape to render; fall back to
        // the bare `Struct` opaque form so the LS can short-circuit.
        Assert.Equal("Struct", StructTypeAnnotation.Format(System.Array.Empty<StructFieldShape>()));
    }
}
