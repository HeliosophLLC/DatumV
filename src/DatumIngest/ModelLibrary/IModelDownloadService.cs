// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

public interface IModelDownloadService
{
    // Returns whether the model's expected files are present locally with
    // the right sizes. "Right sizes" means matching the HF tree API's
    // reported size — a fast local-only check. Full sha256 re-verification
    // is not done here; that would re-read every byte every time the UI
    // refreshes the model list.
    Task<ModelInstallState> ProbeAsync(string modelId, CancellationToken ct = default);

    // Kicks off the download. Returns once the download is queued; progress
    // is pushed asynchronously over SignalR (IStreamHubClient.OnModelDownload*).
    // Throws if:
    //   - modelId is unknown
    //   - the model is a placeholder (Source.Repo not yet uploaded)
    //   - one of the model's licenses is requiresAcceptance and not yet
    //     accepted
    Task InstallAsync(string modelId, CancellationToken ct = default);

    // Installs a specific catalog version alongside whatever's currently
    // active. Downloads the version's files into `<id>/<version>/`, then
    // runs the version's installSql in pinned mode — the installer
    // rewrites `CREATE OR REPLACE MODEL <bare>` to the suffixed pinnedAs
    // form so the registered identifier resolves from
    // `models.<bare>@<digits>` SQL syntax. Does NOT flip the `<id>/active`
    // pointer; the active install keeps serving bare `models.<bare>`
    // references unchanged.
    Task InstallPinnedAsync(string modelId, string version, CancellationToken ct = default);

    // Best-effort: deletes the model's directory on disk.
    Task UninstallAsync(string modelId, CancellationToken ct = default);

    // Flips the active version to a different on-disk version. Runs the
    // memo's version-switch flow: DROPs the bare identifiers from the
    // outgoing version that aren't in the incoming version's declared
    // identifier set (the "leavers"), re-runs the incoming version's
    // installSql in bare mode (CREATE OR REPLACE updates shared
    // identifiers in place; new identifiers register fresh), cross-checks
    // observed vs declared, and flips `<id>/active` last. Lazy disposal
    // handles in-flight queries holding leases on the dropped identifiers.
    // Throws if:
    //   - modelId / version is unknown
    //   - the version's folder isn't on disk
    //   - cross-check finds a declared/observed mismatch (rare; would
    //     indicate authoring drift between catalog JSON and installSql)
    Task ActivateVersionAsync(string modelId, string version, CancellationToken ct = default);

    // Removes a single on-disk version's folder + the identifiers that
    // version registered. Active-version delete also clears the
    // `<id>/active` pointer (no auto-flip to a sibling). Pinned-version
    // delete DROPs the suffixed `<bare>@<digits>` identifiers; bare
    // delete DROPs the bare identifiers. Idempotent — deleting a version
    // that isn't on disk is a no-op.
    Task DeleteVersionAsync(string modelId, string version, CancellationToken ct = default);

    // Bulk probe: returns the install state for every model in the manifest.
    // Cheaper for the Models view than N individual ProbeAsync calls; same
    // result. Future ship-quality version may cache, but the file-system
    // probe is fast enough that bulk == "loop and probe."
    Task<IReadOnlyDictionary<string, ModelInstallState>> ProbeAllAsync(CancellationToken ct = default);

    // Total bytes in any `<file>.part` files inside the model's directory.
    // Used by the UI to surface a Resume affordance when an interrupted
    // download left bytes on disk. Zero means "no partials" (or model dir
    // doesn't exist). Filesystem-only; no HF tree call.
    Task<long> GetPartialBytesAsync(string modelId, CancellationToken ct = default);

    // Bulk variant. Returns one entry per model that has any partial bytes;
    // models with none are omitted (UI can default to zero on miss).
    Task<IReadOnlyDictionary<string, long>> GetAllPartialBytesAsync(CancellationToken ct = default);

    // Deletes all `*.part` files inside the model's directory. Used by the
    // UI's "Restart" affordance when the user wants to wipe partial bytes
    // and start fresh. Does not touch completed files — uninstall does that.
    Task DeletePartialsAsync(string modelId, CancellationToken ct = default);
}

// Per-model lifecycle state surfaced to the UI. The transitions are:
//   NotDownloaded -> (download starts) -> Partial -> Downloaded
//   Downloaded    -> (installSql runs) -> Installed
// For catalog entries with installSql == null the Downloaded state is
// terminal and the UI treats it as "ready"; entries with installSql
// require the installer to register the SQL-defined model into the
// catalog's ModelRegistry before they're considered Installed.
public enum ModelInstallState
{
    NotDownloaded,
    Partial,     // some expected files present, others missing
    Downloaded,  // all files present; either no installSql, or installSql not yet run
    Installed,   // for entries with installSql: SQL has been executed and registered
}
