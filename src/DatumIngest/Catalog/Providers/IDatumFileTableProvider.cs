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

    /// <summary>
    /// The catalog's <see cref="SidecarRegistry"/>, set at provider-add
    /// time so the provider can swap its registered <see cref="IBlobSource"/>
    /// after a mutation grows the underlying <c>.datum-blob</c>. Append
    /// extends the sidecar past the existing mmap's view; the provider
    /// reopens the mmap and calls <see cref="SidecarRegistry.UpdateAt"/>
    /// on the same <see cref="SidecarStoreId"/>.
    /// </summary>
    SidecarRegistry? SidecarRegistry { get; set; }

    /// <summary>
    /// Loads the file's struct type table (v5+) into the calling
    /// <paramref name="context"/>'s <c>Types</c> registry, builds the
    /// on-disk → runtime translation map, and registers it on the
    /// context's <c>TypeIdTranslations</c> table keyed by the provider's
    /// <see cref="SidecarStoreId"/>. No-op when the file carries no
    /// type table (v4 file, or v5 file with no Struct columns).
    /// Idempotent — safe to call once per scan; the translator just
    /// overwrites itself if invoked twice.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="Execution.Operators.ScanOperator"/> immediately
    /// before driving <see cref="ITableProvider.ScanAsync"/>, so any
    /// per-element TypeIds the scan reads from sidecar slot bytes resolve
    /// through the right translator for this query. The default
    /// implementation throws so format-aware providers must opt in.
    /// </remarks>
    void EnsureTypeTableLoaded(DatumIngest.Execution.ExecutionContext context);
}
