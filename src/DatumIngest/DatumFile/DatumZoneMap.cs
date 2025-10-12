using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.DatumFile;

/// <summary>
/// Per-column, per-row-group zone map: the minimum value, maximum value,
/// and null count observed during the write pass. Embedded in the file footer
/// so that column pages can be skipped without decompressing them.
/// </summary>
/// <remarks>
/// Zone maps are only populated for comparable types — Scalar, UInt8, Boolean,
/// String, Date, DateTime, Time, Duration, Uuid. Non-comparable types
/// (Vector, Matrix, Tensor, Image, UInt8Array, JsonValue, Array) carry only
/// <see cref="NullCount"/>; <see cref="Minimum"/> and <see cref="Maximum"/>
/// are <c>null</c> in that case.
/// </remarks>
public sealed record DatumZoneMap
{
    /// <summary>Creates a zone map with the given statistics.</summary>
    /// <param name="nullCount">Number of null values in this row group for this column.</param>
    /// <param name="minimum">Smallest observed value, or <c>null</c> if not applicable or not yet computed.</param>
    /// <param name="maximum">Largest observed value, or <c>null</c> if not applicable or not yet computed.</param>
    public DatumZoneMap(uint nullCount, DataValue? minimum, DataValue? maximum)
    {
        NullCount = nullCount;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Number of null values in this row group for this column.</summary>
    public uint NullCount { get; init; }

    /// <summary>Smallest observed value, or <c>null</c> if the type is non-comparable.</summary>
    public DataValue? Minimum { get; init; }

    /// <summary>Largest observed value, or <c>null</c> if the type is non-comparable.</summary>
    public DataValue? Maximum { get; init; }

    /// <summary>
    /// Returns <c>true</c> if both <see cref="Minimum"/> and <see cref="Maximum"/> are available,
    /// enabling range-based partition pruning.
    /// </summary>
    public bool HasMinMax => Minimum is not null && Maximum is not null;

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>Serializes this zone map using the same nullable DataValue wire format as <c>IndexWriter</c>.</summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(NullCount);
        writer.Write(HasMinMax);

        if (Minimum.HasValue && Maximum.HasValue)
        {
            IndexWriter.WriteDataValue(writer, Minimum.Value);
            IndexWriter.WriteDataValue(writer, Maximum.Value);
        }
    }

    /// <summary>Deserializes a zone map from the given binary reader.</summary>
    internal static DatumZoneMap Deserialize(BinaryReader reader, IValueStore? store = null)
    {
        uint nullCount = reader.ReadUInt32();
        bool hasMinMax = reader.ReadBoolean();

        if (!hasMinMax)
        {
            return new DatumZoneMap(nullCount, null, null);
        }

        DataValue minimum = store is not null
            ? IndexReader.ReadDataValue(reader, store)
            : IndexReader.ReadDataValue(reader);
        DataValue maximum = store is not null
            ? IndexReader.ReadDataValue(reader, store)
            : IndexReader.ReadDataValue(reader);
        return new DatumZoneMap(nullCount, minimum, maximum);
    }
}
