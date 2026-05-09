using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// Format handler for newline-delimited JSON. Matches <c>.jsonl</c> and
/// <c>.ndjson</c> extensions — both common in the wild (HuggingFace datasets
/// ship <c>.jsonl</c>; logging tooling tends to <c>.ndjson</c>). Each non-empty
/// line is one independent JSON value; <see cref="JsonLinesDeserializer"/>
/// handles the streaming and shape inference.
/// </summary>
public sealed class JsonLinesFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "jsonl";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        if (ext.Equals(".jsonl", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ndjson", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new JsonLinesDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }
}
