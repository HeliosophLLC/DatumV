using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Web.Hosting;
using Heliosoph.DatumV.Web.Settings;

namespace Heliosoph.DatumV.Web.DatasetLibrary;

// IKeepRawDownloadsPolicy that reads the user's setting from settings.json
// at the catalog root on each call. Each dataset install re-reads the
// file, so a flip in the Settings UI applies on the very next install
// without a restart. Defaults to `Ask` when the file is missing or the
// field is unset / unparseable.
internal sealed class SettingsBackedKeepRawDownloadsPolicy : IKeepRawDownloadsPolicy
{
    private readonly string _catalogRootPath;

    public SettingsBackedKeepRawDownloadsPolicy(WebHostOptions options)
    {
        // CatalogRootPath is required on the desktop host; the Web host
        // refuses to start without it when ManageLocalCatalog=true, which
        // is the only configuration that wires this policy.
        _catalogRootPath = options.CatalogRootPath
            ?? throw new InvalidOperationException(
                $"{nameof(SettingsBackedKeepRawDownloadsPolicy)} requires "
                + $"{nameof(WebHostOptions.CatalogRootPath)} to be set.");
    }

    public ValueTask<KeepRawDownloadsMode> GetAsync(CancellationToken ct)
        => ValueTask.FromResult(StartupSettingsLoader.LoadKeepRawDownloads(_catalogRootPath));
}
