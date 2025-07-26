namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Classifies the domain of a <see cref="DatasetInsight"/>.
/// </summary>
public enum InsightCategory
{
    /// <summary>Missing values, constant features, data type anomalies.</summary>
    DataQuality,

    /// <summary>Skewness, kurtosis, zero-inflation, outliers.</summary>
    Distribution,

    /// <summary>Cardinality, ordinal detection, one-hot recommendations.</summary>
    Encoding,

    /// <summary>Near-duplicate features, high correlation, functional dependencies.</summary>
    Redundancy,

    /// <summary>High-cardinality identifiers, possible feature reduction.</summary>
    Dimensionality,

    /// <summary>Normalization, log-transform, clipping recommendations.</summary>
    Scale,

    /// <summary>Image resolution, aspect ratio, decodability issues.</summary>
    ImageQuality,

    /// <summary>Temporal range, granularity, cyclical encoding opportunities.</summary>
    TemporalQuality,

    /// <summary>Cross-manifest join quality, schema drift, normalization hints.</summary>
    JoinQuality
}
