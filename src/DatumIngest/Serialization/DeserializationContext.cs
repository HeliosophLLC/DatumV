using DatumIngest.Execution.Pooling;
using DatumIngest.Model;

namespace DatumIngest.Serialization;

/// <summary>
/// Provides shared resources for format deserialization: a <see cref="Pool"/> for
/// renting <see cref="DataValue"/> arrays and <see cref="RowBatch"/> instances, and
/// an <see cref="Arena"/> for storing reference-type payloads (strings, byte blobs)
/// without <see cref="ReferenceStore"/> or <c>AsyncLocal</c> ambient state.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Arena"/> is reset between batch yields by the deserializer,
/// bounding memory to one batch worth of string data. Callers must fully consume
/// each <see cref="RowBatch"/> before pulling the next from the deserializer's
/// <see cref="IAsyncEnumerable{T}"/>.
/// </para>
/// </remarks>
public sealed class DeserializationContext : IDisposable
{
    /// <summary>
    /// Creates a new <see cref="DeserializationContext"/> with the given pool and
    /// a fresh <see cref="Arena"/>.
    /// </summary>
    /// <param name="pool">The pool for renting DataValue arrays and RowBatch instances.</param>
    public DeserializationContext(Pool pool)
    {
        Pool = pool;
        Arena = new Arena();
    }

    /// <summary>Pool for renting <see cref="DataValue"/> arrays and <see cref="RowBatch"/> instances.</summary>
    public Pool Pool { get; }

    /// <summary>Arena for storing string and binary payloads during deserialization.</summary>
    public Arena Arena { get; }

    /// <inheritdoc/>
    public void Dispose()
    {
        Arena.Dispose();
    }
}
