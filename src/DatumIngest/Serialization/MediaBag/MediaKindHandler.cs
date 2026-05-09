using DatumIngest.Model;

namespace DatumIngest.Serialization.MediaBag;

/// <summary>
/// Per-media-kind plumbing for <see cref="MediaBagDeserializer"/>. A handler
/// declares the kind's column schema, recognises the kind's magic bytes, and
/// knows how to populate a row from either arena-resident or sidecar-resident
/// payload bytes. The deserializer probes the first non-metadata entry of an
/// archive, locks one handler instance, and validates every subsequent entry
/// matches its magic.
/// </summary>
/// <remarks>
/// Adding a new media kind is two methods: implement the abstract members and
/// wire the singleton into <see cref="Detect"/>. The deserializer is otherwise
/// kind-agnostic, so a third audio container or a new video kind doesn't ripple
/// into the hot loop.
/// </remarks>
internal abstract class MediaKindHandler
{
    /// <summary>The element kind of the primary media column.</summary>
    public abstract DataKind Kind { get; }

    /// <summary>
    /// Ordered column names for the emitted schema. Slot 0 is always
    /// <c>file_name</c>, slot 1 is always <c>file</c> (the typed media value);
    /// later slots are the kind-specific derived columns.
    /// </summary>
    public abstract string[] ColumnNames { get; }

    /// <summary>Returns <c>true</c> when the prefix bytes match this kind's magic signatures.</summary>
    public abstract bool MatchesMagic(ReadOnlySpan<byte> magic);

    /// <summary>
    /// Populates the row from bytes already appended to <paramref name="arena"/>
    /// at <paramref name="arenaOffset"/> / <paramref name="actualLength"/>.
    /// <paramref name="bytes"/> is the same region exposed as a span for header
    /// parsing / hashing.
    /// </summary>
    public abstract void PopulateRowFromArena(
        DataValue[] values, string fullName,
        long arenaOffset, int actualLength,
        ReadOnlySpan<byte> bytes,
        Arena arena);

    /// <summary>
    /// Populates the row from bytes already written to a <c>.datum-blob</c>
    /// sidecar at absolute <paramref name="sidecarOffset"/> /
    /// <paramref name="sidecarLength"/>. <paramref name="bytes"/> is a pooled
    /// view of the same payload for inline header parsing / hashing.
    /// </summary>
    public abstract void PopulateRowFromSidecar(
        DataValue[] values, string fullName,
        long sidecarOffset, long sidecarLength,
        ReadOnlySpan<byte> bytes,
        Arena arena);

    /// <summary>
    /// Probes <paramref name="magic"/> against every registered handler and
    /// returns the first match, or <c>null</c> when no kind recognises the
    /// prefix. The caller is responsible for turning <c>null</c> into a
    /// "no supported media kind" error keyed to the offending entry name.
    /// </summary>
    public static MediaKindHandler? Detect(ReadOnlySpan<byte> magic)
    {
        if (ImageKindHandler.Instance.MatchesMagic(magic)) return ImageKindHandler.Instance;
        if (AudioKindHandler.Instance.MatchesMagic(magic)) return AudioKindHandler.Instance;
        return null;
    }
}
