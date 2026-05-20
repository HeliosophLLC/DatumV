using System.Diagnostics.CodeAnalysis;

namespace Heliosoph.DatumV.Serialization.Fits;

/// <summary>
/// Parsed FITS Header-Data Unit descriptor: every card, the derived
/// kind/shape metadata, and the byte offsets of the header and data
/// sections. One instance per HDU in a file.
/// </summary>
/// <remarks>
/// <para>
/// FITS files are a concatenation of HDUs; each one has a header section
/// of 2880-byte blocks (36 × 80-byte cards per block) terminated by an
/// <c>END</c> card, followed by a data section padded out to the next
/// 2880-byte boundary. <see cref="TryReadNext"/> reads one HDU starting
/// at the stream's current position; the caller advances past the data
/// section with <see cref="SkipData"/> before reading the next HDU.
/// </para>
/// </remarks>
internal sealed class FitsHduDescriptor
{
    /// <summary>FITS header / data blocks are exactly 2880 bytes (36 cards × 80 bytes).</summary>
    public const int BlockByteSize = 2880;

    private const int CardsPerBlock = BlockByteSize / FitsCard.CardByteSize;

    /// <summary>Byte offset of the first header card in the source stream.</summary>
    public required long HeaderOffset { get; init; }

    /// <summary>Byte offset of the first data byte (immediately after the header's trailing 2880-byte block).</summary>
    public required long DataOffset { get; init; }

    /// <summary>Logical size of the data section in bytes (NAXIS1 × NAXIS2 × … × |BITPIX|/8 for images, NAXIS1 × NAXIS2 for tables, 0 for header-only).</summary>
    public required long DataByteSize { get; init; }

    /// <summary>Data section size padded out to the next 2880-byte boundary — the byte count to skip when advancing to the next HDU.</summary>
    public long DataPaddedByteSize => RoundUpToBlock(DataByteSize);

    /// <summary>All cards read from the header, in file order. Includes COMMENT/HISTORY cards but excludes the trailing END card.</summary>
    public required IReadOnlyList<FitsCard> Cards { get; init; }

    /// <summary>HDU kind, derived from the primary <c>SIMPLE</c> card or the <c>XTENSION</c> card.</summary>
    public required FitsHduKind Kind { get; init; }

    /// <summary>BITPIX value (image pixel format / table row width selector). 0 when absent.</summary>
    public required int BitPix { get; init; }

    /// <summary>NAXIS dimension count. 0 for header-only HDUs.</summary>
    public required int NAxis { get; init; }

    /// <summary>NAXIS1, NAXIS2, … values in file order. Length == NAxis.</summary>
    public required IReadOnlyList<int> NAxisN { get; init; }

    /// <summary>BSCALE multiplier (default 1.0). Applied as <c>physical = BZERO + BSCALE * raw</c>.</summary>
    public double BScale { get; init; } = 1.0;

    /// <summary>BZERO offset (default 0.0). Applied as <c>physical = BZERO + BSCALE * raw</c>.</summary>
    public double BZero { get; init; }

    /// <summary>EXTNAME card value (e.g. <c>SCI</c>, <c>ERR</c>, <c>DQ</c>) if present.</summary>
    public string? ExtName { get; init; }

    /// <summary>EXTVER card value if present.</summary>
    public int? ExtVer { get; init; }

    /// <summary>TFIELDS card value (column count) for BINTABLE / TABLE HDUs.</summary>
    public int? TFields { get; init; }

    /// <summary>True if NAxis &gt; 0 and every NAXISn is positive (image HDU has pixel data).</summary>
    public bool HasData => DataByteSize > 0;

