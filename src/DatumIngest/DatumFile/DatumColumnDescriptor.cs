using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Describes a single column within a <c>.datum</c> file schema: its name, data kind,
/// column-level flags, optional fixed shape for tensor types, and the externalization
/// threshold for binary columns.
/// </summary>
public sealed record DatumColumnDescriptor
{
    /// <summary>
    /// Creates a column descriptor with explicit field values.
    /// </summary>
    /// <param name="name">The column name as it appears in query expressions.</param>
    /// <param name="kind">The <see cref="DataKind"/> of values stored in this column.</param>
    /// <param name="flags">Bitfield of column-level properties.</param>
    /// <param name="fixedShape">
    /// Dimension array for Vector (length 1), Matrix (length 2: rows, cols), or Tensor (rank N) columns.
    /// <c>null</c> for all other kinds. Populated by the writer on first row group flush.
    /// </param>
    /// <param name="externalizationThresholdBytes">
    /// Maximum blob size (bytes) before the entire column page is externalized to sidecar files.
    /// Only meaningful when <see cref="DatumColumnFlags.ExternBlobs"/> is set.
    /// </param>
    public DatumColumnDescriptor(
        string name,
        DataKind kind,
        DatumColumnFlags flags = DatumColumnFlags.None,
        int[]? fixedShape = null,
        long externalizationThresholdBytes = DatumFileConstants.DefaultExternalizationThresholdBytes)
    {
        Name = name;
        Kind = kind;
        Flags = flags;
        FixedShape = fixedShape;
        ExternalizationThresholdBytes = externalizationThresholdBytes;
    }

    /// <summary>The column name as it appears in query expressions.</summary>
    public string Name { get; init; }

    /// <summary>The data kind of values stored in this column.</summary>
    public DataKind Kind { get; init; }

    /// <summary>Bitfield of column-level properties.</summary>
    public DatumColumnFlags Flags { get; init; }

    /// <summary>
    /// Fixed shape dimensions for Vector, Matrix, or Tensor columns; <c>null</c> for all other kinds.
    /// For Vector this is a single-element array <c>[D]</c>; for Matrix <c>[rows, cols]</c>;
    /// for Tensor <c>[d0, d1, ..., dN-1]</c>.
    /// </summary>
    public int[]? FixedShape { get; init; }

    /// <summary>
    /// Blob externalization threshold in bytes. Only consulted when
    /// <see cref="DatumColumnFlags.ExternBlobs"/> is set in <see cref="Flags"/>.
    /// </summary>
    public long ExternalizationThresholdBytes { get; init; }

    /// <summary>Returns <c>true</c> if this column may contain null values.</summary>
    public bool IsNullable => (Flags & DatumColumnFlags.Nullable) != 0;

    /// <summary>Returns <c>true</c> if this column has a recorded fixed shape.</summary>
    public bool HasFixedShape => (Flags & DatumColumnFlags.FixedShape) != 0;

    /// <summary>Returns <c>true</c> if this column is eligible for dictionary encoding.</summary>
    public bool IsDictionaryEligible => (Flags & DatumColumnFlags.DictionaryEligible) != 0;

    /// <summary>Returns <c>true</c> if oversized blobs in this column are externalized to sidecar files.</summary>
    public bool ExternalizesBlobs => (Flags & DatumColumnFlags.ExternBlobs) != 0;

    /// <summary>
    /// Computes the total number of float elements per row for a fixed-shape float column.
    /// Returns 1 for Scalar, the vector length for Vector, and the product of all dimensions
    /// for Matrix and Tensor. Returns 0 if <see cref="FixedShape"/> is not yet populated.
    /// </summary>
    public int ElementsPerRow()
    {
        if (Kind == DataKind.Scalar || Kind == DataKind.UInt8)
        {
            return 1;
        }

        if (FixedShape is null || FixedShape.Length == 0)
        {
            return 0;
        }

        int product = 1;

        foreach (int dimension in FixedShape)
        {
            product *= dimension;
        }

        return product;
    }
}
