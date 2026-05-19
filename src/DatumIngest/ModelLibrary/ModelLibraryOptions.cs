// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// Filesystem paths the model library needs at runtime. Provided by the host
/// at DI registration time. Decouples the library from any specific host's
/// configuration object (WebHostOptions, CLI options, test fixtures).
/// </summary>
/// <param name="CatalogRootPath">
/// Directory under which the library stores per-user state: today the
/// license-acceptance JSON, future cached HF tree responses, download
/// telemetry, etc. Always set.
/// </param>
/// <param name="ModelsDirectory">
/// Directory where installed model files land, one subdirectory per model
/// id. Each model's directory mirrors the structure of its source repo's
/// included files.
/// </param>
public sealed record ModelLibraryOptions(
    string CatalogRootPath,
    string ModelsDirectory);
