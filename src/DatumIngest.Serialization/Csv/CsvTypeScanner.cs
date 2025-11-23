using System.Diagnostics;
using System.Globalization;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Csv;

/// <summary>
/// First pass of the two-pass CSV ingestion pipeline. Reads every row of the file
/// once to build an authoritative per-column type inference, narrowing integer and
/// float ranges, detecting leading-zero "code" columns, and matching date formats.
/// The resulting <see cref="CsvScanResult"/> is then fed to <see cref="CsvDeserializer"/>
/// so pass 2 skips sample-based inference and benefits from a pre-warmed temporal
/// format cache.
/// </summary>
/// <remarks>
/// Zero-allocation hot path — reuses the span-based field parser semantics from
/// <see cref="CsvDeserializer"/> but dispatches per field into a small per-column
/// state struct instead of constructing <see cref="DataValue"/>s. Short-circuits
/// type candidates as they are eliminated so narrow numeric columns stop paying
/// date/UUID costs after the first non-numeric value.
/// </remarks>
public static class CsvTypeScanner
{
    /// <summary>Scans the file end-to-end and returns a finalised schema decision per column.</summary>
    public static async Task<CsvScanResult> ScanAsync(
        FileFormatDescriptor source,
        CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        await using Stream stream = await source.OpenAsync(cancellationToken).ConfigureAwait(false);

        char delimiter = DelimiterDetector.Detect(stream, source.Options, source.FilePath);
        bool? headerOverride = GetHeaderOverride(source.Options);
        HeaderDetectionResult detection = HeaderDetector.Detect(stream, delimiter, headerOverride);

        if (detection.ColumnNames.Length == 0)
        {
            sw.Stop();
            return CsvScanResult.Empty(sw.Elapsed);
        }

        string[] names = detection.ColumnNames;
        int columnCount = names.Length;

        ColumnScanState[] states = new ColumnScanState[columnCount];
        for (int i = 0; i < columnCount; i++) states[i] = ColumnScanState.Initial();

        TemporalFormatCache temporalCache = new(columnCount);

        using LineReader lineReader = new(stream);
        if (detection.HasHeader) lineReader.ReadLineAsString();

        long rowCount = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!lineReader.TryReadLogicalLine(out ReadOnlySpan<char> lineSpan))
                break;

            rowCount++;

