// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.DatasetLibrary;

/// <summary>
/// Filesystem paths the dataset library needs at runtime. Provided by the
/// host at DI registration time. Decouples the library from any specific
/// host's configuration object (WebHostOptions, CLI options, test
/// fixtures).
/// </summary>
/// <param name="CatalogRootPath">
/// Directory under which the table catalog lives. Ingested datasets land
/// under <c>&lt;CatalogRootPath&gt;/datasets/</c> alongside the rest of
/// the catalog's persistent state. Always set.
/// </param>
/// <param name="DatasetsCacheDirectory">
/// Directory for raw archive downloads and their extracted trees. Separate
/// from <see cref="CatalogRootPath"/> because the contents are expendable
/// — the user's <c>keepRawDownloads</c> setting decides whether files
/// here survive a successful ingest. Defaults to
/// <c>%LOCALAPPDATA%/Heliosoph.DatumV/datasets-cache</c> when neither the
/// host config nor <c>$DATUMV_DATASETS</c> is set.
/// </param>
public sealed record DatasetLibraryOptions(
    string CatalogRootPath,
    string DatasetsCacheDirectory);
