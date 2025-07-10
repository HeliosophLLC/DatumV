namespace DatumQuery.Statistics.Interactions;

using DatumQuery.Model;

/// <summary>
/// Computes mutual information I(X;Y) = Σ P(x,y) log₂(P(x,y) / (P(x)P(y)))
/// between two columns. Uses reservoir sampling (max <see cref="MaxSamples"/> pairs)
/// and discretizes numeric columns into <see cref="NumericBinCount"/> equal-width bins
/// at result time. Categorical columns use raw string values (capped at
/// <see cref="MaxCategories"/>).
/// </summary>
public sealed class MutualInformationAccumulator
{
    /// <summary>Maximum sample pairs to retain.</summary>
    public const int MaxSamples = 10_000;

    /// <summary>Number of bins for numeric column discretization.</summary>
    public const int NumericBinCount = 20;

    /// <summary>Maximum categorical values before mapping to &lt;other&gt;.</summary>
    public const int MaxCategories = 500;

    private readonly bool _aIsNumeric;
    private readonly bool _bIsNumeric;

    // Reservoir stores extracted values: float for numeric, string for categorical
    private readonly List<(object A, object B)> _reservoir = new();
    private long _totalCount;
    private readonly Random _random = new(42);

    /// <summary>
    /// Initializes a new instance of the <see cref="MutualInformationAccumulator"/> class.
    /// </summary>
    /// <param name="kindA">DataKind of the first column.</param>
    /// <param name="kindB">DataKind of the second column.</param>
    public MutualInformationAccumulator(DataKind kindA, DataKind kindB)
    {
        _aIsNumeric = IsNumericKind(kindA);
        _bIsNumeric = IsNumericKind(kindB);
    }

    /// <summary>
    /// Adds a pair of values to the reservoir.
    /// </summary>
    public void Add(DataValue valueA, DataValue valueB)
    {
        if (valueA.IsNull || valueB.IsNull)
        {
            return;
        }

        object? a = ExtractValue(valueA, _aIsNumeric);
        object? b = ExtractValue(valueB, _bIsNumeric);

        if (a is null || b is null)
        {
            return;
        }

        _totalCount++;

        if (_reservoir.Count < MaxSamples)
        {
            _reservoir.Add((a, b));
        }
        else
        {
            long j = _random.NextInt64(_totalCount);

            if (j < MaxSamples)
            {
                _reservoir[(int)j] = (a, b);
            }
        }
    }

    /// <summary>
    /// Returns the mutual information I(X;Y) ≥ 0 in bits, or NaN if insufficient data.
    /// </summary>
    public double GetValue()
    {
        return GetDetailedValue().MutualInformation;
    }

    /// <summary>
    /// Returns mutual information together with Theil's U (uncertainty coefficient) in both
    /// directions. U(A|B) = MI / H(A) measures how much B reduces uncertainty about A.
    /// </summary>
    public MutualInformationResult GetDetailedValue()
    {
        if (_reservoir.Count < 2)
        {
            return new MutualInformationResult(double.NaN, double.NaN, double.NaN);
        }

        // Discretize values into string keys
        string[] keysA = Discretize(_reservoir.Select(p => p.A).ToList(), _aIsNumeric);
        string[] keysB = Discretize(_reservoir.Select(p => p.B).ToList(), _bIsNumeric);

        int n = _reservoir.Count;

        // Build joint and marginal frequency tables
        Dictionary<string, long> marginalA = new();
        Dictionary<string, long> marginalB = new();
        Dictionary<(string, string), long> joint = new();

        for (int i = 0; i < n; i++)
        {
            string a = keysA[i];
            string b = keysB[i];

            marginalA[a] = marginalA.GetValueOrDefault(a) + 1;
            marginalB[b] = marginalB.GetValueOrDefault(b) + 1;

            (string, string) key = (a, b);
            joint[key] = joint.GetValueOrDefault(key) + 1;
        }

        // Compute MI
        double mi = 0;

        foreach (KeyValuePair<(string, string), long> entry in joint)
        {
            double pJoint = (double)entry.Value / n;
            double pA = (double)marginalA[entry.Key.Item1] / n;
            double pB = (double)marginalA.GetValueOrDefault(entry.Key.Item1, 1) > 0
                ? (double)marginalB[entry.Key.Item2] / n
                : 0;

            if (pJoint > 0 && pA > 0 && pB > 0)
            {
                mi += pJoint * Math.Log2(pJoint / (pA * pB));
            }
        }

        mi = Math.Max(0, mi); // MI is non-negative; clamp numerical errors

        // Compute marginal entropies for Theil's U
        double hA = ComputeEntropy(marginalA, n);
        double hB = ComputeEntropy(marginalB, n);

        double theilUAB = hA > 0 ? mi / hA : double.NaN;
        double theilUBA = hB > 0 ? mi / hB : double.NaN;

        return new MutualInformationResult(mi, theilUAB, theilUBA);
    }

