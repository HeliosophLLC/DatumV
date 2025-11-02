namespace DatumIngest.DatumFile.Sidecar;

/// <summary>
/// Per-query map from <c>storeId</c> byte to <see cref="IBlobSource"/>. Each
/// table provider that owns a sidecar registers it here at scan time and gets
/// back a byte; the decoder stamps that byte onto every sidecar-flagged
/// <see cref="Model.DataValue"/> it produces. At access time, image accessors
/// resolve the right blob source by reading the value's <c>storeId</c> and
/// looking it up here.
/// </summary>
/// <remarks>
/// <para>
/// Registry lifetime equals query lifetime — one registry per
/// <see cref="Execution.ExecutionContext"/>. Multi-table queries that join two
/// sidecar-bound tables get distinct <c>storeId</c>s for each table, so a
/// joined row whose image cells came from different sources still resolves
/// each cell against its correct sidecar.
/// </para>
/// <para>
/// On-disk DataValues do not persist a <c>storeId</c>: each <c>.datum</c> file
/// has at most one companion <c>.datum-blob</c>, so within a file all sidecar
/// values share one (implicit) source. The byte is assigned at read time when
/// the provider registers, and the decoder stamps freshly-decoded values with
/// the assigned byte.
/// </para>
/// <para>
/// Cap: 256 stores per query (the byte is the limit). A query referencing
/// more than 256 sidecar-bound tables is pathological in practice; real
/// workloads touch 1-10. Overflow throws at <see cref="Register"/> with a
/// clear message rather than silently corrupting.
/// </para>
/// </remarks>
public sealed class SidecarRegistry
{
    private readonly IBlobSource?[] _stores = new IBlobSource?[256];
    private int _count;

    /// <summary>The number of sidecars currently registered (0-256).</summary>
    public int Count => _count;

    /// <summary>
    /// Registers a sidecar source and returns the <c>storeId</c> byte assigned to it.
    /// The byte is stable for the lifetime of this registry. Subsequent
    /// <see cref="Resolve"/> calls with the returned byte return <paramref name="source"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown when 256 stores are already registered. Split the query or contact support.
    /// </exception>
    public byte Register(IBlobSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (_count >= _stores.Length)
        {
            throw new InvalidOperationException(
                "Query references more sidecar-backed tables (>256) than the runtime supports. " +
                "Each sidecar-bound table consumes one storeId byte per query; this limit is " +
                "very generous for realistic workloads (which touch 1-10 tables) but is hard " +
                "in the on-disk encoding. Split the query into smaller pieces.");
        }

        byte id = (byte)_count;
        _stores[id] = source;
        _count++;
        return id;
    }

    /// <summary>
    /// Returns the sidecar source previously registered for <paramref name="storeId"/>,
    /// or <see langword="null"/> if no source is registered at that slot.
    /// </summary>
    public IBlobSource? Resolve(byte storeId) => _stores[storeId];
}
