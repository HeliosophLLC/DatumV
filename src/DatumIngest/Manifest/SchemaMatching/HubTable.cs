namespace DatumIngest.Manifest.SchemaMatching;

/// <summary>
/// A hub (dimension/parent) table at the center of a star schema relationship. The hub
/// holds unique key values that are referenced by one or more <see cref="SpokeTable"/>
/// instances via foreign key relationships.
/// </summary>
public sealed class HubTable
{
    /// <summary>Gets the name of the hub table.</summary>
    public required string TableName { get; init; }

    /// <summary>
    /// Gets the key columns in the hub table that serve as the unique key for this star.
    /// Single-element for surrogate keys; multiple elements for composite keys.
    /// </summary>
    public required IReadOnlyList<string> KeyColumns { get; init; }

    /// <summary>Gets the spoke tables connected to this hub via foreign key relationships.</summary>
    public required IReadOnlyList<SpokeTable> Spokes { get; init; }

    /// <summary>Gets the number of spoke tables connected to this hub.</summary>
    public int SpokeCount => Spokes.Count;
}
