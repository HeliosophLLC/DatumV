namespace DatumIngest.Tests.Functions.Image;

using DatumIngest.Functions.Image;
using DatumIngest.Model;

using SkiaSharp;

/// <summary>
/// Tests for <see cref="PerceptualHashFunction"/>.
/// </summary>
public sealed class PerceptualHashFunctionTests : ServiceTestBase
{
    // ───────────────── Helpers ─────────────────

    /// <summary>Creates a solid-color PNG image for testing.</summary>
    private static byte[] MakeTestPng(int width, int height, SKColor? color = null)
    {
        using SKBitmap bitmap = new(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        bitmap.Erase(color ?? SKColors.Red);
        using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private readonly PerceptualHashFunction _perceptualHash = new();

    [Fact]
    public void PerceptualHash_Name()
    {
        Assert.Equal("perceptual_hash", _perceptualHash.Name);
    }

    [Fact]
    public void PerceptualHash_Validate_AcceptsImage()
    {
        Assert.Equal(DataKind.Vector, _perceptualHash.ValidateArguments([DataKind.Image]));
    }

    [Fact]
    public void PerceptualHash_Validate_AcceptsUInt8Array()
    {
        Assert.Equal(DataKind.Vector, _perceptualHash.ValidateArguments([DataKind.UInt8Array]));
    }

    [Fact]
    public void PerceptualHash_Validate_WrongArgCount_Throws()
    {
        Assert.Throws<ArgumentException>(() => _perceptualHash.ValidateArguments([]));
        Assert.Throws<ArgumentException>(() =>
            _perceptualHash.ValidateArguments([DataKind.Image, DataKind.Float32]));
    }

    [Fact]
    public void PerceptualHash_Validate_WrongType_Throws()
    {
        Assert.Throws<ArgumentException>(() => _perceptualHash.ValidateArguments([DataKind.Float32]));
    }

    [Fact]
    public void PerceptualHash_Returns64ElementVector()
    {
        byte[] png = MakeTestPng(32, 32);
        DataValue result = _perceptualHash.Execute([DataValue.FromImage(png)]);

        Assert.Equal(DataKind.Vector, result.Kind);
        float[] hash = result.AsVector();
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void PerceptualHash_ContainsOnlyBinaryValues()
    {
        byte[] png = MakeTestPng(32, 32, SKColors.Green);
        DataValue result = _perceptualHash.Execute([DataValue.FromImage(png)]);

        float[] hash = result.AsVector();

        foreach (float bit in hash)
        {
            Assert.True(bit is 0f or 1f, $"Expected 0.0 or 1.0, got {bit}");
        }
    }

    [Fact]
    public void PerceptualHash_IdenticalImages_ProduceIdenticalHashes()
    {
        byte[] png = MakeTestPng(32, 32, SKColors.Blue);
        DataValue result1 = _perceptualHash.Execute([DataValue.FromImage(png)]);
        DataValue result2 = _perceptualHash.Execute([DataValue.FromImage(png)]);

        float[] hash1 = result1.AsVector();
        float[] hash2 = result2.AsVector();

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void PerceptualHash_DifferentColorImages_ProduceSameHashForSolidColor()
    {
        // Solid colors resize to uniform images — dHash is all 0 (no differences)
        byte[] redPng = MakeTestPng(32, 32, SKColors.Red);
        byte[] bluePng = MakeTestPng(32, 32, SKColors.Blue);
        DataValue redResult = _perceptualHash.Execute([DataValue.FromImage(redPng)]);
        DataValue blueResult = _perceptualHash.Execute([DataValue.FromImage(bluePng)]);

        float[] redHash = redResult.AsVector();
        float[] blueHash = blueResult.AsVector();

        // Both solid-color images should produce all-zero hashes (no horizontal differences)
        Assert.Equal(redHash, blueHash);
    }

    [Fact]
    public void PerceptualHash_NullInput_ReturnsNull()
    {
        DataValue result = _perceptualHash.Execute([DataValue.Null(DataKind.Image)]);
        Assert.True(result.IsNull);
    }
}
