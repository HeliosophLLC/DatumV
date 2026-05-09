using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Format handler for JSON files. Matches <c>.json</c> on
/// <see cref="FileFormatDescriptor.LogicalExtension"/> so gzipped sources
/// (<c>foo.json.gz</c>) are recognised through the same path.
/// </summary>
public sealed class JsonFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "json";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        if (ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new JsonDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }
}
