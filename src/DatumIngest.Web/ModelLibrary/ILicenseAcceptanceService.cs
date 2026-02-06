namespace DatumIngest.Web.ModelLibrary;

// Tracks which licenses the user has explicitly accepted. Backed by a JSON
// file under the catalog root. Downloads of a model are gated on every one
// of the model's licenseIds being accepted (when license.requiresAcceptance
// is true).
public interface ILicenseAcceptanceService
{
    Task<bool> IsAcceptedAsync(string licenseId, CancellationToken ct = default);

    Task AcceptAsync(string licenseId, CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetAcceptedAsync(CancellationToken ct = default);
}
