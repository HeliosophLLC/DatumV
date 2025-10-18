using System.Globalization;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// Per-column adaptive cache that remembers the last-successful date/time format
/// per column and tries it first on subsequent rows. Falls back to a fixed list of
/// common candidate formats, then to the BCL's general <c>TryParse</c> as a
/// last resort.
/// </summary>
/// <remarks>
/// <para>
/// Profiling showed that <c>DateTimeOffset.TryParse</c> dominates CSV ingestion CPU by
/// a wide margin (~90% on Chicago Crimes) because the BCL parser has no hint about the
/// input format and must try many variations per row. When a column uses a consistent
/// format, memoising that format and calling <c>DateTimeOffset.TryParseExact</c> with
/// the cached format gives a 5–10× speedup per row.
/// </para>
/// <para>
/// The cache behaves like a move-to-front cache of size one per column: if the cached
/// format fails (e.g. the column contains mixed formats), we scan the candidate list,
/// update the cache to the new winner, and continue. Truly random formats degrade to
/// at-worst the candidate-walk cost; no correctness regression versus the old path.
/// </para>
/// </remarks>
public sealed class TemporalFormatCache
{
    /// <summary>
    /// Sentinel value for "no format has succeeded on this column yet". Used as the
    /// initial state and after a cache reset.
    /// </summary>
    private const int Unset = -1;

    /// <summary>
    /// Candidate <see cref="DateTimeOffset"/> / <see cref="DateTime"/> formats. Ordered
    /// roughly by expected frequency across real-world datasets (ISO 8601 first, then US,
    /// then ambiguous DD/MM, then date-only variants).
    /// </summary>
    internal static readonly string[] DateTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.fff",
        "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "MM/dd/yyyy hh:mm:ss tt",
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy hh:mm:ss tt",
        "M/d/yyyy HH:mm:ss",
        "dd/MM/yyyy HH:mm:ss",
        "yyyy-MM-dd",
        "MM/dd/yyyy",
        "dd/MM/yyyy",
    };

    /// <summary>
    /// Candidate <see cref="DateOnly"/> formats. A subset of
    /// <see cref="DateTimeFormats"/> that cleanly describes a whole day.
    /// </summary>
    internal static readonly string[] DateFormats =
    {
        "yyyy-MM-dd",
        "MM/dd/yyyy",
        "M/d/yyyy",
        "dd/MM/yyyy",
        "d/M/yyyy",
        "yyyy/MM/dd",
    };

    // One cached-format index per column, for DateTime and Date dispatch respectively.
    private readonly int[] _dateTimeCache;
    private readonly int[] _dateCache;

    /// <summary>
    /// Initializes a fresh cache with an "unset" slot for each of
    /// <paramref name="columnCount"/> columns. The cache grows no further after
    /// construction; callers specify the column count up front.
    /// </summary>
    public TemporalFormatCache(int columnCount)
    {
        _dateTimeCache = new int[columnCount];
        _dateCache = new int[columnCount];
        Array.Fill(_dateTimeCache, Unset);
        Array.Fill(_dateCache, Unset);
    }

    /// <summary>
    /// Attempts to parse a <see cref="DateTimeOffset"/> using the last-successful
    /// format for <paramref name="columnIndex"/> first, then the candidate list,
    /// then a full <c>DateTimeOffset.TryParse</c> fallback. On success, the matching
    /// format's index is cached so future rows on the same column hit the fast path.
    /// </summary>
    public bool TryParseDateTime(ReadOnlySpan<char> field, int columnIndex, out DateTimeOffset value)
    {
        int cached = _dateTimeCache[columnIndex];
        if (cached != Unset && DateTimeOffset.TryParseExact(
                field, DateTimeFormats[cached], CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out value))
        {
            return true;
        }

        for (int i = 0; i < DateTimeFormats.Length; i++)
        {
            if (i == cached) continue;
            if (DateTimeOffset.TryParseExact(
                    field, DateTimeFormats[i], CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out value))
            {
                _dateTimeCache[columnIndex] = i;
                return true;
            }
        }

        // Last resort — an unrecognised but BCL-parseable format. Don't cache since we
        // can't identify which candidate would match; next row will walk the list again.
        return DateTimeOffset.TryParse(
            field, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out value);
    }

    /// <summary>
    /// Attempts to parse a <see cref="DateOnly"/>. See <see cref="TryParseDateTime"/>
    /// for caching behaviour — identical semantics with
    /// <see cref="DateFormats"/> as the candidate set.
    /// </summary>
    public bool TryParseDate(ReadOnlySpan<char> field, int columnIndex, out DateOnly value)
    {
        int cached = _dateCache[columnIndex];
        if (cached != Unset && DateOnly.TryParseExact(
                field, DateFormats[cached], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value))
        {
            return true;
        }

        for (int i = 0; i < DateFormats.Length; i++)
        {
            if (i == cached) continue;
            if (DateOnly.TryParseExact(
                    field, DateFormats[i], CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out value))
            {
                _dateCache[columnIndex] = i;
                return true;
            }
        }

        // Fall back to the full parser — catches ambiguous / locale-specific forms.
        return DateOnly.TryParse(field, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
