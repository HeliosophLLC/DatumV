using System.Text;

namespace Heliosoph.DatumV.Indexing.Fts;

/// <summary>
/// Encodes a tokenized term into the byte key used by Shape-A FTS storage —
/// the dup-key <see cref="Heliosoph.DatumV.Indexing.BTree.MutableBytes.MutableBPlusTreeBytes"/>
/// behind <see cref="FullTextSearchIndex"/>. Posting tie-breakers (chunk,
/// rowOff) live on <see cref="Heliosoph.DatumV.Indexing.BTree.MutableBytes.BytesIndexEntry"/>
/// directly and are not part of the encoded key.
/// </summary>
/// <remarks>
/// <para>Encoding: <c>utf8(term)</c> with the same <c>\x00 → \x00\xFF</c>
/// escape and <c>\x00\x00</c> terminator that
/// <see cref="Heliosoph.DatumV.Indexing.CompositeKeyEncoder"/> uses for strings.
/// The terminator is what prevents one term from being a prefix of another:
/// keys for term <c>"cat"</c> start with <c>c,a,t,0x00,0x00</c> and keys
/// for <c>"cats"</c> start with <c>c,a,t,s,0x00,0x00</c>, so a prefix scan
/// for <c>"cat"</c> doesn't accidentally pull in <c>"cats"</c> postings.</para>
///
/// <para>The escape logic mirrors <see cref="Heliosoph.DatumV.Indexing.CompositeKeyEncoder"/>;
/// promote to a shared helper if a third caller appears.</para>
/// </remarks>
internal static class FtsPostingKeyEncoder
{
    /// <summary>
    /// Encodes <paramref name="term"/> into the prefix used both for
    /// insertion (as the bytes-tree key) and for prefix-scan lookup of
    /// every posting carrying that term.
    /// </summary>
    public static byte[] Encode(string term)
    {
        ArgumentNullException.ThrowIfNull(term);

        int utf8Size = Encoding.UTF8.GetByteCount(term);
        // Worst case the term is all NULs and every byte doubles; +2 for the terminator.
        byte[] buffer = new byte[(utf8Size * 2) + 2];

        int written = 0;
        if (utf8Size > 0)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(term);
            for (int i = 0; i < utf8.Length; i++)
            {
                byte b = utf8[i];
                buffer[written++] = b;
                if (b == 0)
                {
                    buffer[written++] = 0xFF;
                }
            }
        }
        buffer[written++] = 0;
        buffer[written++] = 0;

        if (written == buffer.Length)
        {
            return buffer;
        }

        byte[] trimmed = new byte[written];
        Buffer.BlockCopy(buffer, 0, trimmed, 0, written);
        return trimmed;
    }
}
