namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// The kind of HDF5 object surfaced by <see cref="Hdf5ObjectDescriptor"/>.
/// Committed datatypes are skipped in v1 — they're rare in practice and
/// don't carry data the row pipeline cares about.
/// </summary>
internal enum Hdf5ObjectKind
{
    /// <summary>A group node (directory-like container).</summary>
    Group,

    /// <summary>A dataset (typed N-dimensional array).</summary>
    Dataset,
}
