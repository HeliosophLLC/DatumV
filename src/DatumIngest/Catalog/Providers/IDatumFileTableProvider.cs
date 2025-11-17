using DatumIngest.DatumFile.Sidecar;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Common surface for v1 / v2 <c>.datum</c> table providers — exposes the
/// companion sidecar (when present) so <see cref="TableCatalog"/> can
/// register it with the catalog's <see cref="SidecarRegistry"/> at
/// provider-add time and stamp the assigned <c>storeId</c> back onto the
/// provider's decoder. Format-agnostic so the catalog doesn't need to
/// pattern-match on a specific reader version.
/// </summary>
public interface IDatumFileTableProvider
{
    /// <summary>
    /// Memory-mapped read view over the companion <c>.datum-blob</c>, or
    /// <see langword="null"/> when the file declares no sidecar.
    /// </summary>
    IBlobSource? Sidecar { get; }

    /// <summary>
    /// Sidecar <c>storeId</c> byte assigned by the catalog's
    /// <see cref="SidecarRegistry"/>. Set once at provider-add time;
    /// thereafter stable for the catalog's lifetime. Used by the
    /// provider's decoder to label sidecar-flagged DataValues so
    /// downstream accessors can resolve through the registry.
    /// </summary>
    byte SidecarStoreId { get; set; }
}
