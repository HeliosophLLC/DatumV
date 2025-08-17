namespace DatumIngest.Manifest;

/// <summary>
/// Semantic role of a column, inferred from its <see cref="Model.DataKind"/> and
/// statistical profile. Used by cross-manifest join scoring to gate candidate
/// pairs — only columns with compatible roles can form join edges.
/// </summary>
public enum ColumnRole
{
    /// <summary>
    /// A primary key or natural key column: high cardinality relative to the row count,
    /// integer or UUID typed, low null ratio.
    /// </summary>
    Identifier,

    /// <summary>
    /// A foreign key column referencing another table's identifier: integer or UUID typed,
    /// moderate repetition (cardinality below the row count).
    /// </summary>
    ForeignKey,

    /// <summary>
    /// A discrete/categorical column with a bounded vocabulary: low cardinality relative
    /// to row count, concentrated frequency distribution.
    /// </summary>
    Categorical,

    /// <summary>
    /// A continuous numeric measure: floating-point or high-cardinality integer with
    /// fractional values or smooth distribution. Typically never a join key.
    /// </summary>
    Measure,

    /// <summary>
    /// A temporal column: date, datetime, time, or duration kind.
    /// </summary>
    Temporal,

    /// <summary>
    /// A free-form text column: string with high cardinality and long average length.
    /// </summary>
    Text,

    /// <summary>
    /// Raw binary data: byte arrays or image data.
    /// </summary>
    Binary,

    /// <summary>
    /// A multi-dimensional numeric column: vector, matrix, or tensor kind.
    /// </summary>
    Structural
}
