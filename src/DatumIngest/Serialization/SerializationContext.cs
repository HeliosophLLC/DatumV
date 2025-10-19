using DatumIngest.Pooling;
using DatumIngest.Model;

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
    public SerializationContext(Pool pool, int batchByteTarget = 16 * 1024 * 1024)
    {
        Pool = pool;
        BatchByteTarget = batchByteTarget;
    }

    /// <summary>Pool for renting <see cref="DataValue"/> arrays and <see cref="RowBatch"/> instances.</summary>
    public Pool Pool { get; }

    /// <summary>
    /// Advisory target bytes per batch. Honoured by deserializers that accumulate
    /// variable-size payloads (e.g. <c>ZipDeserializer</c>); others ignore it.
    /// </summary>
    public int BatchByteTarget { get; }
}
