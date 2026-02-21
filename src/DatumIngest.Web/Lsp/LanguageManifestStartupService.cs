using Microsoft.Extensions.Hosting;

namespace DatumIngest.Web.Lsp;

/// <summary>
/// Forces eager resolution of <see cref="LanguageManifestService"/> at
/// app startup. Without this, the singleton would lazily resolve on the
/// first LSP request — and any DDL that ran in the interim (e.g. via
/// startup migrations or a host-side seed script) would have fired its
/// catalog events into the void, leaving the manifest stale on first read.
/// </summary>
internal sealed class LanguageManifestStartupService(LanguageManifestService manifest) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Constructor side-effect — touching the property is enough to
        // confirm the singleton was instantiated.
        _ = manifest.CurrentManifest;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
