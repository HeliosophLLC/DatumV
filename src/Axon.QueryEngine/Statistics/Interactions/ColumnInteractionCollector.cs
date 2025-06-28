namespace Axon.QueryEngine.Statistics.Interactions;

using Axon.QueryEngine.Model;

/// <summary>
/// Manages pairwise interaction accumulators for all eligible column pairs in a result set.
/// Discovers columns on the first row, instantiates accumulators per pair based on column
/// kind combinations, and produces <see cref="ColumnInteractionResult"/> records.
/// </summary>
public sealed class ColumnInteractionCollector
{
    private bool _initialized;
    private List<PairState> _pairs = new();

    /// <summary>
    /// Adds a row of values to all pairwise accumulators.
    /// On first call, discovers eligible columns and creates accumulators.
    /// </summary>
    public void AddRow(Row row)
    {
        if (!_initialized)
        {
            Initialize(row);
            _initialized = true;
        }

        foreach (PairState pair in _pairs)
        {
            DataValue valueA = row[pair.ColumnA];
            DataValue valueB = row[pair.ColumnB];

            pair.Pearson?.Add(valueA, valueB);
            pair.Spearman?.Add(valueA, valueB);
            pair.CramerV?.Add(valueA, valueB);
            pair.Anova?.Add(valueA, valueB);
            pair.MutualInformation?.Add(valueA, valueB);
        }
    }

    /// <summary>
    /// Computes and returns interaction results for all column pairs.
    /// </summary>
    public IReadOnlyList<ColumnInteractionResult> GetInteractions()
    {
        List<ColumnInteractionResult> results = new(_pairs.Count);

        foreach (PairState pair in _pairs)
        {
            double? pearson = NanToNull(pair.Pearson?.GetValue());
            double? spearman = NanToNull(pair.Spearman?.GetValue());
            double? cramerV = NanToNull(pair.CramerV?.GetValue());
            double? anovaF = NanToNull(pair.Anova?.GetValue());
            double? mi = NanToNull(pair.MutualInformation?.GetValue());

            results.Add(new ColumnInteractionResult(
                pair.ColumnA,
                pair.ColumnB,
                pearson,
                spearman,
                cramerV,
                anovaF,
                mi));
        }

        return results;
    }

    private void Initialize(Row row)
    {
        // Discover eligible columns (skip Image, UInt8Array, Vector, Matrix, Tensor)
        List<(string Name, DataKind Kind)> eligible = new();

        foreach (string columnName in row.ColumnNames)
        {
            DataKind kind = row[columnName].Kind;

            if (IsEligibleKind(kind))
            {
                eligible.Add((columnName, kind));
            }
        }

        // Create accumulators for each pair
        for (int i = 0; i < eligible.Count; i++)
        {
            for (int j = i + 1; j < eligible.Count; j++)
            {
                (string nameA, DataKind kindA) = eligible[i];
                (string nameB, DataKind kindB) = eligible[j];

                bool aIsNumeric = IsNumericKind(kindA);
                bool bIsNumeric = IsNumericKind(kindB);
                bool aIsCategorical = IsCategoricalKind(kindA);
                bool bIsCategorical = IsCategoricalKind(kindB);

                PairState pair = new()
                {
                    ColumnA = nameA,
                    ColumnB = nameB
                };

                // Numeric × Numeric: Pearson, Spearman, MI
                if (aIsNumeric && bIsNumeric)
                {
                    pair.Pearson = new PearsonAccumulator();
                    pair.Spearman = new SpearmanAccumulator();
                    pair.MutualInformation = new MutualInformationAccumulator(kindA, kindB);
                }
                // Categorical × Categorical: Cramér's V, MI
                else if (aIsCategorical && bIsCategorical)
                {
                    pair.CramerV = new CramerVAccumulator();
                    pair.MutualInformation = new MutualInformationAccumulator(kindA, kindB);
                }
                // Mixed: ANOVA, MI
                else if ((aIsNumeric && bIsCategorical) || (aIsCategorical && bIsNumeric))
                {
                    pair.Anova = new AnovaAccumulator(firstIsCategorical: aIsCategorical);
                    pair.MutualInformation = new MutualInformationAccumulator(kindA, kindB);
                }

                _pairs.Add(pair);
            }
        }
    }

    private static bool IsNumericKind(DataKind kind)
    {
        return kind is DataKind.Scalar or DataKind.UInt8;
    }

    private static bool IsCategoricalKind(DataKind kind)
    {
        return kind is DataKind.String or DataKind.JsonValue or DataKind.Date or DataKind.DateTime;
    }

    private static bool IsEligibleKind(DataKind kind)
    {
        return IsNumericKind(kind) || IsCategoricalKind(kind);
    }

    private static double? NanToNull(double? value)
    {
        return value is null || double.IsNaN(value.Value) ? null : value;
    }

    private sealed class PairState
    {
        public required string ColumnA { get; init; }
        public required string ColumnB { get; init; }
        public PearsonAccumulator? Pearson { get; set; }
        public SpearmanAccumulator? Spearman { get; set; }
        public CramerVAccumulator? CramerV { get; set; }
        public AnovaAccumulator? Anova { get; set; }
        public MutualInformationAccumulator? MutualInformation { get; set; }
    }
}

/// <summary>
/// Result of interaction analysis between two columns.
/// </summary>
/// <param name="ColumnA">Name of the first column.</param>
/// <param name="ColumnB">Name of the second column.</param>
/// <param name="Pearson">Pearson correlation (numeric × numeric only).</param>
/// <param name="Spearman">Spearman rank correlation (numeric × numeric only).</param>
/// <param name="CramerV">Cramér's V association (categorical × categorical only).</param>
/// <param name="AnovaFStatistic">ANOVA F-statistic (categorical × numeric only).</param>
/// <param name="MutualInformation">Mutual information in bits (all pair types).</param>
public sealed record ColumnInteractionResult(
    string ColumnA,
    string ColumnB,
    double? Pearson,
    double? Spearman,
    double? CramerV,
    double? AnovaFStatistic,
    double? MutualInformation);
