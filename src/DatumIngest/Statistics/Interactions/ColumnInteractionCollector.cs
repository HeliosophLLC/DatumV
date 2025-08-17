namespace DatumIngest.Statistics.Interactions;

using DatumIngest.Model;

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
            pair.MissingnessCorrelation?.Add(valueA, valueB);
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

            MutualInformationResult? miResult = pair.MutualInformation?.GetDetailedValue();
            double? mi = NanToNull(miResult?.MutualInformation);
            double? theilUAB = NanToNull(miResult?.TheilUAB);
            double? theilUBA = NanToNull(miResult?.TheilUBA);

            double? missingnessCorrelation = NanToNull(pair.MissingnessCorrelation?.GetValue());

            results.Add(new ColumnInteractionResult(
                pair.ColumnA,
                pair.ColumnB,
                pearson,
                spearman,
                cramerV,
                anovaF,
                mi,
                theilUAB,
                theilUBA,
                missingnessCorrelation));
        }

        return results;
    }

    private void Initialize(Row row)
    {
        // Discover all columns for pairwise interaction analysis
        List<(string Name, DataKind Kind)> columns = new();

        foreach (string columnName in row.ColumnNames)
        {
            DataKind kind = row[columnName].Kind;
            columns.Add((columnName, kind));
        }

        // Create accumulators for each pair
        for (int i = 0; i < columns.Count; i++)
        {
            for (int j = i + 1; j < columns.Count; j++)
            {
                (string nameA, DataKind kindA) = columns[i];
                (string nameB, DataKind kindB) = columns[j];

                bool aIsNumeric = IsNumericKind(kindA);
                bool bIsNumeric = IsNumericKind(kindB);
                bool aIsCategorical = IsCategoricalKind(kindA);
                bool bIsCategorical = IsCategoricalKind(kindB);

                PairState pair = new()
                {
                    ColumnA = nameA,
                    ColumnB = nameB,
                    MissingnessCorrelation = new MissingnessCorrelationAccumulator()
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
                // Mixed (numeric × categorical): ANOVA, MI
                else if ((aIsNumeric && bIsCategorical) || (aIsCategorical && bIsNumeric))
                {
                    pair.Anova = new AnovaAccumulator(firstIsCategorical: aIsCategorical);
                    pair.MutualInformation = new MutualInformationAccumulator(kindA, kindB);
                }
                // Otherwise (at least one ineligible kind): missingness correlation only

                _pairs.Add(pair);
            }
        }
    }

    private static bool IsNumericKind(DataKind kind)
    {
        return kind is DataKind.Float32 or DataKind.UInt8;
    }

    private static bool IsCategoricalKind(DataKind kind)
    {
        return kind is DataKind.String or DataKind.JsonValue or DataKind.Date or DataKind.DateTime;
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
        public MissingnessCorrelationAccumulator? MissingnessCorrelation { get; set; }
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
/// <param name="TheilUAB">Theil's U(A|B): how much column B reduces uncertainty about column A. Range [0, 1].</param>
/// <param name="TheilUBA">Theil's U(B|A): how much column A reduces uncertainty about column B. Range [0, 1].</param>
/// <param name="MissingnessCorrelation">Pearson correlation between null masks of the two columns. Range [-1, 1].</param>
public sealed record ColumnInteractionResult(
    string ColumnA,
    string ColumnB,
    double? Pearson,
    double? Spearman,
    double? CramerV,
    double? AnovaFStatistic,
    double? MutualInformation,
    double? TheilUAB,
    double? TheilUBA,
    double? MissingnessCorrelation);
