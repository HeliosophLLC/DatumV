namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Machine-readable identifier for each insight pattern. Atomic kinds represent a single
/// detection (e.g., <see cref="HighMissingness"/>); compound kinds represent syndromes
/// where multiple conditions co-occur and warrant bundled treatment
/// (e.g., <see cref="ZeroInflatedSkewedNumeric"/>).
/// </summary>
public enum InsightKind
{
    // ── Data Quality (atomic) ──

    /// <summary>Column has more than 30% null values.</summary>
    HighMissingness,

    /// <summary>Column has more than 80% null values — likely unusable without external data.</summary>
    CriticalMissingness,

    /// <summary>Two or more columns share a missingness correlation above threshold, suggesting structural gaps.</summary>
    CorrelatedMissingness,

    /// <summary>Missingness is informative — the null/non-null pattern itself carries signal.</summary>
    InformativeMissingness,

    /// <summary>Column has zero variance (all non-null values identical).</summary>
    ConstantFeature,

    // ── Distribution (atomic) ──

    /// <summary>More than half the values are zero, inflating the distribution.</summary>
    ZeroInflated,

    /// <summary>Right-skewed distribution (skewness above threshold).</summary>
    RightSkewed,

    /// <summary>Left-skewed distribution (skewness below negative threshold).</summary>
    LeftSkewed,

    /// <summary>Heavy-tailed distribution (excess kurtosis above threshold).</summary>
    HeavyTailed,

    /// <summary>Outlier ratio exceeds threshold (Z-score based).</summary>
    ExtremeOutliers,

    // ── Encoding (atomic) ──

    /// <summary>Integer-valued numeric column with few distinct values — may be ordinal.</summary>
    PossibleOrdinal,

    /// <summary>Low-cardinality string column suitable for one-hot encoding.</summary>
    LowCardinalityCategorical,

    /// <summary>High-cardinality string column where one-hot would explode dimensionality.</summary>
    HighCardinalityCategorical,

    /// <summary>Column has exactly two distinct non-null values — already binary or should be encoded as such.</summary>
    BinaryFeature,

    // ── Redundancy (atomic) ──

    /// <summary>Two numeric columns have near-perfect linear correlation.</summary>
    NearDuplicateNumeric,

    /// <summary>Two categorical columns have near-perfect association (Cramér's V).</summary>
    NearDuplicateCategorical,

    /// <summary>One column is a deterministic function of another.</summary>
    FunctionalDependency,

    // ── Dimensionality (atomic) ──

    /// <summary>Column has near-unique values — likely an identifier rather than a feature.</summary>
    PossibleIdentifier,

    // ── Scale (atomic) ──

    /// <summary>Numeric column has a range vastly exceeding its mean — normalization recommended.</summary>
    NormalizationNeeded,

    // ── Image Quality (atomic) ──

    /// <summary>Image column contains images below minimum resolution threshold.</summary>
    TinyImages,

    /// <summary>Image column contains images above maximum resolution threshold.</summary>
    HugeImages,

    /// <summary>Image column contains files that failed to decode.</summary>
    UndecodableImages,

    // ── Syndromes (compound) ──

    /// <summary>
    /// Zero-inflated column that is also skewed and/or heavy-tailed.
    /// Treatment: bundled indicator + conditional log-transform + clip at nonzero P99.
    /// </summary>
    ZeroInflatedSkewedNumeric,

    /// <summary>
    /// Non-normal distribution without zero-inflation: skewed + heavy-tailed + outliers.
    /// Treatment: log-transform + clip.
    /// </summary>
    NonNormalDistribution,

    /// <summary>
    /// Correlated missingness across multiple columns, indicating a systematic data gap.
    /// Treatment: group indicator.
    /// </summary>
    SystematicDataGap,

    /// <summary>
    /// Near-duplicate or functionally dependent columns suggesting data leakage risk.
    /// Treatment: keep one.
    /// </summary>
    FeatureLeakageRisk,

    /// <summary>
    /// Column is both constant and critically missing — provides no information.
    /// Treatment: drop.
    /// </summary>
    UnusableFeature,

    // ── Cross-Manifest (atomic) ──

    /// <summary>A join candidate is classified as many-to-many, producing a Cartesian product.</summary>
    ManyToManyJoin,

    /// <summary>A join candidate has a high null-key ratio — inner joins will silently drop rows.</summary>
    HighNullKey,

    /// <summary>A join candidate has a large cardinality mismatch between join keys.</summary>
    CardinalityMismatch,

    /// <summary>A join candidate's key columns have disjoint value ranges — the join will produce few or no matches.</summary>
    DisjointRange,

    /// <summary>Columns with the same name have different types across tables, suggesting schema drift.</summary>
    SchemaDrift,

    /// <summary>Multiple tables share overlapping columns with high value overlap, suggesting denormalized data.</summary>
    DenormalizationHint,

    /// <summary>
    /// Two or more tables share identical schemas and connect to the same hub tables,
    /// indicating partitions of the same entity (e.g., train/test splits).
    /// </summary>
    EquivalentTablePartition,

    /// <summary>
    /// Two or more tables share the same schema with a temporal column whose date ranges
    /// are disjoint, indicating time-based partitions suitable for UNION ALL.
    /// </summary>
    TemporalPartition,

    /// <summary>
    /// Two or more tables have near-identical schemas and overlapping values, suggesting
    /// redundant copies of the same data. One copy should be dropped.
    /// </summary>
    DuplicateRepresentation,

    /// <summary>
    /// Two or more tables share most column names but have type differences on some columns,
    /// suggesting schema evolution or incompatible data sources.
    /// </summary>
    NearDuplicateSchema,

    /// <summary>
    /// The join graph has high structural complexity — many edges relative to
    /// the number of tables, indicating ambiguous join paths. Consider manual
    /// join specification.
    /// </summary>
    DenseJoinGraph,
}