    /// <summary>
    /// Reads one HDU header at the current stream position and returns the
    /// parsed descriptor. The caller is responsible for advancing past the
    /// data section before reading the next HDU — use <see cref="SkipData"/>.
    /// </summary>
    /// <param name="stream">Source stream. Must be seekable so the header offset can be captured and the data block size computed.</param>
    /// <param name="isPrimary"><c>true</c> for HDU 0 (must start with <c>SIMPLE</c>); <c>false</c> for extension HDUs (must start with <c>XTENSION</c>).</param>
    public static FitsHduDescriptor Read(Stream stream, bool isPrimary)
    {
        long headerOffset = stream.Position;
        List<FitsCard> cards = [];
        Span<byte> block = stackalloc byte[BlockByteSize];

        bool endFound = false;
        while (!endFound)
        {
            ReadExactly(stream, block);
            for (int i = 0; i < CardsPerBlock; i++)
            {
                ReadOnlySpan<byte> cardBytes = block.Slice(i * FitsCard.CardByteSize, FitsCard.CardByteSize);
                FitsCard card = FitsCard.Parse(cardBytes);
                if (card.IsEnd)
                {
                    endFound = true;
                    // Skip remaining cards in this block — they're padding.
                    break;
                }
                cards.Add(card);
            }
        }

        long dataOffset = stream.Position;
        return BuildDescriptor(cards, headerOffset, dataOffset, isPrimary);
    }

    /// <summary>
    /// Reads the next HDU's header, or returns <c>false</c> at clean EOF.
    /// </summary>
    public static bool TryReadNext(
        Stream stream,
        bool isPrimary,
        [NotNullWhen(true)] out FitsHduDescriptor? descriptor)
    {
        // Peek one byte to distinguish "clean EOF after final HDU's padding"
        // from "truncated mid-header". The seek-back keeps Read's contract.
        long mark = stream.Position;
        int peeked = stream.ReadByte();
        if (peeked < 0)
        {
            descriptor = null;
            return false;
        }
        stream.Position = mark;

        descriptor = Read(stream, isPrimary);
        return true;
    }

    /// <summary>
    /// Advances <paramref name="stream"/> past this HDU's data section and
    /// the trailing 2880-byte padding so the next HDU read starts at the
    /// correct offset.
    /// </summary>
    public void SkipData(Stream stream)
    {
        long target = DataOffset + DataPaddedByteSize;
        if (stream.Position == target) return;
        stream.Position = target;
    }

    /// <summary>Pads <paramref name="size"/> up to the next 2880-byte block boundary.</summary>
    public static long RoundUpToBlock(long size)
    {
        if (size <= 0) return 0;
        long remainder = size % BlockByteSize;
        return remainder == 0 ? size : size + (BlockByteSize - remainder);
    }

    /// <summary>
    /// On-disk byte width of one row for BINTABLE / ASCII-table HDUs (= NAXIS1).
    /// 0 when NAXIS == 0. For image HDUs NAXIS1 is the fast-axis pixel count,
    /// not a byte width — callers don't use this property there.
    /// </summary>
    public int RowByteSize => NAxis >= 1 ? NAxisN[0] : 0;

    /// <summary>Number of pixel-data elements (product of NAXISn). 0 when NAxis == 0.</summary>
    public long PixelCount
    {
        get
        {
            if (NAxis == 0) return 0;
            long product = 1;
            for (int i = 0; i < NAxisN.Count; i++)
            {
                product = checked(product * NAxisN[i]);
            }
            return product;
        }
    }

    /// <summary>
    /// Bytes per pixel for image HDUs (|BITPIX| / 8). Defined for image and
    /// primary HDUs that carry pixel data; called on table HDUs with care
    /// because BITPIX in BINTABLE/TABLE has a different semantic.
    /// </summary>
    public int BytesPerPixel => Math.Abs(BitPix) / 8;

