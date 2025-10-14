// using System.Diagnostics.CodeAnalysis;

// namespace DatumIngest.Serialization.Hdf5;

// /// <summary>
// /// Format handler for HDF5 files. Matches <c>.hdf5</c>, <c>.h5</c>, and <c>.hdf</c> extensions,
// /// and the 8-byte HDF5 magic signature <c>\x89HDF\r\n\x1a\n</c>.
// /// </summary>
// public sealed class Hdf5FileFormat : IFileFormat
// {
//     private static readonly byte[] Hdf5Magic = [0x89, 0x48, 0x44, 0x46, 0x0D, 0x0A, 0x1A, 0x0A];

//     /// <inheritdoc />
//     public string Name => "hdf5";

//     /// <inheritdoc />
//     public bool CanHandle(
//         FileFormatDescriptor descriptor,
//         [NotNullWhen(true)] out IFormatDeserializer? deserializer)
//     {
//         string ext = Path.GetExtension(descriptor.FilePath);
//         if (ext.Equals(".hdf5", StringComparison.OrdinalIgnoreCase) ||
//             ext.Equals(".h5", StringComparison.OrdinalIgnoreCase) ||
//             ext.Equals(".hdf", StringComparison.OrdinalIgnoreCase))
//         {
//             deserializer = new Hdf5Deserializer(descriptor);
//             return true;
//         }

//         // Fall back to magic byte detection.
//         if (File.Exists(descriptor.FilePath) && HasHdf5Magic(descriptor.FilePath))
//         {
//             deserializer = new Hdf5Deserializer(descriptor);
//             return true;
//         }

//         deserializer = null;
//         return false;
//     }

//     private static bool HasHdf5Magic(string filePath)
//     {
//         Span<byte> buffer = stackalloc byte[8];
//         using FileStream stream = new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
//         int read = stream.Read(buffer);
//         return read == 8 && buffer.SequenceEqual(Hdf5Magic);
//     }
// }
