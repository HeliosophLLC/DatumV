using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;

namespace Heliosoph.DatumV.Tests.Serialization.Hdf5;

/// <summary>
/// Integration tests for <see cref="Hdf5ObjectWalker"/> + the typed
/// metadata accessors it produces. Builds tiny HDF5 fixtures with
/// PureHDF and verifies the walker yields the expected tree of
/// <see cref="Hdf5ObjectDescriptor"/>s with correctly-mapped element
/// kinds, dimensions, and attribute values.
/// </summary>
public sealed class Hdf5ObjectWalkerTests : IDisposable
{
    private readonly string _scratchDir = Path.Combine(
        Path.GetTempPath(), $"h5-walker-{Guid.NewGuid():N}");

    public Hdf5ObjectWalkerTests()
    {
        Directory.CreateDirectory(_scratchDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratchDir)) Directory.Delete(_scratchDir, recursive: true); }
        catch { /* best-effort */ }
    }

    private string TempH5(string name) => Path.Combine(_scratchDir, name);

    // ───────────────────── Tree walk ─────────────────────

    [Fact]
    public void Walk_FlatRoot_YieldsRootGroupPlusDatasets()
    {
        string path = TempH5("flat.h5");
        H5File writer = new()
        {
            ["values"] = new int[] { 1, 2, 3 },
            ["weights"] = new float[] { 0.5f, 1.5f },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        List<Hdf5ObjectDescriptor> walked = [.. Hdf5ObjectWalker.Walk(file)];

        Assert.Equal(3, walked.Count);
        Assert.Equal("/", walked[0].Path);
        Assert.Equal(Hdf5ObjectKind.Group, walked[0].Kind);

        Hdf5ObjectDescriptor values = walked.Single(o => o.Path == "/values");
        Assert.Equal(Hdf5ObjectKind.Dataset, values.Kind);
        Assert.Equal(DataKind.Int32, values.DatasetType!.Value.ElementKind);
        Assert.Equal(new ulong[] { 3 }, values.DatasetType!.Value.Dimensions);

        Hdf5ObjectDescriptor weights = walked.Single(o => o.Path == "/weights");
        Assert.Equal(DataKind.Float32, weights.DatasetType!.Value.ElementKind);
    }

    [Fact]
    public void Walk_NestedGroups_RecursesAndBuildsCorrectPaths()
    {
        string path = TempH5("nested.h5");
        H5File writer = new()
        {
            ["spectra"] = new H5Group
            {
                ["flux"] = new double[] { 1.0, 2.0 },
                ["wavelength"] = new double[] { 400.0, 500.0 },
            },
            ["metadata"] = new H5Group
            {
                ["instrument"] = new H5Group
                {
                    ["filter"] = new int[] { 770 },
                },
            },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        List<string> paths = [.. Hdf5ObjectWalker.Walk(file).Select(o => o.Path)];

        Assert.Contains("/", paths);
        Assert.Contains("/spectra", paths);
        Assert.Contains("/spectra/flux", paths);
        Assert.Contains("/spectra/wavelength", paths);
        Assert.Contains("/metadata", paths);
        Assert.Contains("/metadata/instrument", paths);
        Assert.Contains("/metadata/instrument/filter", paths);
    }

    // ───────────────────── Dimensions / scalar detection ─────────────────────

    [Fact]
    public void Walk_TwoDDataset_ReportsBothDimensions()
    {
        string path = TempH5("2d.h5");
        H5File writer = new()
        {
            ["image"] = new float[,]
            {
                { 1, 2, 3, 4 },
                { 5, 6, 7, 8 },
            },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        Hdf5ObjectDescriptor image = Hdf5ObjectWalker.Walk(file).Single(o => o.Path == "/image");
        Assert.Equal(new ulong[] { 2, 4 }, image.DatasetType!.Value.Dimensions);
        Assert.False(image.DatasetType!.Value.IsScalar);
        Assert.Equal(8ul, image.DatasetType!.Value.ElementCount);
    }

    // ───────────────────── dtype mapping ─────────────────────

    [Fact]
    public void Walk_SignedAndUnsignedIntegers_MapToDistinctDataKinds()
    {
        string path = TempH5("signs.h5");
        H5File writer = new()
        {
            ["i8"] = new sbyte[] { -1 },
            ["u8"] = new byte[] { 1 },
            ["i16"] = new short[] { -1 },
            ["u16"] = new ushort[] { 1 },
            ["i32"] = new int[] { -1 },
            ["u32"] = new uint[] { 1 },
            ["i64"] = new long[] { -1 },
            ["u64"] = new ulong[] { 1 },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        Dictionary<string, DataKind> byName = Hdf5ObjectWalker.Walk(file)
            .Where(o => o.Kind == Hdf5ObjectKind.Dataset)
            .ToDictionary(o => o.Path, o => o.DatasetType!.Value.ElementKind);

        Assert.Equal(DataKind.Int8, byName["/i8"]);
        Assert.Equal(DataKind.UInt8, byName["/u8"]);
        Assert.Equal(DataKind.Int16, byName["/i16"]);
        Assert.Equal(DataKind.UInt16, byName["/u16"]);
        Assert.Equal(DataKind.Int32, byName["/i32"]);
        Assert.Equal(DataKind.UInt32, byName["/u32"]);
        Assert.Equal(DataKind.Int64, byName["/i64"]);
        Assert.Equal(DataKind.UInt64, byName["/u64"]);
    }

    // ───────────────────── Attributes ─────────────────────

    [Fact]
    public void Walk_RootAttributes_ReadValuesIntoManagedPrimitives()
    {
        string path = TempH5("attrs.h5");
        H5File writer = new()
        {
            Attributes =
            {
                ["description"] = "carina-cluster",
                ["exposure_seconds"] = 1234.5,
                ["filter_count"] = 7,
            },
            ["data"] = new int[] { 0 },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        Hdf5ObjectDescriptor root = Hdf5ObjectWalker.Walk(file).First();

        Hdf5AttributeRecord description = root.Attributes.Single(a => a.Name == "description");
        Assert.Equal(DataKind.String, description.Type.ElementKind);
        Assert.Equal("carina-cluster", description.Value);

        Hdf5AttributeRecord exposure = root.Attributes.Single(a => a.Name == "exposure_seconds");
        Assert.Equal(DataKind.Float64, exposure.Type.ElementKind);
        Assert.Equal(1234.5, (double)exposure.Value!);

        Hdf5AttributeRecord filterCount = root.Attributes.Single(a => a.Name == "filter_count");
        Assert.Equal(DataKind.Int32, filterCount.Type.ElementKind);
        Assert.Equal(7, (int)filterCount.Value!);
    }

    [Fact]
    public void Walk_DatasetAttributes_AttachToTheDatasetNotTheRoot()
    {
        string path = TempH5("ds-attrs.h5");
        H5File writer = new()
        {
            ["flux"] = new H5Dataset(data: new double[] { 1.0, 2.0, 3.0 })
            {
                Attributes =
                {
                    ["units"] = "Jy",
                    ["bandpass"] = "F770W",
                },
            },
        };
        writer.Write(path);

        using var file = H5File.OpenRead(path);
        Hdf5ObjectDescriptor flux = Hdf5ObjectWalker.Walk(file).Single(o => o.Path == "/flux");
        Assert.Equal(2, flux.Attributes.Count);
        Assert.Contains(flux.Attributes, a => a.Name == "units" && (string)a.Value! == "Jy");
        Assert.Contains(flux.Attributes, a => a.Name == "bandpass" && (string)a.Value! == "F770W");
    }
}
