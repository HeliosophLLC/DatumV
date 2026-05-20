// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

namespace Heliosoph.DatumV.ModelLibrary;

/// <summary>
/// Read-only window into the centralized license registry (one
/// <c>licenses/index.json</c> + sibling text files at the repo root).
/// Both the model catalog and the dataset catalog reference licenses by
/// id; this service is the single source of truth for license metadata
/// + text. Acceptance state (per-user, per-id) lives on
/// <see cref="ILicenseAcceptanceService"/>, which is orthogonal — the
/// registry doesn't know what's been accepted, the acceptance service
/// doesn't know what the licenses say.
/// </summary>
public interface ILicenseRegistry
{
    /// <summary>Every declared license, indexed by id.</summary>
    IReadOnlyDictionary<string, CatalogLicense> All { get; }

    /// <summary>
    /// Returns the metadata block for <paramref name="licenseId"/>, or
    /// <see langword="null"/> when the id is unknown. Equivalent to
    /// <c>All.TryGetValue</c> with a non-throwing miss.
    /// </summary>
    CatalogLicense? GetMetadata(string licenseId);

    /// <summary>
    /// Returns the raw license text (markdown or plain text per the
    /// individual license) for <paramref name="licenseId"/>, or
    /// <see langword="null"/> when the id is unknown or the
    /// referenced <c>textFile</c> is missing on disk.
    /// </summary>
    string? GetText(string licenseId);
}
