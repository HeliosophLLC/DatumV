using DatumIngest.DatumFile.Sidecar;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Serialization;

/// <summary>
/// Provides shared resources for format serialization and deserialization: a
/// <see cref="Pool"/> for renting <see cref="DataValue"/> arrays and
/// <see cref="RowBatch"/> instances, and an <see cref="Arena"/> for storing
/// reference-type payloads (strings, byte blobs) without ambient state.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Arena"/> is reset between batch yields by the deserializer,
/// bounding memory to one batch worth of string data. Callers must fully consume
/// each <see cref="RowBatch"/> before pulling the next from the deserializer's
/// <see cref="IAsyncEnumerable{T}"/>.
/// </para>
/// </remarks>
public sealed class SerializationContext
{
    /// <summary>
    /// Creates a new <see cref="SerializationContext"/> with the given pool.
    /// </summary>
    /// <param name="pool">The pool for renting DataValue arrays and RowBatch instances.</param>
    /// <param name="batchByteTarget">
    /// Advisory target bytes per batch. Deserializers that honour it flush a batch once
    /// its arena reaches this size. Ignored by schema-driven formats that batch by rows.
    /// Defaults to 16 MB.
    /// </param>
    /// <param name="lboStore">
    /// Optional sink for routing Large Binary Objects (images, byte arrays, future
    /// video, etc.) out of the batch's transient arena and into a long-lived destination
    /// such as a <c>.datum-blob</c> sidecar. Deserializers that opt in call
    /// <c>lboStore.Append(...)</c> for binary column values and embed the returned
    /// 64-bit <c>(offset, length)</c> in the resulting <see cref="DataValue"/>;
    /// deserializers that don't (or when this is <see langword="null"/>) keep writing
    /// binary payloads to <c>batch.Arena</c> as before.
    /// </param>
    public SerializationContext(Pool pool, int batchByteTarget = 16 * 1024 * 1024, IBlobSink? lboStore = null)
    {
        Pool = pool;
        BatchByteTarget = batchByteTarget;
        LboStore = lboStore;
    }

    /// <summary>Pool for renting <see cref="DataValue"/> arrays and <see cref="RowBatch"/> instances.</summary>
    public Pool Pool { get; }

    /// <summary>
    /// Advisory target bytes per batch. Honoured by deserializers that accumulate
    /// variable-size payloads (e.g. <c>ZipDeserializer</c>); others ignore it.
    /// </summary>
    public int BatchByteTarget { get; }

    /// <summary>
    /// Optional Large Binary Object sink. When non-<see langword="null"/>, deserializers
    /// that opt in route binary column payloads (images, byte arrays, etc.) here instead
    /// of into the batch's per-batch <see cref="Arena"/>. The resulting
    /// <see cref="DataValue"/> carries the sink's returned 64-bit <c>(offset, length)</c>
    /// so downstream stages reference the bytes without copying. The ingester sets this
    /// to the writer's sidecar store; tests and programmatic-API callers can leave it
    /// <see langword="null"/> to keep the legacy single-arena behaviour.
    /// </summary>
    public IBlobSink? LboStore { get; }
}
