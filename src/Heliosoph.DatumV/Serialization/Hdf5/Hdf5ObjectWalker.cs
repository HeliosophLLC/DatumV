using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Walks an open HDF5 file's object tree depth-first, yielding one
/// <see cref="Hdf5ObjectDescriptor"/> per group and dataset including
/// the root. Used by both the <c>open_h5_meta</c> TVF (which surfaces
/// every record as a row) and the <c>open_h5_dataset</c> TVF (which
/// walks until it finds the target path).
/// </summary>
internal static class Hdf5ObjectWalker
{
    /// <summary>
    /// Yields the root descriptor then every descendant in depth-first,
    /// declaration order. The returned descriptors share the file's
    /// lifetime — they're invalid once the file is disposed.
    /// </summary>
    public static IEnumerable<Hdf5ObjectDescriptor> Walk(IH5Group root)
    {
        return WalkObject(root, path: "/");
    }

    private static IEnumerable<Hdf5ObjectDescriptor> WalkObject(IH5Object obj, string path)
    {
        // Skip committed datatype nodes — they don't carry data and we
        // don't surface them in v1. Datasets and groups are the only
        // kinds the row pipeline cares about.
        Hdf5ObjectKind? kind = obj switch
        {
            IH5Dataset => Hdf5ObjectKind.Dataset,
            IH5Group => Hdf5ObjectKind.Group,
            _ => null,
        };

        if (kind is null) yield break;

        Hdf5DatasetType? datasetType = obj is IH5Dataset ds
            ? Hdf5DatasetType.From(ds.Type, ds.Space)
            : null;

        IReadOnlyList<Hdf5AttributeRecord> attributes = ReadAttributes(obj);

        yield return new Hdf5ObjectDescriptor
        {
            Path = path,
            Kind = kind.Value,
            DatasetType = datasetType,
            Attributes = attributes,
            Handle = obj,
        };

        if (obj is IH5Group group)
        {
            foreach (IH5Object child in group.Children())
            {
                string childPath = path == "/" ? "/" + child.Name : path + "/" + child.Name;
                foreach (Hdf5ObjectDescriptor descendant in WalkObject(child, childPath))
                {
                    yield return descendant;
                }
            }
        }
    }

    private static IReadOnlyList<Hdf5AttributeRecord> ReadAttributes(IH5Object obj)
    {
        IEnumerable<IH5Attribute> raw = obj.Attributes();
        List<Hdf5AttributeRecord> result = [];
        foreach (IH5Attribute attribute in raw)
        {
            result.Add(Hdf5AttributeRecord.Read(attribute));
        }
        return result;
    }
}
