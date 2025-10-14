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
    /// Creates a new <see cref="SerializationContext"/> with the given pool and
    /// a fresh <see cref="Arena"/>.
    /// </summary>
    /// <param name="pool">The pool for renting DataValue arrays and RowBatch instances.</param>
    public SerializationContext(Pool pool)
    {
        Pool = pool;
    }

    /// <summary>Pool for renting <see cref="DataValue"/> arrays and <see cref="RowBatch"/> instances.</summary>
    public Pool Pool { get; }

}
