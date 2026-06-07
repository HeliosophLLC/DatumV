using PureHDF;

namespace Heliosoph.DatumV.Tests.Serialization.Hdf5;

/// <summary>
/// Smoke test for PureHDF — the pure-managed HDF5 library already
/// referenced by the project. Builds a tiny .h5 file end-to-end (root
/// group + scalar attribute + 1-D Int32 dataset + 2-D Float32 dataset
/// + nested group with a dataset inside), reopens it, and reads back
/// every piece. If this passes, the entire HDF5 reader can be built on
/// PureHDF without P/Invoke or per-RID native binary distribution.
/// </summary>
public sealed class PureHdfSmokeTest
{
    [Fact]
    public void RoundTrip_RootAttribute_OneDIntDataset_TwoDFloatDataset_NestedGroup()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"purehdf-smoke-{Guid.NewGuid():N}.h5");

        try
        {
            WriteFixture(path);

            var file = H5File.OpenRead(path);

            // Root-level attribute round-trip — exercises the attribute
            // accessor path the open_h5_meta TVF will lean on.
            IH5Attribute description = file.Attribute("description");
            string descriptionValue = description.Read<string[]>()[0];
            Assert.Equal("smoke-test", descriptionValue);

            // 1-D Int32 dataset.
            IH5Dataset values = file.Dataset("values");
            int[] valuesData = values.Read<int[]>();
            Assert.Equal([10, 20, 30, 40, 50], valuesData);

            // 2-D Float32 dataset — flat read in row-major order.
            IH5Dataset matrix = file.Dataset("matrix");
            float[] matrixFlat = matrix.Read<float[]>();
            Assert.Equal(6, matrixFlat.Length);
            Assert.Equal(1.0f, matrixFlat[0]);
            Assert.Equal(2.0f, matrixFlat[1]);
            Assert.Equal(3.0f, matrixFlat[2]);
            Assert.Equal(4.0f, matrixFlat[3]);
            Assert.Equal(5.0f, matrixFlat[4]);
            Assert.Equal(6.0f, matrixFlat[5]);

            // Shape metadata available through Space.Dimensions — needed by
            // open_h5_dataset's plan-time schema peek.
            ulong[] dims = matrix.Space.Dimensions;
            Assert.Equal(new ulong[] { 2, 3 }, dims);

            // Nested group access — the tree walker that backs
            // open_h5_meta needs to descend into sub-groups.
            IH5Group spectra = file.Group("spectra");
            IH5Dataset flux = spectra.Dataset("flux");
            double[] fluxData = flux.Read<double[]>();
            Assert.Equal([1.5, 2.5, 3.5], fluxData);

            // Children enumeration — the manifest walker iterates this.
            List<string> rootChildren = [.. file.Children().Select(c => c.Name)];
            Assert.Contains("values", rootChildren);
            Assert.Contains("matrix", rootChildren);
            Assert.Contains("spectra", rootChildren);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Builds the fixture file with PureHDF's writer API — the same
    /// "programmatic binary fixture" pattern the FITS tests use, so we
    /// can ship test files without binary blobs in the repo.
    /// </summary>
    private static void WriteFixture(string path)
    {
        H5File file = new()
        {
            Attributes = { ["description"] = "smoke-test" },
            ["values"] = new int[] { 10, 20, 30, 40, 50 },
            ["matrix"] = new float[,] { { 1, 2, 3 }, { 4, 5, 6 } },
            ["spectra"] = new H5Group
            {
                ["flux"] = new double[] { 1.5, 2.5, 3.5 },
            },
        };
        file.Write(path);
    }
}
