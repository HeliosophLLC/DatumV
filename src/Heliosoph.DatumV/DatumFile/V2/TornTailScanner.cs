using System.Buffers.Binary;

namespace Heliosoph.DatumV.DatumFile.V2;

/// <summary>
/// Backward scanner for the last valid <c>FMTD</c> tail in a torn
/// <c>.datum</c> file — extracted from the writer's torn-append
/// recovery path so readers can reuse it for non-destructive open.
/// </summary>
/// <remarks>
/// <para>
/// Both the writer's <c>RecoverIfTorn</c> and the reader's
/// <c>OpenInternal</c> call this when a file's last 8 bytes are not a
/// well-formed tail. Writer follows a successful find with
/// <c>SetLength</c> (destructive truncate); reader uses the recovered
/// EOF as the logical end-of-file for subsequent footer/page reads
/// without modifying the file (so a crashed write doesn't block
/// concurrent reads — the next writer reopen does the cleanup).
/// </para>
/// </remarks>
internal static class TornTailScanner
{
    /// <summary>
    /// Scans <paramref name="stream"/> backward from EOF for the last
    /// valid <c>FMTD</c> tail magic whose preceding 4-byte
    /// <c>footerByteLength</c> yields a plausible footer start
    /// (≥ <see cref="DatumFormatV2.HeaderSize"/>, &lt; the FMTD position).
    /// Returns the absolute byte position of the recovered tail's EOF
    /// (i.e. <c>fmtdPosition + 4</c>) or <c>-1</c> when no recoverable
    /// tail is found.
    /// </summary>
    /// <remarks>
    /// Does not mutate the file. The stream's <see cref="Stream.Position"/>
    /// is left undefined; callers should reset it before further reads.
    /// </remarks>
    public static long FindLastCleanTailEof(Stream stream)
    {
        if (stream.Length < DatumFormatV2.HeaderSize + DatumFormatV2.TailSize)
        {
            return -1;
        }

        const int chunkSize = 4096;
        const int overlap = 3;  // FMTD is 4 bytes; 3 bytes of overlap captures cross-chunk straddlers
        long fileLength = stream.Length;
        long minScanStart = DatumFormatV2.HeaderSize;

        byte[] buffer = new byte[chunkSize + overlap];

        long scanCursor = fileLength;
        while (scanCursor > minScanStart)
        {
            long chunkStart = Math.Max(minScanStart, scanCursor - chunkSize);
            long chunkEndExclusive = Math.Min(fileLength, scanCursor + overlap);
            int bytesToRead = (int)(chunkEndExclusive - chunkStart);
            if (bytesToRead < 4) break;

            stream.Position = chunkStart;
            stream.ReadExactly(buffer, 0, bytesToRead);

            for (int i = bytesToRead - 4; i >= 0; i--)
            {
                if (buffer[i] != (byte)'F' || buffer[i + 1] != (byte)'M'
                    || buffer[i + 2] != (byte)'T' || buffer[i + 3] != (byte)'D')
                {
                    continue;
                }

                long fmtdPosition = chunkStart + i;
                long tailEof = fmtdPosition + 4;

                if (fmtdPosition < DatumFormatV2.HeaderSize + 4)
                {
                    continue;
                }
                stream.Position = fmtdPosition - 4;
                Span<byte> lengthBytes = stackalloc byte[4];
                stream.ReadExactly(lengthBytes);
                uint footerLen = BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes);
                long footerStart = fmtdPosition - 4 - footerLen;
                if (footerStart < DatumFormatV2.HeaderSize || footerLen == 0)
                {
                    continue;
                }

                return tailEof;
            }

            if (chunkStart == minScanStart) break;
            scanCursor = chunkStart;
        }

        return -1;
    }
}
