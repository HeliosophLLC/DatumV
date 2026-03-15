using DatumIngest.Model;

namespace DatumIngest.Tests.Model;

/// <summary>
/// Behavioural tests for <see cref="TypeAnnotationResolver"/>: bare scalar
/// names, the <c>Array&lt;T&gt;</c> wrapper produced by the parser (from
/// either source form), and rejection of unknown names and nested arrays.
/// </summary>
public class TypeAnnotationResolverTests
{
    [Theory]
    [InlineData("Int32", DataKind.Int32)]
    [InlineData("int32", DataKind.Int32)]
    [InlineData("STRING", DataKind.String)]
    [InlineData("Float64", DataKind.Float64)]
    [InlineData("Boolean", DataKind.Boolean)]
    [InlineData("bool", DataKind.Boolean)]   // alias
    [InlineData("scalar", DataKind.Float32)] // alias
    public void TryParse_BareScalar_ReturnsKindWithoutArrayFlag(string annotation, DataKind expected)
    {
        Assert.True(TypeAnnotationResolver.TryParse(annotation, out DataKind kind, out bool isArray));
        Assert.Equal(expected, kind);
        Assert.False(isArray);
    }

    [Theory]
    [InlineData("Array<String>", DataKind.String)]
    [InlineData("Array<Int32>", DataKind.Int32)]
    [InlineData("array<float64>", DataKind.Float64)]
    [InlineData("Array< STRING >", DataKind.String)] // whitespace inside angle brackets is tolerated
    public void TryParse_ArrayWrapper_ReturnsElementKindWithArrayFlag(string annotation, DataKind expected)
    {
        Assert.True(TypeAnnotationResolver.TryParse(annotation, out DataKind kind, out bool isArray));
        Assert.Equal(expected, kind);
        Assert.True(isArray);
    }

    [Theory]
    [InlineData("Array<Array<Int32>>")]
    [InlineData("Array<Array<String>>")]
    public void TryParse_NestedArray_IsRejected(string annotation)
    {
        Assert.False(TypeAnnotationResolver.TryParse(annotation, out _, out _));
    }

    [Theory]
    [InlineData("NotARealKind")]
    [InlineData("Array<NotARealKind>")]
    [InlineData("")]
    [InlineData("Array<>")]
    [InlineData("Array<")]
    [InlineData("Foo<Int32>")]   // unknown wrapper isn't Array
    public void TryParse_UnknownAnnotation_ReturnsFalse(string annotation)
    {
        Assert.False(TypeAnnotationResolver.TryParse(annotation, out _, out _));
    }

    // ───────────────── PG string aliases (VARCHAR / TEXT) ─────────────────

    [Theory]
    [InlineData("VARCHAR")]
    [InlineData("varchar")]
    [InlineData("TEXT")]
    [InlineData("text")]
    public void TryParse_PgStringAlias_ResolvesToString(string annotation)
    {
        Assert.True(TypeAnnotationResolver.TryParse(annotation, out DataKind kind, out bool isArray));
        Assert.Equal(DataKind.String, kind);
        Assert.False(isArray);
    }

    // ───────────────── MaxLength suffix on String / aliases ─────────────────

    [Theory]
    [InlineData("VARCHAR(10)", 10)]
    [InlineData("varchar(255)", 255)]
    [InlineData("String(64)", 64)]
    [InlineData("string(1)", 1)]
    public void TryParse_StringWithMaxLength_ExtractsLength(string annotation, int expectedMaxLength)
    {
        Assert.True(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out DataKind kind, out bool isArray, out _, out int? maxLength, out int[]? fixedShape));
        Assert.Equal(DataKind.String, kind);
        Assert.False(isArray);
        Assert.Equal(expectedMaxLength, maxLength);
        Assert.Null(fixedShape);
    }

    [Theory]
    [InlineData("VARCHAR")]
    [InlineData("TEXT")]
    [InlineData("String")]
    public void TryParse_StringWithoutWidth_LeavesMaxLengthNull(string annotation)
    {
        Assert.True(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out _, out _, out _, out int? maxLength, out _));
        Assert.Null(maxLength);
    }

    // ───────────────── FixedShape on arrays ─────────────────

    // The resolver consumes canonical strings (`Array<T>` / `Array<T>(N)`); the
    // bracket sugars `T[]` / `T[N]` are canonicalised by the SQL parser before
    // they ever reach here. End-to-end bracket coverage lives in
    // CreateTableTests.
    [Theory]
    [InlineData("Array<Float32>(384)", DataKind.Float32, new[] { 384 })]
    [InlineData("Array<Int8>(256)", DataKind.Int8, new[] { 256 })]
    [InlineData("Array<Int32>(1024)", DataKind.Int32, new[] { 1024 })]
    public void TryParse_FixedLengthArray_ExtractsShape(string annotation, DataKind expectedKind, int[] expectedShape)
    {
        Assert.True(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out DataKind kind, out bool isArray, out _, out _, out int[]? fixedShape));
        Assert.Equal(expectedKind, kind);
        Assert.True(isArray);
        Assert.NotNull(fixedShape);
        Assert.Equal(expectedShape, fixedShape);
    }

    [Theory]
    [InlineData("Array<Float32>(3,3)", DataKind.Float32, new[] { 3, 3 })]
    [InlineData("Array<Float32>(2, 128, 128)", DataKind.Float32, new[] { 2, 128, 128 })]
    public void TryParse_MultiDimArray_ExtractsShape(string annotation, DataKind expectedKind, int[] expectedShape)
    {
        Assert.True(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out DataKind kind, out bool isArray, out _, out _, out int[]? fixedShape));
        Assert.Equal(expectedKind, kind);
        Assert.True(isArray);
        Assert.NotNull(fixedShape);
        Assert.Equal(expectedShape, fixedShape);
    }

    [Theory]
    [InlineData("Array<Float32>")]
    public void TryParse_VariableLengthArray_LeavesFixedShapeNull(string annotation)
    {
        Assert.True(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out _, out bool isArray, out _, out _, out int[]? fixedShape));
        Assert.True(isArray);
        Assert.Null(fixedShape);
    }

    // ───────────────── Suffix rejections ─────────────────

    [Theory]
    [InlineData("Int32(10)")]        // paren on non-String scalar
    [InlineData("Float32(10)")]
    [InlineData("String(10, 20)")]    // multi-arg on a scalar
    [InlineData("VARCHAR(0)")]         // zero width
    [InlineData("Array<Float32>(0)")]  // zero dim
    [InlineData("Array<Float32>(0,3)")]
    public void TryParse_InvalidSuffix_ReturnsFalse(string annotation)
    {
        Assert.False(TypeAnnotationResolver.TryParse(
            annotation, types: null,
            out _, out _, out _, out _, out _));
    }

    // ───────────────── Back-compat: 2-arg and 5-arg overloads still work ─────────────────

    [Fact]
    public void TryParse_LegacyOverload_StillAcceptsBareScalar()
    {
        Assert.True(TypeAnnotationResolver.TryParse("String", out DataKind kind, out bool isArray));
        Assert.Equal(DataKind.String, kind);
        Assert.False(isArray);
    }

    [Fact]
    public void TryParse_RegistryOverload_StillAcceptsBareScalar()
    {
        Assert.True(TypeAnnotationResolver.TryParse("String", types: null, out DataKind kind, out bool isArray, out int typeId));
        Assert.Equal(DataKind.String, kind);
        Assert.False(isArray);
        Assert.Equal(TypeRegistry.NoType, typeId);
    }
}
