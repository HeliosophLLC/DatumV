using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Model;
using PureHDF;

namespace Heliosoph.DatumV.Serialization.Hdf5;

/// <summary>
/// Deserializes HDF5 files into <see cref="RowBatch"/> streams for the
/// generic ingest pipeline. Yields one row per group / dataset with
/// the same shape <c>open_h5_meta</c> produces — the most informative
/// default for an unknown HDF5 file. Recipes that want dataset rows
/// directly should use <c>open_h5_dataset</c> inside an SQL recipe.
/// </summary>
public sealed class Hdf5Deserializer : IFormatDeserializer
{
    private const int DefaultBatchSize = 256;

    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "path", "kind", "element_kind", "dimensions",
        "is_scalar", "is_supported", "attribute_count", "attributes",
    ]);

    private readonly FileFormatDescriptor _descriptor;

    /// <summary>Creates a deserializer for the given file descriptor.</summary>
    public Hdf5Deserializer(FileFormatDescriptor descriptor)
    {
        _descriptor = descriptor;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<RowBatch> DeserializeAsync(
        SerializationContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // PureHDF needs a path on disk (it mmap's or seeks into the file).
        // FileFormatDescriptor.OpenAsync returns a stream we'd have to copy
        // to a temp file anyway for the mmap path. Skip the round-trip by
        // requiring FilePath up front — gzipped HDF5 files (uncommon since
        // HDF5 has its own compression) would need a temp materialisation;
        // we don't support that in v1.
        if (!File.Exists(_descriptor.FilePath))
        {
            throw new InvalidDataException(
                $"Hdf5Deserializer needs a direct file path; got '{_descriptor.FilePath}' which doesn't exist.");
        }

        await Task.Yield();
        using var file = H5File.OpenRead(_descriptor.FilePath);

        RowBatch? batch = null;
        foreach (Hdf5ObjectDescriptor obj in Hdf5ObjectWalker.Walk(file))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= context.Pool.RentRowBatch(OutputColumnLookup, DefaultBatchSize);
            DataValue[] row = BuildRow(obj, batch.Arena, context);
            batch.Add(row);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    private static DataValue[] BuildRow(
        Hdf5ObjectDescriptor obj,
        IValueStore arena,
        SerializationContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);

        row[0] = DataValue.FromString(obj.Path, arena);
        row[1] = DataValue.FromString(KindToString(obj.Kind), arena);

        if (obj.DatasetType is { } dt)
        {
            row[2] = DataValue.FromString(dt.IsSupported ? dt.ElementKind.ToString() : "Unknown", arena);
            long[] dims = new long[dt.Dimensions.Count];
            for (int i = 0; i < dims.Length; i++) dims[i] = checked((long)dt.Dimensions[i]);
            row[3] = DataValue.FromArenaArray<long>(dims, DataKind.Int64, arena);
            row[4] = DataValue.FromBoolean(dt.IsScalar);
            row[5] = DataValue.FromBoolean(dt.IsSupported);
        }
        else
        {
            row[2] = DataValue.Null(DataKind.String);
            row[3] = DataValue.NullArrayOf(DataKind.Int64);
            row[4] = DataValue.FromBoolean(false);
            row[5] = DataValue.FromBoolean(true);
        }

        row[6] = DataValue.FromInt32(obj.Attributes.Count);
        row[7] = DataValue.FromJson(Hdf5AttributesJson.Build(obj.Attributes), arena);
        return row;
    }

    private static string KindToString(Hdf5ObjectKind kind) =>
        kind switch
        {
            Hdf5ObjectKind.Group => "group",
            Hdf5ObjectKind.Dataset => "dataset",
            _ => "unknown",
        };
}
