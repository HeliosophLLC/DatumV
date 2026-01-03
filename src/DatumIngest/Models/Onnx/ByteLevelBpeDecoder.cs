using System.Text;

namespace DatumIngest.Models.Onnx;

/// <summary>
/// Inverse of GPT-2 / RoBERTa byte-level BPE encoding. The encoder maps
/// non-printable bytes (including space and newline) to high-Unicode
/// codepoints so they survive BPE merging without ambiguity; the decoder
/// must reverse that mapping. <c>BpeTokenizer.Decode</c> doesn't apply
/// this reverse step, so a literal <c>Ġ</c> (U+0120, the encoded form
/// of space) leaks into the output unless we unmap it here.
/// </summary>
/// <remarks>
/// The GPT-2 byte-to-unicode table assigns codepoints 256+ to bytes
/// outside the printable-ASCII range. For English text only two bytes
/// typically need translation: space (<c>0x20 → Ġ</c> at U+0120) and
/// newline (<c>0x0A → Ċ</c> at U+010A). RoBERTa-family tokenizers use
/// the same mapping, so this helper is shared by both ViT-GPT2 (GPT-2
/// vocab) and TrOCR (RoBERTa vocab) decoders.
/// </remarks>
internal static class ByteLevelBpeDecoder
{
    private static readonly Dictionary<char, byte> UnicodeToByte = BuildUnicodeToByte();

    /// <summary>
    /// Translates a tokenizer-decoded string (which still contains the
    /// byte-level BPE mojibake) back into UTF-8 text.
    /// </summary>
    public static string Decode(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        byte[] bytes = new byte[raw.Length * 4];  // worst-case UTF-8 expansion
        Span<byte> singleCharBuf = stackalloc byte[4];
        Span<char> singleChar = stackalloc char[1];
        int byteIdx = 0;
        foreach (char c in raw)
        {
            if (UnicodeToByte.TryGetValue(c, out byte mapped))
            {
                bytes[byteIdx++] = mapped;
            }
            else
            {
                singleChar[0] = c;
                int written = Encoding.UTF8.GetBytes(singleChar, singleCharBuf);
                singleCharBuf[..written].CopyTo(bytes.AsSpan(byteIdx));
                byteIdx += written;
            }
        }
        return Encoding.UTF8.GetString(bytes, 0, byteIdx);
    }

    private static Dictionary<char, byte> BuildUnicodeToByte()
    {
        // Replicate GPT-2's bytes_to_unicode: printable bytes (! through ~,
        // ¡ through ¬, ® through ÿ) map to themselves; the remaining 68
        // unmapped bytes get codepoints starting at 0x100 (256).
        Dictionary<char, byte> reverse = new(256);
        List<int> printable =
        [
            .. Enumerable.Range('!', '~' - '!' + 1),
            .. Enumerable.Range('¡', '¬' - '¡' + 1),
            .. Enumerable.Range('®', 'ÿ' - '®' + 1),
        ];
        HashSet<int> printableSet = new(printable);

        foreach (int b in printable)
        {
            reverse[(char)b] = (byte)b;
        }

        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (printableSet.Contains(b)) continue;
            reverse[(char)(256 + n)] = (byte)b;
            n++;
        }
        return reverse;
    }
}
