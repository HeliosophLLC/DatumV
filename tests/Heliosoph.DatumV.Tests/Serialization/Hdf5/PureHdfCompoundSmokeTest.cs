using System.Runtime.InteropServices;
using PureHDF;

namespace Heliosoph.DatumV.Tests.Serialization.Hdf5;

/// <summary>
/// Smoke test: can we read an HDF5 compound dataset dynamically — without
/// a compile-time CLR struct — by reading raw bytes and decoding each
/// member ourselves? If this passes the compound-dtype reader for
/// <c>open_h5_dataset</c> is unblocked; if not we need a different
/// approach. Builds a tiny compound dataset with PureHDF's writer (which
/// itself needs a CLR struct, but that's fixture-only), reopens it, and
/// verifies the dataset's type metadata + raw byte layout matches a hand
/// decode.
/// </summary>
public sealed class PureHdfCompoundSmokeTest
{
    // Layout: int32 id + double mag = 12 bytes per element.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Source
    {
        public int Id;
        public double Mag;
    }

    [Fact]
    public void Compound_RawByteRead_CarriesMemberLayoutMetadata()
    {
        string path = Path.Combine(Path.GetTempPath(), $"compound-smoke-{Guid.NewGuid():N}.h5");
        try
        {
            Source[] data =
            [
                new Source { Id = 1, Mag = 19.5 },
                new Source { Id = 2, Mag = 20.25 },
                new Source { Id = 3, Mag = 18.75 },
            ];

            new H5File { ["catalog"] = data }.Write(path);

            using var file = H5File.OpenRead(path);
            IH5Dataset dataset = file.Dataset("catalog");

            Assert.Equal(H5DataTypeClass.Compound, dataset.Type.Class);
            ICompoundType compound = dataset.Type.Compound!;
            Assert.Equal(2, compound.Members.Length);

            CompoundMember idMember = compound.Members[0];
            CompoundMember magMember = compound.Members[1];
            Assert.Equal("Id", idMember.Name);
            Assert.Equal(0, idMember.Offset);
            Assert.Equal(H5DataTypeClass.FixedPoint, idMember.Type.Class);
            Assert.Equal(4, idMember.Type.Size);

            Assert.Equal("Mag", magMember.Name);
            Assert.Equal(H5DataTypeClass.FloatingPoint, magMember.Type.Class);
            Assert.Equal(8, magMember.Type.Size);

            int rowBytes = (int)dataset.Type.Size;
            Assert.Equal(3ul, dataset.Space.Dimensions[0]);

            // Raw byte read — the path we'll actually use for open_h5_dataset's
            // compound support. If this works we can decode each row's bytes
            // per CompoundMember.Offset + member type without ever needing a
            // CLR struct at runtime.
            byte[] raw = dataset.Read<byte[]>();
            Assert.Equal(3 * rowBytes, raw.Length);

            // Verify member values by hand-decoding row 1.
            int id1 = BitConverter.ToInt32(raw, rowBytes * 1 + idMember.Offset);
            double mag1 = BitConverter.ToDouble(raw, rowBytes * 1 + magMember.Offset);
            Assert.Equal(2, id1);
            Assert.Equal(20.25, mag1);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
