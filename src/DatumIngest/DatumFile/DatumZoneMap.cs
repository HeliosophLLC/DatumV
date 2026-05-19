using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile;

/// <summary>
/// Per-column, per-row-group zone map: the minimum value, maximum value,
/// and null count observed during the write pass. Embedded in the file footer
/// so that column pages can be skipped without decompressing them.
/// </summary>
/// <remarks>
/// <para>
/// Zone maps are only populated for comparable types — scalar numerics,
/// <see cref="DataKind.Boolean"/>, <see cref="DataKind.String"/>, temporal types,
/// and <see cref="DataKind.Uuid"/>. Non-comparable types (Vector, Image, Array,
/// Struct, byte arrays) carry only <see cref="NullCount"/>;
/// <see cref="Minimum"/> and <see cref="Maximum"/> are <c>null</c> in that case.
/// </para>
/// <para>
/// <see cref="Minimum"/> and <see cref="Maximum"/> are managed-boxed primitives
/// (e.g. <see cref="long"/>, <see cref="double"/>, <see cref="string"/>) rather
/// than <see cref="DataValue"/> so the zone map has no arena dependency and
/// survives the writer's page-arena resets between row groups.
/// </para>
/// </remarks>
public sealed record DatumZoneMap
{
    /// <summary>Creates a zone map with no min/max (non-comparable type or all nulls).</summary>
    public DatumZoneMap(uint nullCount)
    {
        NullCount = nullCount;
        Kind = DataKind.Unknown;
        Minimum = null;
        Maximum = null;
    }

    /// <summary>Creates a zone map with the given managed min/max values.</summary>
    /// <param name="nullCount">Number of null values in this row group for this column.</param>
    /// <param name="kind">The <see cref="DataKind"/> of <paramref name="minimum"/> and <paramref name="maximum"/>.</param>
    /// <param name="minimum">Smallest observed value (managed-boxed primitive), or <c>null</c>.</param>
    /// <param name="maximum">Largest observed value (managed-boxed primitive), or <c>null</c>.</param>
    public DatumZoneMap(uint nullCount, DataKind kind, object? minimum, object? maximum)
    {
        NullCount = nullCount;
        Kind = kind;
        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Number of null values in this row group for this column.</summary>
    public uint NullCount { get; init; }

    /// <summary>The <see cref="DataKind"/> of <see cref="Minimum"/> and <see cref="Maximum"/>, or <see cref="DataKind.Unknown"/> if not populated.</summary>
    public DataKind Kind { get; init; }

    /// <summary>Smallest observed value (managed-boxed), or <c>null</c> if the type is non-comparable or no values were seen.</summary>
    public object? Minimum { get; init; }

    /// <summary>Largest observed value (managed-boxed), or <c>null</c> if the type is non-comparable or no values were seen.</summary>
    public object? Maximum { get; init; }

    /// <summary>Whether both <see cref="Minimum"/> and <see cref="Maximum"/> are available for range-based pruning.</summary>
    public bool HasMinMax => Minimum is not null && Maximum is not null;

    // ──────────────────── Binary serialization ────────────────────

    /// <summary>Serializes this zone map to the footer.</summary>
    internal void Serialize(BinaryWriter writer)
    {
        writer.Write(NullCount);
        writer.Write(HasMinMax);

        if (HasMinMax)
        {
            ZoneMapValueSerializer.Write(writer, Kind, Minimum!);
            ZoneMapValueSerializer.Write(writer, Kind, Maximum!);
        }
    }

    /// <summary>Deserializes a zone map from the footer.</summary>
    internal static DatumZoneMap Deserialize(BinaryReader reader)
    {
        uint nullCount = reader.ReadUInt32();
        bool hasMinMax = reader.ReadBoolean();

        if (!hasMinMax)
        {
            return new DatumZoneMap(nullCount);
        }

        object minimum = ZoneMapValueSerializer.Read(reader, out DataKind kind);
        object maximum = ZoneMapValueSerializer.Read(reader, out _);
        return new DatumZoneMap(nullCount, kind, minimum, maximum);
    }
}
