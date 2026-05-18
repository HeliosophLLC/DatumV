namespace DatumIngest.Serialization.Fits;

/// <summary>
/// The kind of a FITS Header-Data Unit. Determined by the primary
/// <c>SIMPLE</c> card (for HDU 0) or the <c>XTENSION</c> card (for
/// subsequent HDUs).
/// </summary>
internal enum FitsHduKind
{
    /// <summary>HDU 0 in a SIMPLE FITS file. Carries an image array (possibly empty when NAXIS=0).</summary>
    Primary,

    /// <summary><c>XTENSION='IMAGE   '</c>. Typed pixel array per BITPIX.</summary>
    Image,

    /// <summary><c>XTENSION='BINTABLE'</c>. Fixed-width binary table rows.</summary>
    BinTable,

    /// <summary><c>XTENSION='TABLE   '</c>. ASCII table. Rare in modern data; reader doesn't decode rows in v1.</summary>
    AsciiTable,

    /// <summary>Unrecognised XTENSION value. The reader skips the data section but surfaces the metadata.</summary>
    Unknown,
}
