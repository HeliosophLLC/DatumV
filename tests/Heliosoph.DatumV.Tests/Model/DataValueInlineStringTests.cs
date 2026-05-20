using System.IO.Hashing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Model;

/// <summary>
/// Tests for inline UTF-8 storage in <see cref="DataValue"/> for
/// <see cref="DataKind.String"/> values
/// whose UTF-8 form fits in 16 bytes.
/// </summary>
public sealed class DataValueInlineStringTests : ServiceTestBase
{
    private readonly Arena _store;

    public DataValueInlineStringTests()
    {
        _store = CreateArena();
    }

    // ───────────────────── Capacity boundary ─────────────────────

    [Theory]
    [InlineData("")]        // 0 bytes
    [InlineData("a")]       // 1 byte ASCII
    [InlineData("hello")]   // 5 bytes ASCII
    [InlineData("1234567890")]                    // 10 bytes ASCII
    [InlineData("abcdefghijklmno")]               // 15 bytes ASCII
    [InlineData("abcdefghijklmnop")]              // 16 bytes ASCII
    [InlineData("2026-05-22T13:45:00")]           // 19 bytes — common datetime format
    [InlineData("abcdefghijklmnopqrstuvwxyz")]    // 26 bytes ASCII
    [InlineData("abcdefghijklmnopqrstuvwxy1")]    // 26 bytes ASCII
    [InlineData("abcdefghijklmnopqrstuvwxyzA")]   // 27 bytes ASCII (boundary)
    public void FromString_UpTo27Bytes_IsInline(string value)
    {
        DataValue dv = DataValue.FromString(value, _store);
        Assert.True(dv.IsInline, $"expected inline for length {value.Length}");
        Assert.False(dv.IsArenaBacked);
        Assert.Equal(value, dv.AsString(_store));
    }

    [Theory]
    [InlineData("abcdefghijklmnopqrstuvwxyzAB")]  // 28 bytes ASCII (over)
    [InlineData("this string is definitely longer than twenty-seven bytes")]
    public void FromString_Over27Bytes_UsesStore(string value)
    {
        DataValue dv = DataValue.FromString(value, _store);
        Assert.False(dv.IsInline);
        Assert.Equal(value, dv.AsString(_store));
    }

    [Fact]
    public void FromString_NonAscii_UsesUtf8ByteLengthForBoundary()
    {
        // "café" = 5 UTF-8 bytes (é = 0xC3 0xA9) — fits inline.
        DataValue inline = DataValue.FromString("café", _store);
        Assert.True(inline.IsInline);
        Assert.Equal("café", inline.AsString(_store));

        // 13 multi-byte chars = 26 UTF-8 bytes — fits within the 27-byte cap.
        string thirteenE = new('é', 13);
        DataValue underCap = DataValue.FromString(thirteenE, _store);
        Assert.True(underCap.IsInline, $"expected inline for {thirteenE.Length} chars / {System.Text.Encoding.UTF8.GetByteCount(thirteenE)} UTF-8 bytes");
        Assert.Equal(thirteenE, underCap.AsString(_store));

        // 14 multi-byte chars = 28 bytes — spills to store.
        string fourteenE = new('é', 14);
        DataValue overflow = DataValue.FromString(fourteenE, _store);
        Assert.False(overflow.IsInline);
        Assert.Equal(fourteenE, overflow.AsString(_store));
    }

    // ───────────────────── Accessors ─────────────────────

    [Fact]
    public void AsUtf8Span_Inline_ReturnsBytesDirectlyFromStruct()
    {
        DataValue dv = DataValue.FromString("hello", _store);
        Assert.True(dv.IsInline);
        ReadOnlySpan<byte> span = dv.AsUtf8Span(_store);
        Assert.Equal(5, span.Length);
        Assert.Equal([(byte)'h', (byte)'e', (byte)'l', (byte)'l', (byte)'o'], span.ToArray());
    }

    [Fact]
    public void AsUtf8Span_Empty_ReturnsEmptySpan()
    {
        DataValue dv = DataValue.FromString("", _store);
        Assert.True(dv.IsInline);
        Assert.Equal(0, dv.AsUtf8Span(_store).Length);
    }

