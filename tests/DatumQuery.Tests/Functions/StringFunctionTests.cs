using Axon.QueryEngine.Functions.Scalar;
using Axon.QueryEngine.Model;

namespace Axon.QueryEngine.Tests.Functions;

public class StringFunctionTests
{
    // ───────────────── LenFunction ─────────────────

    [Fact]
    public void Len_String_ReturnsLength()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("hello")]);
        Assert.Equal(5f, result.AsScalar());
    }

    [Fact]
    public void Len_EmptyString_ReturnsZero()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("")]);
        Assert.Equal(0f, result.AsScalar());
    }

    [Fact]
    public void Len_Vector_ReturnsElementCount()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromVector([1, 2, 3])]);
        Assert.Equal(3f, result.AsScalar());
    }

    [Fact]
    public void Len_UInt8Array_ReturnsByteCount()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromUInt8Array([10, 20])]);
        Assert.Equal(2f, result.AsScalar());
    }

    [Fact]
    public void Len_Matrix_ReturnsTotalElements()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromMatrix([1, 2, 3, 4, 5, 6], 2, 3)]);
        Assert.Equal(6f, result.AsScalar());
    }

    [Fact]
    public void Len_Tensor_ReturnsTotalElements()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.FromTensor([1, 2, 3, 4, 5, 6, 7, 8], [2, 2, 2])]);
        Assert.Equal(8f, result.AsScalar());
    }

    [Fact]
    public void Len_NullInput_ReturnsNull()
    {
        LenFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Len_UnsupportedKind_Throws()
    {
        LenFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.Scalar]));
    }

    [Fact]
    public void Len_WrongArgCount_Throws()
    {
        LenFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.String]));
    }

    // ───────────────── MidFunction ─────────────────

    [Fact]
    public void Mid_ExtractsSubstring()
    {
        MidFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromScalar(6),
            DataValue.FromScalar(5)
        ]);
        Assert.Equal("world", result.AsString());
    }

    [Fact]
    public void Mid_StartBeyondEnd_ReturnsEmpty()
    {
        MidFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromScalar(100),
            DataValue.FromScalar(5)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void Mid_LengthExceedsAvailable_ClamsToEnd()
    {
        MidFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromScalar(3),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal("lo", result.AsString());
    }

    [Fact]
    public void Mid_NegativeStart_TreatedAsZero()
    {
        MidFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello"),
            DataValue.FromScalar(-1),
            DataValue.FromScalar(3)
        ]);
        Assert.Equal("hel", result.AsString());
    }

    [Fact]
    public void Mid_NullInput_ReturnsNull()
    {
        MidFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromScalar(0),
            DataValue.FromScalar(3)
        ]);
        Assert.True(result.IsNull);
    }

    [Fact]
    public void Mid_WrongArgCount_Throws()
    {
        MidFunction function = new();
        Assert.Throws<ArgumentException>(() =>
            function.ValidateArguments([DataKind.String, DataKind.Scalar]));
    }

    // ───────────────── SubstringFunction ─────────────────

    [Fact]
    public void Substring_TwoArgs_ExtractsToEnd()
    {
        SubstringFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromScalar(6)
        ]);
        Assert.Equal("world", result.AsString());
    }

    [Fact]
    public void Substring_ThreeArgs_ExtractsWithLength()
    {
        SubstringFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("hello world"),
            DataValue.FromScalar(0),
            DataValue.FromScalar(5)
        ]);
        Assert.Equal("hello", result.AsString());
    }

    [Fact]
    public void Substring_StartBeyondEnd_ReturnsEmpty()
    {
        SubstringFunction function = new();
        DataValue result = function.Execute([
            DataValue.FromString("abc"),
            DataValue.FromScalar(100)
        ]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void Substring_NullInput_ReturnsNull()
    {
        SubstringFunction function = new();
        DataValue result = function.Execute([
            DataValue.Null(DataKind.String),
            DataValue.FromScalar(0)
        ]);
        Assert.True(result.IsNull);
    }

    // ───────────────── GetFilenameFunction ─────────────────

    [Fact]
    public void GetFilename_ExtractsFilename()
    {
        GetFilenameFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("/data/images/photo.jpg")]);
        Assert.Equal("photo.jpg", result.AsString());
    }

    [Fact]
    public void GetFilename_WindowsPath()
    {
        GetFilenameFunction function = new();
        DataValue result = function.Execute([DataValue.FromString(@"C:\data\file.txt")]);
        Assert.Equal("file.txt", result.AsString());
    }

    [Fact]
    public void GetFilename_NullInput_ReturnsNull()
    {
        GetFilenameFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── GetFileExtensionFunction ─────────────────

    [Fact]
    public void GetFileExtension_ExtractsExtension()
    {
        GetFileExtensionFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("photo.jpg")]);
        Assert.Equal(".jpg", result.AsString());
    }

    [Fact]
    public void GetFileExtension_NoExtension_ReturnsEmpty()
    {
        GetFileExtensionFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("README")]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void GetFileExtension_NullInput_ReturnsNull()
    {
        GetFileExtensionFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }

    // ───────────────── GetPathFunction ─────────────────

    [Fact]
    public void GetPath_ExtractsDirectoryPath()
    {
        GetPathFunction function = new();
        DataValue result = function.Execute([DataValue.FromString(@"C:\data\images\photo.jpg")]);
        Assert.Equal(@"C:\data\images", result.AsString());
    }

    [Fact]
    public void GetPath_NoDirectory_ReturnsEmpty()
    {
        GetPathFunction function = new();
        DataValue result = function.Execute([DataValue.FromString("file.txt")]);
        Assert.Equal("", result.AsString());
    }

    [Fact]
    public void GetPath_NullInput_ReturnsNull()
    {
        GetPathFunction function = new();
        DataValue result = function.Execute([DataValue.Null(DataKind.String)]);
        Assert.True(result.IsNull);
    }
}
