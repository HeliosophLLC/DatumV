namespace DatumIngest.Manifest;

/// <summary>
/// Records the decision made by the full-file schema scanner for a single column,
/// co-located on its <see cref="FeatureManifest"/>. Null when the ingester used
/// sample-based inference or an externally-declared schema.
/// </summary>
/// <param name="Reason">The rule that produced the chosen <see cref="FeatureManifest.Kind"/>.</param>
/// <param name="Severity">
/// Whether this decision is routine (silent good news), notable (user likely wants to
/// know), or warning (may indicate a data-quality issue worth investigating).
/// </param>
/// <param name="Explanation">
/// One-line human-readable summary suitable for direct display. Typically generated
/// from <see cref="Reason"/> + <see cref="Evidence"/>.
/// </param>
/// <param name="Evidence">
/// Structured signals the scanner observed: e.g. <c>observed_min</c>,
/// <c>observed_max</c>, <c>narrowed_from</c>, <c>matched_format</c>,
/// <c>leading_zero_example</c>. Null when the decision needs no supporting data.
/// </param>
public sealed record SchemaInferenceDecision(
    SchemaInferenceReason Reason,
    SchemaInferenceSeverity Severity,
    string Explanation,
    IReadOnlyDictionary<string, object>? Evidence);

/// <summary>
/// Why the scanner chose a particular <see cref="Model.DataKind"/> for a column.
/// </summary>
public enum SchemaInferenceReason
{
    /// <summary>
    /// Sample-based inference ran; full-file scan was disabled. The decision reflects
    /// the first N rows only and may not hold across the whole dataset.
    /// </summary>
    SampleInference,

    /// <summary>
    /// Integer column narrowed from <see cref="Model.DataKind.Int64"/> to the smallest
    /// signed/unsigned type that fits the observed <c>[min, max]</c> range.
    /// </summary>
    NarrowedByObservedRange,

    /// <summary>
    /// At least one value in the column had a leading zero (e.g. <c>"02931"</c>) so
    /// the column was kept as <see cref="Model.DataKind.String"/> to preserve the
    /// original formatting. Narrowing to an integer type would silently drop the
    /// zero padding and break roundtrip.
    /// </summary>
    KeptAsStringLeadingZeros,

    /// <summary>
    /// Values in the column parsed as multiple incompatible types across the file
    /// (e.g. some integer, some text), so the column was kept as
    /// <see cref="Model.DataKind.String"/>.
    /// </summary>
    KeptAsStringMixedFormats,

    /// <summary>
    /// All non-null values matched one of the scanner's candidate date/time formats.
    /// The matching format string is available in <see cref="SchemaInferenceDecision.Evidence"/>
    /// under the <c>matched_format</c> key.
    /// </summary>
    DateFormatMatched,

    /// <summary>
    /// All non-null values parsed as <see cref="DateTimeOffset"/> via the BCL's
    /// flexible parser, but none of the scanner's candidate formats matched. Parsing
    /// on the ingest pass will use the slower <c>TryParse</c> path for this column.
    /// </summary>
    DateFormatFallback,

    /// <summary>
    /// Float column narrowed from <see cref="Model.DataKind.Float64"/> to
    /// <see cref="Model.DataKind.Float32"/> because every observed value round-trips
    /// through <c>single</c> precision without loss.
    /// </summary>
    FloatNarrowedToFloat32,

    /// <summary>
    /// Every value in the column was null/empty. The <see cref="FeatureManifest.Kind"/>
    /// is a best-effort default and may not reflect the intended type.
    /// </summary>
    AllNull,

    /// <summary>
    /// Column was kept as <see cref="Model.DataKind.Json"/> because at least one value
    /// was a nested object/array, or because scalar values across rows did not share a
    /// single primitive type. The original JSON shape is preserved for query-time access
    /// via the <c>json_*</c> function family.
    /// </summary>
    KeptAsJson,
}

/// <summary>
/// Attention level assigned to a <see cref="SchemaInferenceDecision"/>, used to
/// surface noteworthy decisions without polluting the audit trail for routine ones.
/// </summary>
public enum SchemaInferenceSeverity
{
    /// <summary>
    /// A silent, expected decision. The scanner did what a reasonable engineer would
    /// have done by hand (e.g. narrowing a 5-digit-range integer to Int16).
    /// </summary>
    Routine,

    /// <summary>
    /// A decision the user is likely to want to know about but that does not indicate
    /// a data-quality problem — e.g. leading-zero preservation of a code column.
    /// </summary>
    Notable,

    /// <summary>
    /// A decision that may indicate a data-quality issue worth investigating — e.g.
    /// an all-null column or mixed types forcing a fallback to String.
    /// </summary>
    Warning,
}
