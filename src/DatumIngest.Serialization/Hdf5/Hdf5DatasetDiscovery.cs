using PureHDF;
using PureHDF.VOL.Native;

namespace DatumIngest.Serialization.Hdf5;

/// <summary>
/// Holds the flattened path and reference to a discovered HDF5 dataset.
/// </summary>
internal sealed record Hdf5DatasetEntry(string Path, IH5Dataset Dataset);

/// <summary>
/// Recursively discovers all datasets in an HDF5 file, building flattened
/// slash-separated paths (e.g. "sensors/temperature").
/// </summary>
internal static class Hdf5DatasetDiscovery
{
    internal static List<Hdf5DatasetEntry> Discover(NativeFile file)
    {
        List<Hdf5DatasetEntry> entries = new();
        DiscoverRecursive(file, "", entries);
        return entries;
    }

    private static void DiscoverRecursive(IH5Group group, string parentPath, List<Hdf5DatasetEntry> results)
    {
        foreach (IH5Object child in group.Children())
        {
            string fullPath = parentPath.Length == 0
                ? child.Name
                : $"{parentPath}/{child.Name}";

            if (child is IH5Dataset dataset)
                results.Add(new Hdf5DatasetEntry(fullPath, dataset));
            else if (child is IH5Group childGroup)
                DiscoverRecursive(childGroup, fullPath, results);
        }
    }
}
