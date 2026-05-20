namespace Heliosoph.DatumV.DatumFile.V2.Encoding;

/// <summary>
/// Translates a runtime <see cref="Heliosoph.DatumV.Model.TypeRegistry"/> id to a
/// stable per-file on-disk id during encode, allocating new on-disk ids on
/// first sight. Owned by <see cref="DatumFileWriterV2"/>; consumed by the
/// <see cref="VariableSlotPageEncoderV2"/> when stamping per-element
/// <c>ArraySlot</c> reserved bytes for <c>Array&lt;Struct&gt;</c> columns
/// and by the writer itself when picking a <c>StructTypeId</c> for the
/// column footer.
/// </summary>
/// <remarks>
/// On-disk ids are dense (1..N in emission order) so the file's type table
/// is compact and reader-side translation is a flat dictionary. Runtime
/// ids are sparse and per-query — passing them through verbatim would
/// make slot bytes meaningless when the file is opened in a different
/// query, breaking cross-query portability. Translation happens once per
/// runtime id (cached in a dictionary); subsequent lookups are O(1).
/// </remarks>
internal interface ITypeIdAllocator
{
    /// <summary>
    /// Returns the on-disk type-id assigned to <paramref name="runtimeTypeId"/>,
    /// allocating a new one on first sight. Passing 0 (the no-type sentinel)
    /// returns 0 unchanged.
    /// </summary>
    ushort AllocateOrLookup(ushort runtimeTypeId);
}
