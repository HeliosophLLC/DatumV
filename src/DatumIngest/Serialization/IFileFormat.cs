using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Serialization;

/// <summary>
/// Describes a file format that can be detected and deserialized. Each supported
/// format (CSV, Parquet, HDF5, etc.) implements this interface with its own
/// extension/magic-byte detection logic and deserializer construction.
/// </summary>
public interface IFileFormat
{
    /// <summary>The short name of this format (e.g. "csv", "parquet", "hdf5").</summary>
    string Name { get; }

    /// <summary>
    /// Tests whether this format can handle the given file descriptor.
    /// If the format matches, <paramref name="deserializer"/> is set to a ready-to-use
    /// deserializer instance; otherwise it is <see langword="null"/>.
    /// </summary>
    /// <param name="descriptor">The source file descriptor (path, options).</param>
    /// <param name="deserializer">
    /// When this method returns <see langword="true"/>, contains the deserializer
    /// for the matched format. Guaranteed non-null on success.
    /// </param>
    /// <returns><see langword="true"/> if this format can handle the file.</returns>
    bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer);
}
