namespace DatumIngest.Web.ModelLibrary;

public interface IModelDownloadService
{
    // Returns whether the model's expected files are present locally with
    // the right sizes. "Right sizes" means matching the HF tree API's
    // reported size â€” a fast local-only check. Full sha256 re-verification
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

    // Best-effort: deletes the model's directory on disk.
    Task UninstallAsync(string modelId, CancellationToken ct = default);

    // Bulk probe: returns the install state for every model in the manifest.
    // Cheaper for the Models view than N individual ProbeAsync calls; same
    // result. Future ship-quality version may cache, but the file-system
    // probe is fast enough that bulk == "loop and probe."
    Task<IReadOnlyDictionary<string, ModelInstallState>> ProbeAllAsync(CancellationToken ct = default);
}

public enum ModelInstallState
{
    NotInstalled,
    Partial,     // some expected files present, others missing
    Installed,
}
