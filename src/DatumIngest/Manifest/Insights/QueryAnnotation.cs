namespace Heliosoph.DatumV.Manifest.Insights;

using System.Text.Json.Serialization;

/// <summary>
/// Maps a column in the synthesized query output back to the <see cref="DatasetInsight"/>
/// that produced it. Separated from the executable SQL so that queries remain paste-safe.
/// </summary>
/// <param name="Column">Column name in the query output.</param>
/// <param name="InsightKind">The <see cref="DatasetInsight.Kind"/> that drove this column's transformation.</param>
/// <param name="Note">Brief human-readable explanation of the transformation.</param>
/// <param name="Confidence">Confidence of the originating insight, enabling consumer-side partitioning of manual actions.</param>
public sealed record QueryAnnotation(
    string Column,
    [property: JsonConverter(typeof(JsonStringEnumConverter<InsightKind>))]
    InsightKind InsightKind,
    string Note,
    double Confidence);
