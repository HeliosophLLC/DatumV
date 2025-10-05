namespace DatumIngest.Model;

/// <summary>
/// Unified contract for storing and retrieving strings from a backing store.
/// Implemented by <see cref="ReferenceStore"/> (per-query object registry) and
/// <see cref="StringArena"/> (contiguous UTF-8 byte buffer).
/// </summary>
/// <remarks>
/// Each implementation is assigned a unique <see cref="StoreId"/> on construction
/// and registers itself in <see cref="StringStoreRegistry"/>. A <see cref="DataValue"/>
/// embeds the <see cref="StoreId"/> so that <see cref="DataValue.AsString()"/> can
/// resolve the correct store without ambient state.
/// </remarks>
public interface IStringStore
{
    /// <summary>
    /// Unique identifier for this store instance, embedded in <see cref="DataValue._storeId"/>.
    /// </summary>
    byte StoreId { get; }

    /// <summary>
    /// Stores a string value and returns two payload words to embed in a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="value">The string to store.</param>
    /// <returns>
    /// A pair of ints: for <see cref="ReferenceStore"/>, (index, 0);
    /// for <see cref="StringArena"/>, (offset, length).
    /// </returns>
    (int P0, int P1) Store(string value);

    /// <summary>
    /// Retrieves a previously stored string using the payload words from a <see cref="DataValue"/>.
    /// </summary>
    /// <param name="p0">First payload word (index or offset).</param>
    /// <param name="p1">Second payload word (unused or length).</param>
    /// <returns>The stored string.</returns>
    string Retrieve(int p0, int p1);
}

/// <summary>
/// Static registry that maps <see cref="IStringStore.StoreId"/> values to live
/// <see cref="IStringStore"/> instances. Used by <see cref="DataValue.AsString()"/>
/// to resolve the backing store from the embedded <c>_storeId</c> byte.
/// </summary>
internal static class StringStoreRegistry
{
    private static readonly IStringStore?[] _stores = new IStringStore?[256];
    private static readonly System.Collections.Concurrent.ConcurrentQueue<byte> _freeIds = new();

    static StringStoreRegistry()
    {
        // ID 0 is reserved (means "no store, use legacy AsyncLocal path").
        for (int i = 1; i < 256; i++)
            _freeIds.Enqueue((byte)i);
    }

    /// <summary>
    /// Allocates a unique <see cref="IStringStore.StoreId"/> and registers the store.
    /// </summary>
    /// <param name="store">The store to register.</param>
    /// <returns>The assigned store ID.</returns>
    internal static byte Register(IStringStore store)
    {
        if (!_freeIds.TryDequeue(out byte id))
            throw new InvalidOperationException(
                "All 255 StringStoreRegistry slots are in use. Ensure stores are disposed when no longer needed.");

        _stores[id] = store;
        return id;
    }

    /// <summary>
    /// Removes a store from the registry, allowing the slot to be reused.
    /// </summary>
    /// <param name="storeId">The ID previously returned by <see cref="Register"/>.</param>
    internal static void Deregister(byte storeId)
    {
        _stores[storeId] = null;
        _freeIds.Enqueue(storeId);
    }

    /// <summary>
    /// Resolves a store by its ID.
    /// </summary>
    /// <param name="storeId">The store ID embedded in a <see cref="DataValue"/>.</param>
    /// <returns>The registered store.</returns>
    internal static IStringStore Get(byte storeId) =>
        _stores[storeId] ?? throw new InvalidOperationException(
            $"No IStringStore registered for StoreId {storeId}. The store may have been disposed.");
}
