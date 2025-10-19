namespace DatumIngest.Ingestion.Sampling;

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
