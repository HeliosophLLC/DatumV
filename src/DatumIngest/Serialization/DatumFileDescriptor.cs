namespace DatumIngest.Serialization;

/// <summary>
/// Describes a <c>.datum</c> file as input to <see cref="DatumIngest.Ingestion.Indexer"/>.
/// Distinct from <see cref="FileFormatDescriptor"/>: this type only ever points at a
/// native column-store file, so no gzip handling, no temp-file materialization, and
/// no provider-specific options are exposed.
/// </summary>
public sealed class DatumFileDescriptor
{
    /// <summary>
    /// Creates a descriptor for the given <c>.datum</c> file path.
    /// </summary>
    /// <param name="filePath">Absolute or relative path to the <c>.datum</c> file.</param>
    public DatumFileDescriptor(string filePath)
    {
        FilePath = filePath;
    }

    /// <summary>The <c>.datum</c> file path.</summary>
    public string FilePath { get; }
}
