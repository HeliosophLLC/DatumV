namespace DatumIngest.DatumFile.V2.Encoding;

/// <summary>
/// Picks the right <see cref="IPageEncoderV2"/> for a column based on its
/// <see cref="ColumnDescriptorV2.Encoder"/>. The writer holds one encoder
/// per column and reuses it across pages.
/// </summary>
internal static class PageEncoderFactoryV2
{
    public static IPageEncoderV2 Create(ColumnDescriptorV2 column, int pageSize) =>
        column.Encoder switch
        {
            EncoderKind.FixedWidth => new FixedWidthPageEncoderV2(column, pageSize),
            EncoderKind.BitPackedBoolean => new BitPackedBooleanPageEncoderV2(column, pageSize),
            EncoderKind.VariableSlot => new VariableSlotPageEncoderV2(column, pageSize),
            _ => throw new NotSupportedException(
                $"Unknown EncoderKind {column.Encoder} for column '{column.Name}'."),
        };
}