    private static FitsHduDescriptor BuildDescriptor(
        List<FitsCard> cards,
        long headerOffset,
        long dataOffset,
        bool isPrimary)
    {
        // First-card sanity gate. Primary HDUs MUST open with SIMPLE; every
        // extension MUST open with XTENSION. Anything else is a malformed
        // file (the only other allowed first-card values, GROUPS / EXTEND
        // etc., are second-card-or-later positions).
        if (cards.Count == 0)
        {
            throw new InvalidDataException("FITS HDU header is empty (no cards before END).");
        }

        string firstKeyword = cards[0].Keyword;
        if (isPrimary && firstKeyword != "SIMPLE")
        {
            throw new InvalidDataException(
                $"FITS primary HDU header must start with SIMPLE; first card is '{firstKeyword}'.");
        }
        if (!isPrimary && firstKeyword != "XTENSION")
        {
            throw new InvalidDataException(
                $"FITS extension HDU header must start with XTENSION; first card is '{firstKeyword}'.");
        }

        FitsHduKind kind = isPrimary
            ? FitsHduKind.Primary
            : ClassifyExtension(cards[0].AsString());

        int bitPix = LookupInt32(cards, "BITPIX") ?? 0;
        int nAxis = LookupInt32(cards, "NAXIS") ?? 0;
        int[] nAxisN = new int[nAxis];
        for (int i = 0; i < nAxis; i++)
        {
            string key = $"NAXIS{i + 1}";
            nAxisN[i] = LookupInt32(cards, key)
                ?? throw new InvalidDataException(
                    $"FITS HDU declares NAXIS={nAxis} but card '{key}' is missing.");
            if (nAxisN[i] < 0)
            {
                throw new InvalidDataException(
                    $"FITS HDU has negative dimension {key}={nAxisN[i]}.");
            }
        }

        long dataByteSize = ComputeDataByteSize(kind, bitPix, nAxisN, cards);

        return new FitsHduDescriptor
        {
            HeaderOffset = headerOffset,
            DataOffset = dataOffset,
            DataByteSize = dataByteSize,
            Cards = cards,
            Kind = kind,
            BitPix = bitPix,
            NAxis = nAxis,
            NAxisN = nAxisN,
            BScale = LookupDouble(cards, "BSCALE") ?? 1.0,
            BZero = LookupDouble(cards, "BZERO") ?? 0.0,
            ExtName = LookupString(cards, "EXTNAME"),
            ExtVer = LookupInt32(cards, "EXTVER"),
            TFields = LookupInt32(cards, "TFIELDS"),
        };
    }

    private static long ComputeDataByteSize(FitsHduKind kind, int bitPix, int[] nAxisN, List<FitsCard> cards)
    {
        // BINTABLE / TABLE: NAXIS1 = row bytes, NAXIS2 = row count. BITPIX is
        // always 8 by spec, but the row width and table size are NAXIS-driven.
        // GROUPS / PCOUNT / GCOUNT extensions can extend this with a heap area
        // (used by variable-length-array columns); we account for PCOUNT below
        // so files that point heap-backed VLA columns past the table data
        // still advance to the next HDU cleanly. We don't decode the heap in
        // v1 — variable-length columns throw at read time.
        if (nAxisN.Length == 0) return 0;

        long product = 1;
        for (int i = 0; i < nAxisN.Length; i++)
        {
            product = checked(product * nAxisN[i]);
        }

        int pcount = LookupInt32(cards, "PCOUNT") ?? 0;
        int gcount = LookupInt32(cards, "GCOUNT") ?? 1;

        long elementBytes = Math.Abs(bitPix) / 8;
        return checked(elementBytes * (pcount + (product * gcount)));
    }

    private static FitsHduKind ClassifyExtension(string? xtensionValue)
    {
        if (xtensionValue is null) return FitsHduKind.Unknown;
        string trimmed = xtensionValue.Trim();
        return trimmed switch
        {
            "IMAGE" => FitsHduKind.Image,
            "BINTABLE" => FitsHduKind.BinTable,
            // A3DTABLE is the (deprecated) original name for BINTABLE — treat as the same.
            "A3DTABLE" => FitsHduKind.BinTable,
            "TABLE" => FitsHduKind.AsciiTable,
            _ => FitsHduKind.Unknown,
        };
    }

    private static int? LookupInt32(List<FitsCard> cards, string keyword)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (string.Equals(cards[i].Keyword, keyword, StringComparison.Ordinal))
            {
                long? v = cards[i].AsInt64();
                if (v is null) return null;
                return checked((int)v.Value);
            }
        }
        return null;
    }

    private static double? LookupDouble(List<FitsCard> cards, string keyword)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (string.Equals(cards[i].Keyword, keyword, StringComparison.Ordinal))
            {
                return cards[i].AsDouble();
            }
        }
        return null;
    }

    private static string? LookupString(List<FitsCard> cards, string keyword)
    {
        for (int i = 0; i < cards.Count; i++)
        {
            if (string.Equals(cards[i].Keyword, keyword, StringComparison.Ordinal))
            {
                return cards[i].AsString();
            }
        }
        return null;
    }

    internal static void ReadExactly(Stream stream, Span<byte> buffer)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
            {
                throw new InvalidDataException(
                    $"Unexpected end of FITS file: expected {buffer.Length} bytes but read {totalRead}.");
            }
            totalRead += bytesRead;
        }
    }
}
