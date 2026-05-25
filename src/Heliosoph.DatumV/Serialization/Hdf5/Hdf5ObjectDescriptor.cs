using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Parsed metadata for one HDF5 object — a group or a dataset.
/// Holds the underlying <see cref="IH5Object"/> handle so that callers
/// (the <c>open_h5_dataset</c> TVF in particular) can perform typed
/// reads against the same open file without re-walking the tree.
/// </summary>
internal sealed class Hdf5ObjectDescriptor
{
    /// <summary>Full path inside the file, slash-separated (e.g. <c>/spectra/flux</c>). Root is <c>/</c>.</summary>
    public required string Path { get; init; }

    /// <summary>Group vs Dataset.</summary>
    public required Hdf5ObjectKind Kind { get; init; }

    /// <summary>Element type + shape for dataset objects; <c>null</c> for groups.</summary>
    public required Hdf5DatasetType? DatasetType { get; init; }

    /// <summary>Attributes attached directly to this object (groups and datasets both can carry them).</summary>
    public required IReadOnlyList<Hdf5AttributeRecord> Attributes { get; init; }

    /// <summary>Underlying PureHDF handle, valid only while the source <see cref="H5File"/> stays open.</summary>
    public required IH5Object Handle { get; init; }
}
