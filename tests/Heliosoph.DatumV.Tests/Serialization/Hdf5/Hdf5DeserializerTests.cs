using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Serialization;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;

namespace Heliosoph.DatumV.Tests.Serialization.Hdf5;

/// <summary>
/// Integration tests for <see cref="Hdf5Deserializer"/>: the
/// ingest-path counterpart of the <c>open_h5_meta</c> TVF. Yields one
/// row per group / dataset with the same 8-column shape so
/// <c>datum ingest foo.h5</c> lands a queryable manifest into a .datum
/// file by default.
/// </summary>
public sealed class Hdf5DeserializerTests : ServiceTestBase, IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"h5-deserializer-{Guid.NewGuid():N}");

    public Hdf5DeserializerTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public new void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
        base.Dispose();
    }

    [Fact]
    public async Task Deserialize_NestedFile_YieldsOneRowPerObjectInTreeOrder()
    {
        string path = Path.Combine(_scratchDir, "nested.h5");
        new H5File
        {
            ["values"] = new int[] { 1, 2, 3 },
            ["spectra"] = new H5Group { ["flux"] = new double[] { 0.5 } },
        }.Write(path);

        FileFormatDescriptor descriptor = new(path);
        Hdf5Deserializer deserializer = new(descriptor);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);

        List<DataValue[]> rows = await CollectRows(deserializer, context);

        // / + /values + /spectra + /spectra/flux = 4 rows
        Assert.Equal(4, rows.Count);

        List<string> paths = [.. rows.Select(r => r[0].AsString())];
        Assert.Contains("/", paths);
        Assert.Contains("/values", paths);
        Assert.Contains("/spectra", paths);
        Assert.Contains("/spectra/flux", paths);
    }

    [Fact]
    public async Task Deserialize_RegisteredInFormatRegistry_OpensViaCanHandle()
    {
        string path = Path.Combine(_scratchDir, "registered.h5");
        new H5File { ["x"] = new int[] { 1 } }.Write(path);

        FileFormatDescriptor descriptor = new(path);

        FormatRegistry registry = new([new Hdf5FileFormat()]);
        IFormatDeserializer deserializer = registry.CreateDeserializer(descriptor);
        Assert.IsType<Hdf5Deserializer>(deserializer);

        Pool pool = CreatePool();
        SerializationContext context = new(pool);
        List<DataValue[]> rows = await CollectRows(deserializer, context);
        Assert.Equal(2, rows.Count); // root + /x
    }

    private static async Task<List<DataValue[]>> CollectRows(
        IFormatDeserializer deserializer,
        SerializationContext context)
    {
        List<DataValue[]> rows = [];
        await foreach (RowBatch batch in deserializer.DeserializeAsync(context))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row source = batch[i];
                DataValue[] stabilized = new DataValue[source.FieldCount];
                for (int f = 0; f < source.FieldCount; f++)
                {
                    stabilized[f] = DataValueRetention.Stabilize(source[f], batch.Arena, batch.Arena);
                }
                rows.Add(stabilized);
            }
            batch.Dispose();
        }
        return rows;
    }
}
