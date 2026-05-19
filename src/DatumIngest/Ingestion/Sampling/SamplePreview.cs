using Heliosoph.DatumV.Functions.Json;

namespace Heliosoph.DatumV.Ingestion.Sampling;

/// <summary>
/// A lightweight preview of sample records from an ingested table, suitable for
/// JSON serialisation and display in client UIs. Contains a feature list describing
/// each column and a fixed-size array of sample rows collected via reservoir sampling.
/// </summary>
public sealed class SamplePreview
{
    /// <summary>
    /// Gets the ordered list of features (columns) describing the structure of each sample row.
    /// </summary>
    public required IReadOnlyList<SampleFeature> Features { get; init; }

    /// <summary>
    /// Gets the sample rows. Each inner array has one element per <see cref="Features"/> entry,
    /// in the same order. Values are JSON-friendly primitives: numbers, strings, booleans,
    /// <c>null</c>, nested arrays (for vectors/matrices/tensors), or base64-encoded image strings.
    /// </summary>
    public required IReadOnlyList<object?[]> Samples { get; init; }
}

/// <summary>
/// Describes a single feature (column) in a <see cref="SamplePreview"/>.
/// </summary>
/// <param name="Name">The column name.</param>
/// <param name="Kind">
/// The lowercased <see cref="Model.DataKind"/> name (e.g. <c>"float32"</c>, <c>"image"</c>, <c>"vector"</c>).
/// </param>
public sealed record SampleFeature(string Name, string Kind);

/// <summary>
/// Cell value for a <see cref="Model.DataKind.Json"/> column in a
/// <see cref="SamplePreview.Samples"/> row. Carries the partial-but-valid JSON
/// text of the cell's CBOR payload alongside optional truncation metadata when
/// the value exceeded the preview budget. Serialized inline as a JSON object
/// of shape <c>{ "kind": "json", "text": "...", "preview": { "total": N,
/// "shown": K, "mode": "array" } }</c> — the <c>kind</c> discriminator marks
/// it for the deserializer so we don't conflate it with a regular embedded
/// object literal.
/// </summary>
/// <param name="Text">Partial JSON text of the cell's value.</param>
/// <param name="Preview">Truncation metadata; <c>null</c> when the value fit
/// in the budget and the text is complete.</param>
public sealed record JsonSamplePreview(string Text, JsonPreviewInfo? Preview);
