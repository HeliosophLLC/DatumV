namespace DatumIngest.Serialization;

/// <summary>
/// Registry of file format handlers. Iterates registered <see cref="IFileFormat"/>
/// instances to detect the format of a file and create a deserializer for it.
/// </summary>
/// <remarks>
/// Not a singleton — create a new instance per context to keep tests isolated.
/// Register <see cref="IFileFormat"/> implementations via DI and inject
/// <c>IEnumerable&lt;IFileFormat&gt;</c> into the constructor.
/// </remarks>
public sealed class FormatRegistry
{
    private readonly List<IFileFormat> _formats = [];

    /// <summary>
    /// Initializes a new <see cref="FormatRegistry"/> with the given formats.
    /// </summary>
    /// <param name="formats">The file formats to register.</param>
    public FormatRegistry(IEnumerable<IFileFormat> formats)
    {
        _formats.AddRange(formats);
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
}