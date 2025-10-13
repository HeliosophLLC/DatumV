namespace DatumIngest.Serialization;

/// <summary>
/// Registry of file format handlers. Iterates registered <see cref="IFileFormat"/>
/// instances to detect the format of a file and create a deserializer for it.
/// </summary>
/// <remarks>
/// Not a singleton — create a new instance per context to keep tests isolated.
/// Use <see cref="CreateDefault"/> for production with all built-in formats.
/// </remarks>
public sealed class FormatRegistry
{
    private readonly List<IFileFormat> _formats = [];

    /// <summary>Registers a format handler.</summary>
    /// <param name="format">The format to register.</param>
    public void Register(IFileFormat format)
    {
        _formats.Add(format);
    }

    /// <summary>
    /// Detects the format of the given file and returns a deserializer for it.
    /// </summary>
    /// <param name="descriptor">The source file descriptor.</param>
    /// <returns>A deserializer for the detected format.</returns>
    /// <exception cref="NotSupportedException">
    /// Thrown when no registered format can handle the file.
    /// </exception>
    public IFormatDeserializer CreateDeserializer(FileFormatDescriptor descriptor)
    {
        foreach (IFileFormat format in _formats)
        {
            if (format.CanHandle(descriptor, out IFormatDeserializer? deserializer))
                return deserializer;
        }

        string extension = Path.GetExtension(descriptor.FilePath);
        throw new NotSupportedException(
            $"No format handler registered for '{descriptor.FilePath}' (extension: '{extension}'). " +
            $"Registered formats: {string.Join(", ", _formats.Select(f => f.Name))}.");
    }

    /// <summary>
    /// Creates a registry pre-populated with all built-in format handlers.
    /// </summary>
    public static FormatRegistry CreateDefault()
    {
        FormatRegistry registry = new();
        registry.Register(new Csv.CsvFileFormat());
        registry.Register(new Jsonl.JsonlFileFormat());
        registry.Register(new Parquet.ParquetFileFormat());
        registry.Register(new Hdf5.Hdf5FileFormat());
        registry.Register(new Idx.IdxFileFormat());
        registry.Register(new Zip.ZipFileFormat());
        return registry;
    }
}
