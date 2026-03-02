// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member
#pragma warning disable IL2026 // reflection-based JSON serialization will not survive trimming

namespace DatumIngest.ModelLibrary;

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
