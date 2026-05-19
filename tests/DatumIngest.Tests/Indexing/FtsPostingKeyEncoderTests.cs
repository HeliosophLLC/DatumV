using System.Text;
using Heliosoph.DatumV.Indexing.Fts;

namespace Heliosoph.DatumV.Tests.Indexing;

/// <summary>
/// Tests for <see cref="FtsPostingKeyEncoder"/>: terminator-based prefix
/// disambiguation ("cat" doesn't prefix "cats"), embedded-NUL escape,
/// memcmp ordering across terms, empty / unicode terms.
/// </summary>
public sealed class FtsPostingKeyEncoderTests
{
    [Fact]
    public void Encode_SimpleTerm_AppendsDoubleNullTerminator()
    {
        byte[] encoded = FtsPostingKeyEncoder.Encode("cat");
        Assert.Equal(new byte[] { (byte)'c', (byte)'a', (byte)'t', 0, 0 }, encoded);
    }

    [Fact]
    public void Encode_EmptyTerm_YieldsOnlyTerminator()
    {
        byte[] encoded = FtsPostingKeyEncoder.Encode(string.Empty);
        Assert.Equal(new byte[] { 0, 0 }, encoded);
    }

    [Fact]
    public void Encode_CatIsNotPrefixOfCats()
    {
        // The whole point of the terminator: "cat" must not prefix-match
        // "cats" postings during a FindPrefix lookup.
        byte[] cat = FtsPostingKeyEncoder.Encode("cat");
        byte[] cats = FtsPostingKeyEncoder.Encode("cats");

        Assert.False(StartsWith(cats, cat),
            "Encoded 'cat' must not be a prefix of encoded 'cats'.");
    }

    [Fact]
    public void Encode_LexOrderMatchesMemcmp()
    {
        // For every adjacent pair in lex-sorted order, the encoded forms
        // must sort the same way under SequenceCompareTo.
        string[] terms = { "a", "ab", "abc", "abd", "b", "ba", "cat", "cats", "dog" };

        for (int i = 1; i < terms.Length; i++)
        {
            byte[] prev = FtsPostingKeyEncoder.Encode(terms[i - 1]);
            byte[] curr = FtsPostingKeyEncoder.Encode(terms[i]);

            int cmp = prev.AsSpan().SequenceCompareTo(curr);
            Assert.True(cmp < 0,
                $"Encoded '{terms[i - 1]}' must sort before encoded '{terms[i]}' (cmp={cmp}).");
        }
    }

    [Fact]
    public void Encode_EmbeddedNul_EscapedAsZeroFF()
    {
        // A term with an embedded \x00 — analyzers never emit these, but the
        // encoder must remain memcmp-orderable for adversarial input.
        byte[] encoded = FtsPostingKeyEncoder.Encode("a\0b");

        Assert.Equal(new byte[] { (byte)'a', 0, 0xFF, (byte)'b', 0, 0 }, encoded);
    }

    [Fact]
    public void Encode_EmbeddedNul_StillSortsCorrectly()
    {
        // "a\0" sorts before "a\0b" sorts before "ab" lexicographically.
        // Encoded forms must preserve that.
        byte[] aNul = FtsPostingKeyEncoder.Encode("a\0");
        byte[] aNulB = FtsPostingKeyEncoder.Encode("a\0b");
        byte[] ab = FtsPostingKeyEncoder.Encode("ab");

        Assert.True(aNul.AsSpan().SequenceCompareTo(aNulB) < 0);
        Assert.True(aNulB.AsSpan().SequenceCompareTo(ab) < 0);
    }

    [Fact]
    public void Encode_UnicodeTerm_PreservesUtf8BytesAndTerminates()
    {
        byte[] encoded = FtsPostingKeyEncoder.Encode("café");

        // UTF-8 of "café" is c, a, f, 0xC3, 0xA9. Plus 0,0 terminator.
        byte[] utf8 = Encoding.UTF8.GetBytes("café");
        Assert.Equal(utf8.Length + 2, encoded.Length);
        Assert.True(encoded.AsSpan(0, utf8.Length).SequenceEqual(utf8));
        Assert.Equal(0, encoded[utf8.Length]);
        Assert.Equal(0, encoded[utf8.Length + 1]);
    }

    [Fact]
    public void Encode_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => FtsPostingKeyEncoder.Encode(null!));
    }

    [Fact]
    public void Encode_TwoCallsSameTerm_ReturnEqualBytes()
    {
        byte[] a = FtsPostingKeyEncoder.Encode("hello");
        byte[] b = FtsPostingKeyEncoder.Encode("hello");
        Assert.Equal(a, b);
    }

    private static bool StartsWith(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.Length > haystack.Length) return false;
        return haystack[..needle.Length].SequenceEqual(needle);
    }
}
