using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests for the PR7 Image inline-metadata layout: <c>_p4</c> packs (width, height) as
/// two <see cref="ushort"/> halves and <c>_p5</c> byte 0 holds channels. Accessors
/// <see cref="DataValue.ImageWidth"/>, <see cref="DataValue.ImageHeight"/>,
/// <see cref="DataValue.ImageChannels"/> read these without touching the arena.
/// </summary>
public sealed class DataValueImageMetadataTests : ServiceTestBase
{
    private readonly Arena _store;

    public DataValueImageMetadataTests()
    {
        _store = CreateArena();
    }

    [Fact]
    public void FromImage_WithDimensions_RoundTripsThroughInlineAccessors()
    {
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0]; // JPEG signature bytes, content not material
        DataValue dv = DataValue.FromImage(payload, _store, width: 1920, height: 1080, channels: 3);

        Assert.Equal(DataKind.Image, dv.Kind);
        Assert.True(dv.IsArenaBacked);
        Assert.Equal((ushort)1920, dv.ImageWidth);
        Assert.Equal((ushort)1080, dv.ImageHeight);
        Assert.Equal((byte)3, dv.ImageChannels);
    }

    [Fact]
    public void FromImage_WithoutDimensions_AccessorsReturnZero()
    {
        // The no-metadata overload — legacy path, what existing callers still use.
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0];
        DataValue dv = DataValue.FromImage(payload, _store);

        Assert.Equal((ushort)0, dv.ImageWidth);
        Assert.Equal((ushort)0, dv.ImageHeight);
        Assert.Equal((byte)0, dv.ImageChannels);
    }

    [Fact]
    public void FromImage_AtCodecCeiling_PreservesUInt16Width()
    {
        // uint16 cap chosen because AV1/VP9/JPEG codec specs themselves cap at 65535.
        byte[] payload = [0xFF, 0xD8];
        DataValue dv = DataValue.FromImage(payload, _store, width: 65535, height: 65535, channels: 4);

        Assert.Equal((ushort)65535, dv.ImageWidth);
        Assert.Equal((ushort)65535, dv.ImageHeight);
        Assert.Equal((byte)4, dv.ImageChannels);
    }

    [Fact]
    public void FromImageAtOffset_WithDimensions_ProducesArenaBackedValueWithMetadata()
    {
        byte[] payload = [0x89, 0x50, 0x4E, 0x47]; // PNG signature, content irrelevant
        var (offset, length) = _store.StoreBytes(payload);

        DataValue dv = DataValue.FromImageAtOffset(offset.Value, length.Value, width: 800, height: 600, channels: 4);

        Assert.Equal(DataKind.Image, dv.Kind);
        Assert.True(dv.IsArenaBacked);
        Assert.Equal((ushort)800, dv.ImageWidth);
        Assert.Equal((ushort)600, dv.ImageHeight);
        Assert.Equal((byte)4, dv.ImageChannels);
        Assert.Equal(payload, dv.AsImage(_store));
    }

    [Fact]
    public void ImageWidth_OnNonImageKind_ReturnsZero()
    {
        // The kind guard matters: for inline-string values whose UTF-8 spills into _p6
        // (≥25 bytes), or any other kind, the accessor must return 0 instead of leaking
        // unrelated bytes interpreted as a ushort.
        DataValue notImage = DataValue.FromInt32(0x12345678);
        Assert.Equal((ushort)0, notImage.ImageWidth);
        Assert.Equal((ushort)0, notImage.ImageHeight);
        Assert.Equal((byte)0, notImage.ImageChannels);

        DataValue longInline = DataValue.FromString("2026-05-22T13:45:00.123", _store); // 23 bytes, inline
        Assert.True(longInline.IsInline);
        Assert.Equal((ushort)0, longInline.ImageWidth);
    }

    [Fact]
    public void Stabilize_PreservesImageDimensions()
    {
        // DataValueRetention.Stabilize must forward inline dimensions through the
        // cross-arena copy — otherwise downstream image_width() falls back to a
        // full SkiaSharp decode unnecessarily.
        byte[] payload = [0xFF, 0xD8, 0xFF, 0xE0];
        DataValue source = DataValue.FromImage(payload, _store, width: 4096, height: 2160, channels: 3);

        using Arena retention = CreateArena();
        DataValue stabilized = DataValueRetention.Stabilize(source, _store, retention);

        Assert.Equal((ushort)4096, stabilized.ImageWidth);
        Assert.Equal((ushort)2160, stabilized.ImageHeight);
        Assert.Equal((byte)3, stabilized.ImageChannels);
    }

    [Fact]
    public void WithArenaOffset_PreservesImageDimensions()
    {
        // The post-cleanup WithArenaOffset shifts only the offset words; kind-specific
        // metadata in _p4/_p5/_p6 must round-trip unchanged.
        byte[] payload = [0xFF, 0xD8];
        var (offset, length) = _store.StoreBytes(payload);
        DataValue source = DataValue.FromImageAtOffset(offset.Value, length.Value, width: 1024, height: 768, channels: 4);

        DataValue shifted = source.WithArenaOffsetForTest(delta: 256);

        Assert.Equal((ushort)1024, shifted.ImageWidth);
        Assert.Equal((ushort)768, shifted.ImageHeight);
        Assert.Equal((byte)4, shifted.ImageChannels);
    }
}

/// <summary>
/// Test-only access to <see cref="DataValue.WithArenaOffset(long)"/>, which is
/// <c>internal</c>. Exposed via <see cref="InternalsVisibleToAttribute"/> on the
/// production assembly.
/// </summary>
internal static class DataValueTestHelpers
{
    public static DataValue WithArenaOffsetForTest(this DataValue value, long delta) =>
        value.WithArenaOffset(delta);
}
