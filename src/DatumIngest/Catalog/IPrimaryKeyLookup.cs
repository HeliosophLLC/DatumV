using System.Diagnostics.CodeAnalysis;
using DatumIngest.Indexing;
using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Read-side surface a provider exposes when it backs PK enforcement with an
/// on-disk index. Two modes:
/// <list type="bullet">
///   <item><b>Single-column</b> — typed B+Tree keyed by <see cref="DataValue"/>.
///     <see cref="TryFind(DataValue, out ValueIndexEntry?)"/> probes by typed value.
///     <see cref="IsComposite"/> is <see langword="false"/>.</item>
///   <item><b>Composite</b> — bytes-keyed B+Tree storing composite-encoded
///     keys. <see cref="TryFindComposite"/> probes by encoded byte sequence
///     (built via <see cref="CompositeKeyEncoder.Encode"/>).
///     <see cref="IsComposite"/> is <see langword="true"/>.</item>
/// </list>
/// Providers without an on-disk PK index (TEMP / InMemory) return
/// <see langword="null"/> from <see cref="ITableProvider.GetPrimaryKeyLookup"/>
/// and the executor falls back to the scan-based path.
/// </summary>
public interface IPrimaryKeyLookup
{
    /// <summary>
    /// Returns <see langword="true"/> when this lookup serves a composite PK
    /// (multiple columns, byte-encoded keys). Single-column lookups return
    /// <see langword="false"/> (the default).
    /// </summary>
    bool IsComposite => false;

    /// <summary>
    /// Single-column probe. Returns <see langword="true"/> and the matching
    /// entry if a row with the given key exists. Throws
    /// <see cref="NotSupportedException"/> when invoked on a composite
    /// lookup — callers should check <see cref="IsComposite"/> and use
    /// <see cref="TryFindComposite"/> instead.
    /// </summary>
    bool TryFind(DataValue key, [NotNullWhen(true)] out ValueIndexEntry? entry);

    /// <summary>
    /// Composite probe via composite-encoded bytes. Returns
    /// <see langword="true"/> and the matching entry if a row with the given
    /// tuple exists. Throws <see cref="NotSupportedException"/> on
    /// single-column lookups; callers check <see cref="IsComposite"/> first.
    /// </summary>
    bool TryFindComposite(ReadOnlySpan<byte> encodedKey, [NotNullWhen(true)] out ValueIndexEntry? entry)
        => throw new NotSupportedException(
            "This lookup is single-column. Use TryFind(DataValue, ...) instead.");
}
