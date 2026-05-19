namespace Heliosoph.DatumV.DatumFile.V2.Encoding;

/// <summary>
/// Result of flushing a column page: the on-disk byte payload, the
/// number of rows it represents, and the zone map summarizing those rows.
/// The bytes are exactly what the writer streams to the file at the
/// page's recorded <see cref="PageDescriptorV2.PageOffset"/>.
/// </summary>
/// <param name="Bytes">
/// The page payload. Layout depends on the encoder; for FixedWidth it is
/// optional null bitmap + raw payload, for BitPackedBoolean it is null
/// bitmap + value bitmap, for VariableSlot it is null bitmap + inline
/// bitmap + 16-byte slots.
/// </param>
/// <param name="RowCount">Rows captured in this page (≤ page size).</param>
/// <param name="ZoneMap">
/// Zone map covering the values in this page. May carry only a null
/// count for non-comparable kinds.
/// </param>
/// <param name="HasNullBitmap">
/// Whether <see cref="Bytes"/> begins with a per-row null bitmap. Records
/// the encoder's actual output so the writer can stamp the matching
/// <see cref="PageDescriptorV2.HasNullBitmap"/> flag — necessary because
/// the column descriptor's <c>IsNullable</c> may have been mutated
/// between encoder construction and page flush (e.g. by
/// <c>ALTER … DROP NOT NULL</c>).
/// </param>
internal readonly record struct EncodedPageV2(
    byte[] Bytes,
    int RowCount,
    DatumZoneMap ZoneMap,
    bool HasNullBitmap);
