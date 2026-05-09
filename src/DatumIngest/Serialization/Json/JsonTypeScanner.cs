using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Serialization.Json;

/// <summary>
/// First pass of the JSON ingestion pipeline. Walks every row of the document
/// once to discover the union of keys across rows and infer a per-column
/// <see cref="DataKind"/>. The resulting <see cref="JsonScanResult"/> feeds
/// <see cref="JsonDeserializer"/> so pass 2 emits <see cref="DataValue"/>s
/// against a stable schema.
/// </summary>
/// <remarks>
/// <para>
/// JSON values arrive pre-typed, so the per-column state machine is simpler than
/// <see cref="Csv.CsvTypeScanner"/>: each non-null value either matches the column's
/// active candidate family (string-only, boolean-only, integer-only, numeric, …) or
/// forces the column to <see cref="DataKind.Json"/>. Object/array values force
/// <see cref="DataKind.Json"/> on first appearance; mixed scalar families
/// (e.g. one row String, another Number) also fall through to
/// <see cref="DataKind.Json"/> so the original shape stays queryable.
/// </para>
/// <para>
/// Keys are unioned across rows. A key present in some rows and absent in others
/// produces a nullable column; JSON <c>null</c> and key absence are treated
/// identically.
/// </para>
/// </remarks>
public static class JsonTypeScanner
{
    /// <summary>
    /// Scans the rows of a JSON document and returns a finalised schema decision per column.
    /// The <paramref name="rows"/> sequence may be iterated more than once.
    /// </summary>
    public static JsonScanResult Scan(IEnumerable<JsonElement> rows, CancellationToken cancellationToken = default)
    {
        Stopwatch sw = Stopwatch.StartNew();

        List<string> columnNames = [];
        Dictionary<string, int> columnIndex = new(StringComparer.Ordinal);
        List<JsonColumnScanState> states = [];

        // Reused per-row: marks which columns were observed in the current row so
        // we can bump NullCount on columns that were absent. Grown on demand as new
        // columns are discovered; cleared at the start of each row.
        bool[] touched = [];
        long rowCount = 0;

        foreach (JsonElement row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            rowCount++;

            if (touched.Length < states.Count) Array.Resize(ref touched, Math.Max(8, states.Count * 2));
            Array.Clear(touched, 0, states.Count);

            foreach (JsonProperty prop in row.EnumerateObject())
            {
                if (!columnIndex.TryGetValue(prop.Name, out int idx))
                {
                    idx = columnNames.Count;
                    columnIndex[prop.Name] = idx;
                    columnNames.Add(prop.Name);
                    JsonColumnScanState fresh = JsonColumnScanState.Initial();
                    // A column discovered late-file was absent in every prior row;
                    // each prior absence counts as a null observation.
                    fresh.NullCount = rowCount - 1;
                    states.Add(fresh);

                    if (touched.Length <= idx) Array.Resize(ref touched, Math.Max(8, states.Count * 2));
                }

                touched[idx] = true;

                JsonColumnScanState state = states[idx];
                UpdateColumnState(ref state, prop.Value);
                states[idx] = state;
            }

            // Bump NullCount for any column not present in this row.
            for (int i = 0; i < states.Count; i++)
            {
                if (!touched[i])
                {
                    JsonColumnScanState s = states[i];
                    s.NullCount++;
                    states[i] = s;
                }
            }
        }

        int columnCount = columnNames.Count;
        DataKind[] kinds = new DataKind[columnCount];
        SchemaInferenceDecision?[] decisions = new SchemaInferenceDecision?[columnCount];
        long[] nullCounts = new long[columnCount];

        for (int i = 0; i < columnCount; i++)
        {
            JsonColumnScanState s = states[i];
            nullCounts[i] = s.NullCount;
            (kinds[i], decisions[i]) = FinalizeColumn(ref s);
        }

        sw.Stop();

        return new JsonScanResult(
            ColumnNames: [.. columnNames],
            Kinds: kinds,
            Decisions: decisions,
            RowCount: rowCount,
            NullCountsPerColumn: nullCounts,
            Elapsed: sw.Elapsed);
    }

    // ────────────────────── Per-value state update ──────────────────────

