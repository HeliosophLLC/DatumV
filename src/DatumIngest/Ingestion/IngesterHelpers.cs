using DatumIngest.DatumFile.Sidecar;
using DatumIngest.DatumFile.V2;
using DatumIngest.Model;

namespace DatumIngest.Ingestion;

/// <summary>
/// Helpers shared between <see cref="Ingester"/> and other producers of
/// <c>.datum</c> files (notably <c>SqlIngestExecutor</c> on the dataset
/// pipeline). Each method is a pure mapping over format-level pieces —
/// no allocations beyond what the caller controls.
/// </summary>
internal static class IngesterHelpers
{
    /// <summary>
    /// Returns the companion sidecar path for a given <c>.datum</c> output
    /// path. Strips the <c>.datum</c> extension if present and appends
    /// <c>.datum-blob</c>; otherwise appends <c>.datum-blob</c> to the full
    /// path.
    /// </summary>
    public static string SidecarPathFor(string datumPath)
        => Path.ChangeExtension(datumPath, SidecarConstants.FileExtension);

    /// <summary>
    /// Converts a <see cref="Schema"/> to the v2 column-descriptor list.
    /// Encoder kind is picked by <see cref="ColumnDescriptorV2.EncoderFor"/>;
    /// nullability comes from <see cref="ColumnInfo.Nullable"/>; array
    /// shape rides through directly via <see cref="ColumnInfo.IsArray"/>.
    /// </summary>
    public static ColumnDescriptorV2[] ToV2Descriptors(Schema schema)
    {
        ColumnDescriptorV2[] descriptors = new ColumnDescriptorV2[schema.Columns.Count];
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            ColumnInfo col = schema.Columns[i];
            descriptors[i] = new ColumnDescriptorV2(
                Name: col.Name,
                Kind: col.Kind,
                Encoder: ColumnDescriptorV2.EncoderFor(col.Kind, col.IsArray),
                IsNullable: col.Nullable,
                IsArray: col.IsArray,
                FixedShape: col.FixedShape,
                MaxLength: col.MaxLength,
                IsBlankPadded: col.IsBlankPadded);
        }
        return descriptors;
    }
}