    private static double ComputeEntropy(Dictionary<string, long> frequencies, int n)
    {
        double h = 0;

        foreach (long count in frequencies.Values)
        {
            double p = (double)count / n;

            if (p > 0)
            {
                h -= p * Math.Log2(p);
            }
        }

        return h;
    }

    private static string[] Discretize(List<object> values, bool isNumeric)
    {
        int n = values.Count;
        string[] keys = new string[n];

        if (isNumeric)
        {
            // Find min and max from float values
            float min = float.MaxValue;
            float max = float.MinValue;

            for (int i = 0; i < n; i++)
            {
                float v = (float)values[i];

                if (v < min)
                {
                    min = v;
                }

                if (v > max)
                {
                    max = v;
                }
            }

            float range = max - min;

            for (int i = 0; i < n; i++)
            {
                float v = (float)values[i];
                int bin = range < float.Epsilon
                    ? 0
                    : Math.Clamp((int)((v - min) / range * NumericBinCount), 0, NumericBinCount - 1);
                keys[i] = $"bin_{bin}";
            }
        }
        else
        {
            // Categorical: use string directly, cap at MaxCategories
            Dictionary<string, int> categoryMap = new();
            int categoryIndex = 0;

            for (int i = 0; i < n; i++)
            {
                string s = (string)values[i];

                if (!categoryMap.ContainsKey(s))
                {
                    if (categoryMap.Count < MaxCategories)
                    {
                        categoryMap[s] = categoryIndex++;
                    }
                    else
                    {
                        keys[i] = "<other>";
                        continue;
                    }
                }

                keys[i] = s;
            }
        }

        return keys;
    }

    private static object? ExtractValue(DataValue value, bool isNumeric)
    {
        if (isNumeric)
        {
            return value.Kind switch
            {
                DataKind.Scalar => value.AsScalar(),
                DataKind.UInt8 => (float)value.AsUInt8(),
                _ => null
            };
        }

        return value.Kind switch
        {
            DataKind.String => value.AsString(),
            DataKind.JsonValue => value.AsJsonValue(),
            DataKind.Date => value.AsDate().ToString("O"),
            DataKind.DateTime => value.AsDateTime().ToString("O"),
            _ => null
        };
    }

    private static bool IsNumericKind(DataKind kind)
    {
        return kind is DataKind.Scalar or DataKind.UInt8;
    }
}

/// <summary>
/// Contains mutual information and Theil's U (uncertainty coefficient) values.
/// </summary>
/// <param name="MutualInformation">Mutual information I(X;Y) ≥ 0 in bits.</param>
/// <param name="TheilUAB">U(A|B) = MI / H(A). How much column B reduces uncertainty about column A. Range [0, 1].</param>
/// <param name="TheilUBA">U(B|A) = MI / H(B). How much column A reduces uncertainty about column B. Range [0, 1].</param>
public readonly record struct MutualInformationResult(
    double MutualInformation,
    double TheilUAB,
    double TheilUBA);
