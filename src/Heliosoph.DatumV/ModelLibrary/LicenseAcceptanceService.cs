// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

using System.Text.Json;


namespace Heliosoph.DatumV.ModelLibrary;

internal sealed class LicenseAcceptanceService : ILicenseAcceptanceService
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private HashSet<string>? _cache;

    public LicenseAcceptanceService(ModelLibraryOptions options)
    {
        _path = Path.Combine(options.CatalogRootPath, "license-acceptance.json");
    }

    public async Task<bool> IsAcceptedAsync(string licenseId, CancellationToken ct = default)
    {
        HashSet<string> accepted = await LoadAsync(ct).ConfigureAwait(false);
        return accepted.Contains(licenseId);
    }

    public async Task AcceptAsync(string licenseId, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            HashSet<string> accepted = await LoadAsyncUnlocked(ct).ConfigureAwait(false);
            if (accepted.Add(licenseId))
            {
                await SaveAsyncUnlocked(accepted, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken ct = default)
    {
        HashSet<string> accepted = await LoadAsync(ct).ConfigureAwait(false);
        return accepted.ToArray();
    }

    private async Task<HashSet<string>> LoadAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { return await LoadAsyncUnlocked(ct).ConfigureAwait(false); }
        finally { _lock.Release(); }
    }

    private async Task<HashSet<string>> LoadAsyncUnlocked(CancellationToken ct)
    {
        if (_cache is not null) return _cache;

        if (!File.Exists(_path))
        {
            _cache = new HashSet<string>(StringComparer.Ordinal);
            return _cache;
        }

        await using FileStream stream = File.OpenRead(_path);
        string[]? ids = await JsonSerializer.DeserializeAsync<string[]>(stream, _json, ct)
            .ConfigureAwait(false);
        _cache = new HashSet<string>(ids ?? Array.Empty<string>(), StringComparer.Ordinal);
        return _cache;
    }

    private async Task SaveAsyncUnlocked(HashSet<string> accepted, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        string tempPath = _path + ".tmp";
        await using (FileStream stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, accepted.ToArray(), _json, ct)
                .ConfigureAwait(false);
        }
        File.Move(tempPath, _path, overwrite: true);
        _cache = accepted;
    }

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };
}
