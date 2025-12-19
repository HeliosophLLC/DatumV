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
}
