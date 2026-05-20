using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2.Decoding;

/// <summary>
/// Picks the right <see cref="IPageDecoderV2"/> for a column based on its
/// <see cref="ColumnDescriptorV2.Encoder"/>. The reader instantiates one
/// per page on demand.
/// </summary>
internal static class PageDecoderFactoryV2
{
    /// <summary>
    /// Creates a decoder. <paramref name="sidecarSource"/> and
    /// <paramref name="eagerStore"/> are only consulted by the
    /// <see cref="VariableSlotPageDecoderV2"/> when the column kind needs
    /// eager sidecar materialization (today: Struct, legacy Array). Pass
    /// them when reading those kinds; <see langword="null"/> is fine for
    /// scalar / boolean / inline-only variable kinds.
    /// </summary>
    public static IPageDecoderV2 Create(
        ColumnDescriptorV2 column,
        ReadOnlyMemory<byte> pageBytes,
        int rowCount,
        byte sidecarStoreId,
        bool hasNullBitmap,
        IBlobSource? sidecarSource = null,
        IValueStore? eagerStore = null,
        ushort columnRuntimeStructTypeId = 0) =>
        column.Encoder switch
        {
            EncoderKind.FixedWidth => new FixedWidthPageDecoderV2(column, pageBytes, rowCount, hasNullBitmap),
            EncoderKind.BitPackedBoolean => new BitPackedBooleanPageDecoderV2(column, pageBytes, rowCount, hasNullBitmap),
            EncoderKind.VariableSlot => new VariableSlotPageDecoderV2(
                column, pageBytes, rowCount, sidecarStoreId, hasNullBitmap, sidecarSource, eagerStore, columnRuntimeStructTypeId),
            _ => throw new InvalidDataException(
                $"Unknown EncoderKind {column.Encoder} for column '{column.Name}'."),
        };
}