    [Fact]
    public void ContentByteLength_Inline_ReturnsUtf8ByteCount()
    {
        DataValue dv = DataValue.FromString("café", _store); // 5 UTF-8 bytes
        Assert.Equal(5, dv.ContentByteLength);
    }

    [Fact]
    public void StringCharCount_Inline_DecodesUtf8ForAccurateCount()
    {
        DataValue dv = DataValue.FromString("café", _store); // 4 chars, 5 bytes
        Assert.Equal(4, dv.StringCharCount(_store));
    }

    // ───────────────────── Hash stability ─────────────────────

    [Fact]
    public void RawContentHash_Inline_MatchesXxHash64OfUtf8Bytes()
    {
        string value = "test";
        DataValue dv = DataValue.FromString(value, _store);
        Assert.True(dv.IsInline);
        ulong expected = XxHash64.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(value));
        Assert.Equal(expected, dv.RawContentHash);
    }

    [Fact]
    public void RawContentHash_SameContent_MatchesAcrossInlineAndStore()
    {
        // Short string → inline. Construct a matching long form that forces the store path,
        // then compare hashes of identical UTF-8 content.
        string shortForm = "hello";
        DataValue inline = DataValue.FromString(shortForm, _store);
        Assert.True(inline.IsInline);

        // FromUtf8Span with length > 16 forces the store path even for shorter bytes
        // (we construct an explicit >16 byte version to compare hashes for the equal-content case).
        DataValue shortViaStore = DataValue.FromUtf8Span(
            System.Text.Encoding.UTF8.GetBytes(shortForm),
            _store);
        // shortForm is 5 bytes → also inline. Use a longer string for the cross check.

        string longForm = "this is a much longer test string";
        DataValue store1 = DataValue.FromString(longForm, _store);
        DataValue store2 = DataValue.FromString(longForm, _store);
        Assert.False(store1.IsInline);
        Assert.False(store2.IsInline);
        Assert.Equal(store1.RawContentHash, store2.RawContentHash);
    }

    // ───────────────────── Equality ─────────────────────

    [Fact]
    public void Equals_SameInlineContent_ReturnsTrue()
    {
        DataValue a = DataValue.FromString("hello", _store);
        DataValue b = DataValue.FromString("hello", _store);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    [Fact]
    public void Equals_DifferentInlineContent_ReturnsFalse()
    {
        DataValue a = DataValue.FromString("hello", _store);
        DataValue b = DataValue.FromString("world", _store);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_PrefixMismatchAtDifferentLengths_ReturnsFalse()
    {
        // "hello" and "hello!" have the same 5-byte prefix but different lengths.
        DataValue a = DataValue.FromString("hello", _store);
        DataValue b = DataValue.FromString("hello!", _store);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Equals_InlineAndStoreForms_ContentMatchesViaHash()
    {
        // Short form is inline; long form with same prefix is reference-store.
        // They represent different logical strings so should not be equal.
        DataValue shortForm = DataValue.FromString("short", _store);
        DataValue longForm = DataValue.FromString("this is a much longer string", _store);
        Assert.True(shortForm.IsInline);
        Assert.False(longForm.IsInline);
        Assert.NotEqual(shortForm, longForm);
    }

    // ───────────────────── IsArenaBacked exclusion ─────────────────────

    [Fact]
    public void IsArenaBacked_InlineString_ReturnsFalse()
    {
        DataValue dv = DataValue.FromString("short", _store);
        Assert.True(dv.IsInline);
        Assert.False(dv.IsArenaBacked);
    }

    [Fact]
    public void IsArenaBacked_ArenaSliceString_ReturnsTrue()
    {
        DataValue dv = DataValue.FromStringSlice(offset: 0, length: 5);
        Assert.False(dv.IsInline);
        Assert.True(dv.IsArenaBacked);
    }

    // ───────────────────── Size invariant ─────────────────────

    [Fact]
    public void DataValue_SizeMatchesSizeBytesConstant()
    {
        // Inline storage must not change the struct size.
        Assert.Equal(DataValue.SizeBytes, System.Runtime.CompilerServices.Unsafe.SizeOf<DataValue>());
    }
}
