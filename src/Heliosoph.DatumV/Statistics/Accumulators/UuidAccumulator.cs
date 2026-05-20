namespace Heliosoph.DatumV.Statistics.Accumulators;

using Heliosoph.DatumV.Model;

/// <summary>
/// Accumulates per-version distribution and (for v7 UUIDs) embedded-timestamp
/// range for <see cref="DataKind.Uuid"/> columns. The version byte is the
/// only kind-specific signal an analyst typically wants surfaced in a
/// manifest — beyond that, <see cref="CardinalityAccumulator"/> already
/// covers distinct count and the role classifier handles Identifier vs
/// ForeignKey via the cardinality ratio.
/// </summary>
/// <remarks>
/// RFC 9562 versions tracked: 1 (timestamp), 2 (DCE), 3 (MD5 namespace),
/// 4 (random), 5 (SHA1 namespace), 6 (timestamp+random), 7 (Unix timestamp),
/// 8 (custom). Anything else (including the all-zero nil UUID) falls under
/// version 0.
///
/// Embedded-timestamp range: only computed for v7 (the simplest format —
/// 48 bits of unix milliseconds in the leading bytes). v1 / v6 timestamps
/// are deferred — their bit layouts (60-bit timestamp split across
/// time_low / time_mid / time_hi_and_version, anchored at 1582-10-15)
/// add complexity that isn't load-bearing for v1 of the manifest.
/// </remarks>
public sealed class UuidAccumulator : IStatisticAccumulator
{
    private readonly Dictionary<int, long> _versionCounts = new();
    private long _v7MinUnixMs = long.MaxValue;
    private long _v7MaxUnixMs = long.MinValue;
    private bool _sawV7;

    /// <inheritdoc />
    public void Add(DataValue value, IValueStore store)
    {
        if (value.IsNull || value.Kind != DataKind.Uuid)
        {
            return;
        }

        Guid g = value.AsUuid();
        Span<byte> bytes = stackalloc byte[16];
        if (!g.TryWriteBytes(bytes, bigEndian: true, out _))
        {
            return;
        }

        int version = (bytes[6] >> 4) & 0x0F;
        if (_versionCounts.TryGetValue(version, out long count))
        {
            _versionCounts[version] = count + 1;
        }
        else
        {
            _versionCounts[version] = 1;
        }

        if (version == 7)
        {
            // RFC 9562 v7: bytes[0..6] = unix_ts_ms (48 bits, big-endian).
            long unixMs =
                ((long)bytes[0] << 40) |
                ((long)bytes[1] << 32) |
                ((long)bytes[2] << 24) |
                ((long)bytes[3] << 16) |
                ((long)bytes[4] << 8)  |
                 (long)bytes[5];
            if (unixMs < _v7MinUnixMs) _v7MinUnixMs = unixMs;
            if (unixMs > _v7MaxUnixMs) _v7MaxUnixMs = unixMs;
            _sawV7 = true;
        }
    }

    /// <inheritdoc />
    public IEnumerable<StatisticResult> GetResults()
    {
        DateTimeOffset? earliest = _sawV7
            ? DateTimeOffset.FromUnixTimeMilliseconds(_v7MinUnixMs)
            : null;
        DateTimeOffset? latest = _sawV7
            ? DateTimeOffset.FromUnixTimeMilliseconds(_v7MaxUnixMs)
            : null;

        yield return new StatisticResult("uuid_stats", new UuidStatsResult(
            _versionCounts.Count > 0
                ? new Dictionary<int, long>(_versionCounts)
                : new Dictionary<int, long>(),
            earliest,
            latest));
    }
}

/// <summary>
/// UUID-column statistics produced by <see cref="UuidAccumulator"/>.
/// </summary>
/// <param name="VersionCounts">Counts of UUIDs per RFC 9562 version field (0-8). Version 0 covers the nil UUID and unrecognised values.</param>
/// <param name="EmbeddedTimestampEarliest">Earliest embedded timestamp across all v7 UUIDs, or <see langword="null"/> when no v7 UUIDs were observed.</param>
/// <param name="EmbeddedTimestampLatest">Latest embedded timestamp across all v7 UUIDs, or <see langword="null"/> when no v7 UUIDs were observed.</param>
public sealed record UuidStatsResult(
    IReadOnlyDictionary<int, long> VersionCounts,
    DateTimeOffset? EmbeddedTimestampEarliest,
    DateTimeOffset? EmbeddedTimestampLatest)
{
    /// <summary>An empty result.</summary>
    public static UuidStatsResult Empty { get; } = new(new Dictionary<int, long>(), null, null);
}
