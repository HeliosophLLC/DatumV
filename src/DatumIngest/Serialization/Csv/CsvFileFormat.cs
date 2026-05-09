using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Format handler for CSV and TSV files. Matches <c>.csv</c> and <c>.tsv</c> extensions.
/// </summary>
public sealed class CsvFileFormat : IFileFormat
{
    /// <inheritdoc />
    public string Name => "csv";

    /// <inheritdoc />
    public bool CanHandle(
        FileFormatDescriptor descriptor,
        [NotNullWhen(true)] out IFormatDeserializer? deserializer)
    {
        string ext = descriptor.LogicalExtension;
        if (ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
        {
            deserializer = new CsvDeserializer(descriptor);
            return true;
        }

        deserializer = null;
        return false;
    }
}
