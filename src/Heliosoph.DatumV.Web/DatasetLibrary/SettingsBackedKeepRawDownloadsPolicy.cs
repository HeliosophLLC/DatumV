using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Web.Hosting;
using Heliosoph.DatumV.Web.Settings;

namespace Heliosoph.DatumV.Web.DatasetLibrary;

// IKeepRawDownloadsPolicy that reads the user's setting from settings.json
// under the global data path on each call. Each dataset install re-reads
// the file, so a flip in the Settings UI applies on the very next install
// without a restart. Defaults to `Ask` when the file is missing or the
// field is unset / unparseable.
internal sealed class SettingsBackedKeepRawDownloadsPolicy(WebHostOptions options) : IKeepRawDownloadsPolicy
{
    public ValueTask<KeepRawDownloadsMode> GetAsync(CancellationToken ct)
        => ValueTask.FromResult(StartupSettingsLoader.LoadKeepRawDownloads(options));
}