    private static void UpdateColumnState(ref JsonColumnScanState state, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null:
                state.NullCount++;
                return;

            case JsonValueKind.True:
            case JsonValueKind.False:
                state.HasAnyNonNullValue = true;
                state.StringCandidate = false;
                state.IntegerCandidate = false;
                state.Int128Candidate = false;
                state.UInt128Candidate = false;
                state.FloatCandidate = false;
                state.AllFloat32Safe = false;
                return;

            case JsonValueKind.String:
                state.HasAnyNonNullValue = true;
                state.BooleanCandidate = false;
                state.IntegerCandidate = false;
                state.Int128Candidate = false;
                state.UInt128Candidate = false;
                state.FloatCandidate = false;
                state.AllFloat32Safe = false;
                return;

            case JsonValueKind.Number:
                state.HasAnyNonNullValue = true;
                state.StringCandidate = false;
                state.BooleanCandidate = false;

                if (state.IntegerCandidate)
                {
                    if (value.TryGetInt64(out long i))
                    {
                        if (i < state.IntMin) state.IntMin = i;
                        if (i > state.IntMax) state.IntMax = i;
                        if (state.AllFloat32Safe && (i < -(1L << 24) || i > (1L << 24)))
                            state.AllFloat32Safe = false;
                        if (i < 0) state.UInt128Candidate = false;
                    }
                    else
                    {
                        state.IntegerCandidate = false;
                        // Either a 128-bit integer (keep Int128/UInt128 alive) or a
                        // fractional/exponent-form number (drop them). Use the raw
                        // text since JsonElement has no Int128 accessor.
                        string raw = value.GetRawText();
                        bool fitsInt128 = Int128.TryParse(
                            raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                        bool fitsUInt128 = UInt128.TryParse(
                            raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
                        if (!fitsInt128) state.Int128Candidate = false;
                        if (!fitsUInt128) state.UInt128Candidate = false;
                        if (fitsInt128 || fitsUInt128) state.AllFloat32Safe = false;
                    }
                }

                if (state.FloatCandidate)
                {
                    if (value.TryGetDouble(out double d))
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
                return;

            case JsonValueKind.Object:
            case JsonValueKind.Array:
                state.HasAnyNonNullValue = true;
                state.StringCandidate = false;
                state.BooleanCandidate = false;
                state.IntegerCandidate = false;
                state.Int128Candidate = false;
                state.UInt128Candidate = false;
                state.FloatCandidate = false;
                state.AllFloat32Safe = false;
                return;

            case JsonValueKind.Undefined:
            default:
                return;
        }
    }

    private static bool IsFloat32RoundTripSafe(double d)
    {
        if (double.IsNaN(d)) return true;
        if (double.IsPositiveInfinity(d) || double.IsNegativeInfinity(d)) return true;
        float f = (float)d;
        return (double)f == d;
    }

    // ────────────────────── Finalize a column's kind + decision ──────────────────────

    private static (DataKind Kind, SchemaInferenceDecision Decision) FinalizeColumn(ref JsonColumnScanState state)
    {
        if (!state.HasAnyNonNullValue)
        {
            return (DataKind.String, new SchemaInferenceDecision(
                SchemaInferenceReason.AllNull, SchemaInferenceSeverity.Warning,
                "Column contains only null/absent values; defaulting to String.",
                null));
        }

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

        if (state.BooleanCandidate)
        {
            return (DataKind.Boolean, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange, SchemaInferenceSeverity.Routine,
                "All values parsed as JSON true/false.", null));
        }

        if (state.StringCandidate)
        {
            return (DataKind.String, new SchemaInferenceDecision(
                SchemaInferenceReason.NarrowedByObservedRange, SchemaInferenceSeverity.Routine,
                "All values are JSON strings.", null));
        }

        // No primitive family survived: either a value was object/array, or scalar
        // families mixed across rows. Either way, preserve the JSON shape.
        return (DataKind.Json, new SchemaInferenceDecision(
            SchemaInferenceReason.KeptAsJson, SchemaInferenceSeverity.Notable,
            "Column contains nested objects/arrays or mixed primitive types; kept as Json.",
            null));
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
}

/// <summary>Result of a JSON scan pass. Feeds <see cref="JsonDeserializer"/> for pass 2.</summary>
public sealed record JsonScanResult(
    string[] ColumnNames,
    DataKind[] Kinds,
    SchemaInferenceDecision?[] Decisions,
    long RowCount,
    long[] NullCountsPerColumn,
    TimeSpan Elapsed)
{
    /// <summary>Empty result for documents with no rows.</summary>
    public static JsonScanResult Empty(TimeSpan elapsed) => new(
        [], [], [], 0, [], elapsed);
}

/// <summary>
/// Accumulated per-column observations during a JSON scan. Mutable, stored in a list
/// parallel to the column-name list. Each candidate flag starts <c>true</c> and is
/// flipped permanently to <c>false</c> the first time its family's invariant is
/// violated by a non-null value. The column's final kind is the surviving
/// most-specific candidate; if none survives but values were seen, the column is
/// <see cref="DataKind.Json"/>.
/// </summary>
internal struct JsonColumnScanState
{
    public bool IntegerCandidate;
    public bool Int128Candidate;
    public bool UInt128Candidate;
    public bool FloatCandidate;
    public bool BooleanCandidate;
    public bool StringCandidate;
    public bool HasAnyNonNullValue;
    public bool AllFloat32Safe;

    public long IntMin;
    public long IntMax;
    public long NullCount;

    public static JsonColumnScanState Initial() => new()
    {
        IntegerCandidate = true,
        Int128Candidate = true,
        UInt128Candidate = true,
        FloatCandidate = true,
        BooleanCandidate = true,
        StringCandidate = true,
        AllFloat32Safe = true,
        IntMin = long.MaxValue,
        IntMax = long.MinValue,
    };
}
