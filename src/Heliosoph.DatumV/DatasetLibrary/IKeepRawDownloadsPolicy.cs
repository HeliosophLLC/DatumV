// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace Heliosoph.DatumV.DatasetLibrary;

/// <summary>
/// Three-state preference for what happens to the raw archives in the
/// dataset cache after a successful ingest.
/// </summary>
public enum KeepRawDownloadsMode
{
    /// <summary>
    /// No remembered choice yet. Keeps the raw archives so the prompt
    /// shipped by a later release has files to act on, then writes the
    /// user's pick back to settings as <see cref="Always"/> or
    /// <see cref="Never"/>.
    /// </summary>
    Ask,

    /// <summary>
    /// Keep the raw archives indefinitely. Useful for re-ingest with
    /// different options, or for users who want to inspect the original
    /// source files.
    /// </summary>
    Always,

    /// <summary>
    /// Delete the raw archives immediately after every successful ingest.
    /// The ingested <c>.datum</c> + sidecar pair on the catalog side is
    /// self-sufficient — a re-install would re-download from the source.
    /// </summary>
    Never,
}

/// <summary>
/// Sink for the user's current <see cref="KeepRawDownloadsMode"/> setting.
/// Read by <see cref="DatasetDownloadService"/> after every successful
/// install to decide whether to wipe the version's raw-cache folder.
/// Implementations are responsible for resolving the current value from
/// wherever settings live (settings.json, in-memory, env var override).
/// </summary>
/// <remarks>
/// Reads happen on the install background task — implementations must be
/// safe to call without a scoped request context.
/// </remarks>
public interface IKeepRawDownloadsPolicy
{
    ValueTask<KeepRawDownloadsMode> GetAsync(CancellationToken ct);
}

/// <summary>
/// Default policy used by hosts that don't wire user settings — tests,
/// CLI consumers, the SaaS path that has no local settings file. Returns
/// <see cref="KeepRawDownloadsMode.Ask"/>, which preserves the raw cache
/// since no prompt or remembered choice is available.
/// </summary>
public sealed class DefaultKeepRawDownloadsPolicy : IKeepRawDownloadsPolicy
{
    public static DefaultKeepRawDownloadsPolicy Instance { get; } = new();
    private DefaultKeepRawDownloadsPolicy() { }
    public ValueTask<KeepRawDownloadsMode> GetAsync(CancellationToken ct)
        => ValueTask.FromResult(KeepRawDownloadsMode.Ask);
}