            if (!lineSpan.Contains('"'))
                ScanUnquotedLine(lineSpan, delimiter, states, temporalCache);
            else
                ScanQuotedLine(lineSpan, delimiter, states, temporalCache);
        }

        DataKind[] kinds = new DataKind[columnCount];
        SchemaInferenceDecision?[] decisions = new SchemaInferenceDecision?[columnCount];
        long[] nullCounts = new long[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            nullCounts[i] = states[i].NullCount;
            (kinds[i], decisions[i]) = FinalizeColumn(ref states[i], rowCount);
        }

        sw.Stop();

        return new CsvScanResult(
            names, kinds, decisions, temporalCache, rowCount, nullCounts,
            stream.Length, sw.Elapsed, detection.HasHeader, delimiter);
    }

    // ────────────────────── Line enumeration ──────────────────────

    private static void ScanUnquotedLine(
        ReadOnlySpan<char> line, char delimiter,
        ColumnScanState[] states, TemporalFormatCache temporalCache)
    {
        int fieldStart = 0;
        int columnIndex = 0;
        int columnCount = states.Length;

        for (int i = 0; i <= line.Length && columnIndex < columnCount; i++)
        {
            if (i == line.Length || line[i] == delimiter)
            {
                ReadOnlySpan<char> field = line[fieldStart..i].Trim();
                UpdateColumnState(ref states[columnIndex], field, columnIndex, temporalCache);
                columnIndex++;
                fieldStart = i + 1;
            }
        }

        while (columnIndex < columnCount)
        {
            UpdateColumnState(ref states[columnIndex], ReadOnlySpan<char>.Empty, columnIndex, temporalCache);
            columnIndex++;
        }
    }

    [ThreadStatic]
    private static char[]? _unescapeBuffer;

    private static void ScanQuotedLine(
        ReadOnlySpan<char> line, char delimiter,
        ColumnScanState[] states, TemporalFormatCache temporalCache)
    {
        int position = 0;
        int columnIndex = 0;
        int columnCount = states.Length;

        while (columnIndex < columnCount && position <= line.Length)
        {
            ReadOnlySpan<char> fieldSpan;

            if (position < line.Length && line[position] == '"')
            {
                position++;
                int segmentStart = position;
                int unescapeLength = 0;
                bool needsUnescape = false;

                while (position < line.Length)
                {
                    if (line[position] != '"') { position++; continue; }

                    if (position + 1 < line.Length && line[position + 1] == '"')
                    {
                        if (!needsUnescape)
                        {
                            EnsureUnescapeBuffer(line.Length);
                            line.Slice(segmentStart, position - segmentStart).CopyTo(_unescapeBuffer!);
                            unescapeLength = position - segmentStart;
                            needsUnescape = true;
                        }
                        else
                        {
                            line.Slice(segmentStart, position - segmentStart)
                                .CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                            unescapeLength += position - segmentStart;
                        }
                        _unescapeBuffer![unescapeLength++] = '"';
                        position += 2;
                        segmentStart = position;
                    }
                    else
                    {
                        if (needsUnescape)
                        {
                            line.Slice(segmentStart, position - segmentStart)
                                .CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                            unescapeLength += position - segmentStart;
                            fieldSpan = _unescapeBuffer.AsSpan(0, unescapeLength);
                        }
                        else
                        {
                            fieldSpan = line.Slice(segmentStart, position - segmentStart);
                        }
                        position++;
                        if (position < line.Length && line[position] == delimiter) position++;
                        goto ProcessField;
                    }
                }

                if (needsUnescape)
                {
                    line.Slice(segmentStart, position - segmentStart)
                        .CopyTo(_unescapeBuffer!.AsSpan(unescapeLength));
                    unescapeLength += position - segmentStart;
                    fieldSpan = _unescapeBuffer.AsSpan(0, unescapeLength);
                }
                else
                {
                    fieldSpan = line.Slice(segmentStart, position - segmentStart);
                }
            }
            else
            {
                int remaining = line.Length - position;
                int nextDelim = remaining > 0 ? line[position..].IndexOf(delimiter) : -1;
                int fieldEnd = nextDelim < 0 ? line.Length : position + nextDelim;
                fieldSpan = line.Slice(position, fieldEnd - position);
                position = nextDelim < 0 ? line.Length + 1 : fieldEnd + 1;
            }

        ProcessField:
            UpdateColumnState(ref states[columnIndex], fieldSpan.Trim(), columnIndex, temporalCache);
            columnIndex++;
        }

        while (columnIndex < columnCount)
        {
            UpdateColumnState(ref states[columnIndex], ReadOnlySpan<char>.Empty, columnIndex, temporalCache);
            columnIndex++;
        }
    }

    private static void EnsureUnescapeBuffer(int minSize)
    {
        if (_unescapeBuffer is null || _unescapeBuffer.Length < minSize)
            _unescapeBuffer = new char[minSize];
    }

    // ────────────────────── Per-field state update ──────────────────────

    private static void UpdateColumnState(
        ref ColumnScanState state,
        ReadOnlySpan<char> field,
        int columnIndex,
        TemporalFormatCache temporalCache)
    {
        if (field.IsEmpty || CsvParser.IsNullLiteral(field))
        {
            state.NullCount++;
            return;
        }

        state.HasAnyNonNullValue = true;
        if (field.Length < state.MinTextLength) state.MinTextLength = field.Length;
        if (field.Length > state.MaxTextLength) state.MaxTextLength = field.Length;

        // Leading-zero code detection: "02931" yes, "0" no, "0.5" no, "-01" no.
        if (!state.LeadingZeroSeen && field.Length > 1 && field[0] == '0' && IsAllDigits(field))
        {
            state.LeadingZeroSeen = true;
            if (state.LeadingZeroExample is null)
                state.LeadingZeroExample = field.ToString();
        }

        // Run every still-active candidate. A candidate that fails for THIS value is
        // permanently eliminated for the column. Length guards fast-fail the
        // structurally impossible cases (e.g. UUID needs exactly 36 chars) rather
        // than skipping the check — skipping would leave the candidate alive and
        // produce false positives like "Arrest" → DateTime from "true"/"false".

        if (state.IntegerCandidate)
        {
            if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long i))
            {
                if (i < state.IntMin) state.IntMin = i;
                if (i > state.IntMax) state.IntMax = i;
                // Integer values above |2^24| lose precision in Float32 even if float-parseable.
                if (state.AllFloat32Safe && (i < -(1L << 24) || i > (1L << 24)))
                    state.AllFloat32Safe = false;
                // i is non-negative? UInt128 still in play; otherwise UInt128 falls out.
                if (i < 0) state.UInt128Candidate = false;
            }
            else
            {
                // long.TryParse failed. Two possibilities: (a) the value is a 128-bit
                // integer that overflows Int64 — keep Int128/UInt128 candidates alive
                // and flag Float32 unsafety; (b) the value isn't an integer at all
                // (e.g. "1.5", "true") — drop all integer candidates and don't punish
                // Float32-safety based on this non-integer value.
                state.IntegerCandidate = false;

                bool fitsInt128 = Int128.TryParse(
                    field, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                bool fitsUInt128 = UInt128.TryParse(
                    field, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);

                if (!fitsInt128) state.Int128Candidate = false;
                if (!fitsUInt128) state.UInt128Candidate = false;

                // 128-bit integers always exceed Float32's 24-bit integer-precision
                // ceiling — but only flag this when we actually saw a 128-bit value.
                if (state.AllFloat32Safe && (fitsInt128 || fitsUInt128))
                {
                    state.AllFloat32Safe = false;
                }
            }
        }

        if (state.FloatCandidate)
        {
            if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            {
                if (state.AllFloat32Safe && !IsFloat32RoundTripSafe(d))
                    state.AllFloat32Safe = false;
            }
            else
            {
                state.FloatCandidate = false;
                state.AllFloat32Safe = false;
            }
        }

        // DateTime requires a time component — every format we care about has at least
        // one ':' separator. Requiring this also prevents pure-date values from matching
        // the BCL's flexible fallback parser inside TemporalFormatCache and being
        // mis-classified as DateTime when they're really Date.
        if (state.DateTimeCandidate)
        {
            if (field.Length < 8 || field.IndexOf(':') < 0
                || !temporalCache.TryParseDateTime(field, columnIndex, out _))
            {
                state.DateTimeCandidate = false;
            }
        }

        if (state.DateCandidate)
        {
            if (field.Length < 8 || !temporalCache.TryParseDate(field, columnIndex, out _))
                state.DateCandidate = false;
        }

        if (state.UuidCandidate)
        {
            if (field.Length != 36 || !Guid.TryParse(field, out _))
                state.UuidCandidate = false;
        }

        if (state.BooleanCandidate)
        {
            if (!DataValueComparer.IsBooleanLiteral(field))
                state.BooleanCandidate = false;
        }
    }

    private static bool IsAllDigits(ReadOnlySpan<char> s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] < '0' || s[i] > '9') return false;
        return true;
    }

    private static bool IsFloat32RoundTripSafe(double d)
    {
        if (double.IsNaN(d)) return true;
        if (double.IsPositiveInfinity(d) || double.IsNegativeInfinity(d)) return true;
        float f = (float)d;
        return (double)f == d;
    }

    // ────────────────────── Finalize a column's kind + decision ──────────────────────

    private static (DataKind Kind, SchemaInferenceDecision Decision) FinalizeColumn(
        ref ColumnScanState state, long totalRowCount)
    {
        if (!state.HasAnyNonNullValue)
        {
            return (DataKind.String, new SchemaInferenceDecision(
                SchemaInferenceReason.AllNull, SchemaInferenceSeverity.Warning,
                "Column contains only null/empty values; defaulting to String.",
                null));
        }

        if (state.LeadingZeroSeen)
        {
            Dictionary<string, object> evidence = new(StringComparer.Ordinal)
            {
                ["min_text_width"] = state.MinTextLength,
                ["max_text_width"] = state.MaxTextLength,
            };
            if (state.LeadingZeroExample is not null)
                evidence["example"] = state.LeadingZeroExample;

            return (DataKind.String, new SchemaInferenceDecision(
                SchemaInferenceReason.KeptAsStringLeadingZeros, SchemaInferenceSeverity.Notable,
                $"Kept as String because at least one value has a leading zero (e.g. {state.LeadingZeroExample ?? "observed"}); narrowing would drop the zero padding. Query as integer with CAST(column AS Int32) if needed.",
                evidence));
        }

        // Pure integer column → narrow to smallest fit. [0, 1] → Boolean (common CSV idiom).
        if (state.IntegerCandidate)
        {
            DataKind narrowed = NarrowInteger(state.IntMin, state.IntMax);
            Dictionary<string, object> evidence = new(StringComparer.Ordinal)
            {
                ["observed_min"] = state.IntMin,
                ["observed_max"] = state.IntMax,
                ["narrowed_to"] = narrowed.ToString(),
            };
            string explanation = narrowed == DataKind.Int64
                ? $"Integer column with range [{state.IntMin}, {state.IntMax}]; requires Int64."
                : $"Integer column narrowed to {narrowed} based on observed range [{state.IntMin}, {state.IntMax}].";
            return (narrowed, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange,
                SchemaInferenceSeverity.Routine, explanation, evidence));
        }

        // Int64 overflow but 128-bit-fits. Prefer UInt128 over Int128 when both
        // hold (column is non-negative + exceeds Int64) — UInt128 has more headroom
        // and matches the "high-value identifier" intent that typically drives
        // 128-bit columns.
        if (state.UInt128Candidate || state.Int128Candidate)
        {
            DataKind chosen = state.UInt128Candidate ? DataKind.UInt128 : DataKind.Int128;
            return (chosen, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange, SchemaInferenceSeverity.Routine,
                $"Integer column with at least one value outside Int64 range; promoted to {chosen}.",
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["narrowed_to"] = chosen.ToString(),
                }));
        }

        // Float column (some non-int value observed, but all numeric).
        if (state.FloatCandidate)
        {
            if (state.AllFloat32Safe)
            {
                return (DataKind.Float32, new SchemaInferenceDecision(
                    SchemaInferenceReason.FloatNarrowedToFloat32,
                    SchemaInferenceSeverity.Routine,
                    "All observed values round-trip through single precision; narrowed from Float64 to Float32.",
                    new Dictionary<string, object>(StringComparer.Ordinal)
                    {
                        ["narrowed_to"] = "Float32",
                    }));
            }
            return (DataKind.Float64, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange,
                SchemaInferenceSeverity.Routine,
                "Numeric column with fractional or wide-range values; using Float64.",
                null));
        }

        // Temporal columns — prefer DateTime over Date when both match (DateTime is strictly more general).
        if (state.DateTimeCandidate)
        {
            return BuildDateDecision(DataKind.DateTime, SchemaInferenceReason.DateFormatMatched);
        }
        if (state.DateCandidate)
        {
            return BuildDateDecision(DataKind.Date, SchemaInferenceReason.DateFormatMatched);
        }

        if (state.UuidCandidate)
        {
            return (DataKind.Uuid, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange, SchemaInferenceSeverity.Routine,
                "All values parsed as GUID.", null));
        }

        if (state.BooleanCandidate)
        {
            return (DataKind.Boolean, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange, SchemaInferenceSeverity.Routine,
                "All values parsed as true/false or 0/1.", null));
        }

        // Mixed — fall back to String.
        return (DataKind.String, new SchemaInferenceDecision(
            SchemaInferenceReason.KeptAsStringMixedFormats, SchemaInferenceSeverity.Notable,
            "Values did not all parse as any single numeric, temporal, or boolean type; kept as String.",
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["min_text_width"] = state.MinTextLength,
                ["max_text_width"] = state.MaxTextLength,
            }));
    }

    private static DataKind NarrowInteger(long min, long max)
    {
        if (min >= 0 && max <= 1) return DataKind.Boolean;
        if (min >= 0 && max <= byte.MaxValue) return DataKind.UInt8;
        if (min >= sbyte.MinValue && max <= sbyte.MaxValue) return DataKind.Int8;
        if (min >= 0 && max <= ushort.MaxValue) return DataKind.UInt16;
        if (min >= short.MinValue && max <= short.MaxValue) return DataKind.Int16;
        if (min >= 0 && max <= uint.MaxValue) return DataKind.UInt32;
        if (min >= int.MinValue && max <= int.MaxValue) return DataKind.Int32;
        return DataKind.Int64;
    }

    private static (DataKind, SchemaInferenceDecision) BuildDateDecision(
        DataKind kind, SchemaInferenceReason reason)
    {
        return (kind, new SchemaInferenceDecision(
            reason, SchemaInferenceSeverity.Routine,
            $"All values parsed as {kind}.", null));
    }

    private static bool? GetHeaderOverride(IReadOnlyDictionary<string, string> options)
    {
        if (options.TryGetValue("header", out string? value))
        {
            if (value.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
            if (value.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return null;
    }
}

/// <summary>Result of a full-file CSV scan. Feeds <see cref="CsvDeserializer"/> for pass 2.</summary>
public sealed record CsvScanResult(
    string[] ColumnNames,
    DataKind[] Kinds,
    SchemaInferenceDecision?[] Decisions,
    TemporalFormatCache WarmedTemporalCache,
    long RowCount,
    long[] NullCountsPerColumn,
    long BytesRead,
    TimeSpan Elapsed,
    bool HasHeader,
    char Delimiter)
{
    /// <summary>Empty result for files with no columns or no data.</summary>
    public static CsvScanResult Empty(TimeSpan elapsed) => new(
        [], [], [], new TemporalFormatCache(0), 0, [], 0, elapsed, false, ',');
}

/// <summary>
/// Accumulated per-column observations during a scan. Mutable, stored in an array
/// parallel to the column list. Each field's contract:
/// <list type="bullet">
///   <item><c>*Candidate</c> flags: start true, flipped permanently to false the first time the
///     corresponding type's parse fails for a non-null value.</item>
///   <item><c>IntMin</c>/<c>IntMax</c>: only valid while <see cref="IntegerCandidate"/> is true.</item>
///   <item><c>AllFloat32Safe</c>: tracks whether every numeric value (integer or float)
///     round-trips through <see cref="float"/> without loss.</item>
///   <item><c>LeadingZeroSeen</c>: latch. Once set, the column is forced to String regardless
///     of what other candidates survive.</item>
/// </list>
/// </summary>
internal struct ColumnScanState
{
    public bool IntegerCandidate;
    /// <summary>
    /// All non-null values fit in <see cref="Int128"/>. Implied true when
    /// <see cref="IntegerCandidate"/> is true (every Int64 value fits Int128).
    /// </summary>
    public bool Int128Candidate;
    /// <summary>
    /// All non-null values fit in <see cref="UInt128"/>. Implied true when
    /// <see cref="IntegerCandidate"/> is true *and* every value seen so far is non-negative.
    /// </summary>
    public bool UInt128Candidate;
    public bool FloatCandidate;
    public bool BooleanCandidate;
    public bool DateCandidate;
    public bool DateTimeCandidate;
    public bool UuidCandidate;
    public bool LeadingZeroSeen;
    public bool MixedTypeEncountered;
    public bool HasAnyNonNullValue;
    public bool AllFloat32Safe;

    public long IntMin;
    public long IntMax;
    public int MinTextLength;
    public int MaxTextLength;
    public long NullCount;

    public string? LeadingZeroExample;

    public static ColumnScanState Initial() => new()
    {
        IntegerCandidate = true,
        Int128Candidate = true,
        UInt128Candidate = true,
        FloatCandidate = true,
        BooleanCandidate = true,
        DateCandidate = true,
        DateTimeCandidate = true,
        UuidCandidate = true,
        AllFloat32Safe = true,
        IntMin = long.MaxValue,
        IntMax = long.MinValue,
        MinTextLength = int.MaxValue,
        MaxTextLength = 0,
    };
}
