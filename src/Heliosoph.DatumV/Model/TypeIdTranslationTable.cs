namespace Heliosoph.DatumV.Model;

/// <summary>
/// Per-query, per-storeId translation table from a file's on-disk struct
/// type-ids to the running query's <see cref="TypeRegistry"/> ids.
/// Populated at file-open time by the source operator after deserializing
/// the file's footer type table; consumed by the sidecar slot-decoding
/// paths so <c>Array&lt;Struct&gt;</c> elements resolve to live registry
/// shapes regardless of which query is reading.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why per-query, not catalog-scoped.</strong> On-disk type-ids
/// are stable per file but runtime type-ids are per-<see cref="TypeRegistry"/>,
/// and registries are per-<see cref="Heliosoph.DatumV.Execution.ExecutionContext"/>.
/// A catalog-scoped translator would mix runtime ids from different queries
/// and resolve to descriptors the consuming query's registry doesn't carry.
/// The companion <see cref="DatumFile.Sidecar.SidecarRegistry"/> tracks
/// blob sources only and is correctly catalog-scoped — bytes don't change
/// between queries; type-ids do.
/// </para>
/// <para>
/// Lookup is O(1) and lock-free after registration. Two queries against
/// the same file each construct their own translation table from the same
/// on-disk type table — duplicate work, identical descriptor bytes, fresh
/// runtime ids per <see cref="TypeRegistry"/>.
/// </para>
/// </remarks>
public sealed class TypeIdTranslationTable
{
    private readonly IReadOnlyDictionary<ushort, ushort>?[] _translators =
        new IReadOnlyDictionary<ushort, ushort>?[256];

    /// <summary>
    /// Records the on-disk → runtime translation for the file backing
    /// <paramref name="storeId"/>. Idempotent: registering twice overwrites.
    /// Pass an empty dictionary when a file has no struct types — keeps
    /// lookup cost the same and lets callers register unconditionally.
    /// </summary>
    public void Register(byte storeId, IReadOnlyDictionary<ushort, ushort> translation)
    {
        ArgumentNullException.ThrowIfNull(translation);
        _translators[storeId] = translation;
    }

    /// <summary>
    /// Translates an on-disk struct type-id from the file behind
    /// <paramref name="storeId"/> to the corresponding runtime type-id in
    /// the query's <see cref="TypeRegistry"/>. Returns
    /// <paramref name="onDiskTypeId"/> unchanged when no translator is
    /// registered — covers in-memory values (where the slot byte already
    /// carries a runtime id) and v4 files written before the type-table
    /// format landed.
    /// </summary>
    public ushort Translate(byte storeId, ushort onDiskTypeId)
    {
        if (onDiskTypeId == 0) return 0;
        IReadOnlyDictionary<ushort, ushort>? translator = _translators[storeId];
        if (translator is null) return onDiskTypeId;
        return translator.TryGetValue(onDiskTypeId, out ushort runtimeId) ? runtimeId : onDiskTypeId;
    }
}
