namespace DatumIngest.Manifest.CrossManifest;

using DatumIngest.Model;

/// <summary>
/// Discovers candidate column pairs across two manifests using name similarity
/// and type compatibility. Pairs that pass initial screening are forwarded to
/// <see cref="JoinEvidenceScorer"/> for full evidence computation.
/// </summary>
internal static class ColumnMatcher
{
    /// <summary>
    /// Bonus applied to name similarity when both column names share a common
    /// join-key suffix such as "_id", "_key", or "_code".
    /// </summary>
    private const double SuffixBonus = 0.15;

    /// <summary>
    /// Common suffixes that indicate join-key columns.
    /// </summary>
    private static readonly string[] JoinKeySuffixes =
        ["_id", "id", "_key", "key", "_code", "code"];

    /// <summary>
    /// Minimum cardinality ratio (min NDV / max NDV) for two Categorical columns
    /// to be considered a candidate pair. Categoricals with very different domain
    /// sizes are unlikely to be join keys.
    /// </summary>
    private const double CategoricalNdvRatioThreshold = 0.5;

    /// <summary>
    /// Finds candidate column pairs between two manifests that pass the name similarity
    /// and type compatibility thresholds.
    /// </summary>
    /// <param name="left">The left table manifest.</param>
    /// <param name="right">The right table manifest.</param>
    /// <param name="thresholds">Thresholds controlling match sensitivity.</param>
    /// <returns>Column pairs that exceed the matching thresholds.</returns>
    internal static IReadOnlyList<ColumnMatchCandidate> FindCandidatePairs(
        ManifestWithName left,
        ManifestWithName right,
        CrossManifestThresholds thresholds)
    {
        List<ColumnMatchCandidate> candidates = new();

        foreach (FeatureManifest leftFeature in left.Manifest.Features)
        {
            foreach (FeatureManifest rightFeature in right.Manifest.Features)
            {
                // Role-based gate: when both sides have roles assigned, reject
                // structurally implausible join pairs early.
                if (!IsRolePairJoinable(leftFeature, rightFeature))
                {
                    continue;
                }

                double nameSimilarity = ComputeNameSimilarity(leftFeature.Name, rightFeature.Name);
                double typeCompatibility = ComputeTypeCompatibility(leftFeature.Kind, rightFeature.Kind);

                // Early pruning: both name and type must contribute.
                if (nameSimilarity < thresholds.NameSimilarityMinThreshold &&
                    typeCompatibility < thresholds.TypeCompatibilityMinThreshold)
                {
                    continue;
                }

                // At least one must be non-trivial.
                if (nameSimilarity < 0.01 && typeCompatibility < 0.01)
                {
                    continue;
                }

                candidates.Add(new ColumnMatchCandidate(
                    leftFeature.Name,
                    rightFeature.Name,
                    nameSimilarity,
                    typeCompatibility));
            }
        }

        return candidates;
    }

    /// <summary>
    /// Computes normalized name similarity between two column names using
    /// Levenshtein distance with a bonus for common join-key suffixes.
    /// </summary>
    internal static double ComputeNameSimilarity(string left, string right)
    {
        string normalizedLeft = left.ToLowerInvariant();
        string normalizedRight = right.ToLowerInvariant();

        if (normalizedLeft == normalizedRight)
        {
            return 1.0;
        }

        int maxLength = Math.Max(normalizedLeft.Length, normalizedRight.Length);

        if (maxLength == 0)
        {
            return 1.0;
        }

        int distance = LevenshteinDistance(normalizedLeft, normalizedRight);
        double baseSimilarity = 1.0 - ((double)distance / maxLength);

        // Apply suffix bonus if both names share a common join-key suffix.
        bool hasSuffixMatch = false;

        foreach (string suffix in JoinKeySuffixes)
        {
            if (normalizedLeft.EndsWith(suffix, StringComparison.Ordinal) &&
                normalizedRight.EndsWith(suffix, StringComparison.Ordinal))
            {
                hasSuffixMatch = true;
                break;
            }
        }

        double similarity = hasSuffixMatch
            ? Math.Min(baseSimilarity + SuffixBonus, 1.0)
            : baseSimilarity;

        return Math.Max(similarity, 0.0);
    }

    /// <summary>
    /// Computes type compatibility between two <see cref="DataKind"/> values.
    /// Returns 1.0 for exact match, 0.5–0.8 for coercible types, 0.0 for incompatible.
    /// </summary>
    internal static double ComputeTypeCompatibility(DataKind left, DataKind right)
    {
        if (left == right)
        {
            return 1.0;
        }

        // All numeric types are coercible with varying compatibility.
        if (IsNumericKind(left) && IsNumericKind(right))
        {
            // Same sign family, different width (e.g. Int16 ↔ Int32)
            if (IsSignedInteger(left) && IsSignedInteger(right))
            {
                return 0.9;
            }

            if (IsUnsignedInteger(left) && IsUnsignedInteger(right))
            {
                return 0.9;
            }

            // Float ↔ Float (e.g. Float32 ↔ Float64)
            if (IsFloatingPoint(left) && IsFloatingPoint(right))
            {
                return 0.9;
            }

            // Signed ↔ unsigned integer
            if ((IsSignedInteger(left) && IsUnsignedInteger(right)) ||
                (IsUnsignedInteger(left) && IsSignedInteger(right)))
            {
                return 0.85;
            }

            // Integer ↔ float
            return 0.7;
        }

        // Temporal coercion: Date ↔ DateTime.
        if ((left is DataKind.Date && right is DataKind.DateTime) ||
            (left is DataKind.DateTime && right is DataKind.Date))
        {
            return 0.8;
        }

        // String-like: String ↔ JsonValue.
        if ((left is DataKind.String && right is DataKind.JsonValue) ||
            (left is DataKind.JsonValue && right is DataKind.String))
        {
            return 0.5;
        }

        return 0.0;
    }

    private static bool IsNumericKind(DataKind kind) =>
        kind is DataKind.Float32 or DataKind.Float64
            or DataKind.UInt8 or DataKind.Int8
            or DataKind.Int16 or DataKind.UInt16
            or DataKind.Int32 or DataKind.UInt32
            or DataKind.Int64 or DataKind.UInt64;

    private static bool IsSignedInteger(DataKind kind) =>
        kind is DataKind.Int8 or DataKind.Int16 or DataKind.Int32 or DataKind.Int64;

    private static bool IsUnsignedInteger(DataKind kind) =>
        kind is DataKind.UInt8 or DataKind.UInt16 or DataKind.UInt32 or DataKind.UInt64;

    private static bool IsFloatingPoint(DataKind kind) =>
        kind is DataKind.Float32 or DataKind.Float64;

    /// <summary>
    /// Determines whether a pair of columns is structurally plausible as a join
    /// based on their classified <see cref="ColumnRole"/> values. When either side
    /// has no role assigned (null), the pair is allowed through to preserve backward
    /// compatibility with manifests that predate role classification.
    /// </summary>
    /// <remarks>
    /// Rules:
    /// <list type="bullet">
    ///   <item>Both sides <see cref="ColumnRole.Measure"/> → blocked (continuous values never join).</item>
    ///   <item>Both sides <see cref="ColumnRole.Structural"/> → blocked (vectors/tensors never join).</item>
    ///   <item>At least one side must be <see cref="ColumnRole.Identifier"/> or <see cref="ColumnRole.ForeignKey"/>,
    ///         unless both sides are <see cref="ColumnRole.Categorical"/> with similar NDV.</item>
    /// </list>
    /// </remarks>
    internal static bool IsRolePairJoinable(FeatureManifest left, FeatureManifest right)
    {
        ColumnRole? leftRole = left.Role;
        ColumnRole? rightRole = right.Role;

        // When either side lacks a role, allow the pair through.
        if (leftRole is null || rightRole is null)
        {
            return true;
        }

        ColumnRole leftValue = leftRole.Value;
        ColumnRole rightValue = rightRole.Value;

        // Both Measure → never a join key.
        if (leftValue is ColumnRole.Measure && rightValue is ColumnRole.Measure)
        {
            return false;
        }

        // Both Structural → vectors/tensors never join.
        if (leftValue is ColumnRole.Structural && rightValue is ColumnRole.Structural)
        {
            return false;
        }

        // At least one side is Identifier or ForeignKey → always allowed.
        if (leftValue is ColumnRole.Identifier or ColumnRole.ForeignKey ||
            rightValue is ColumnRole.Identifier or ColumnRole.ForeignKey)
        {
            return true;
        }

        // Both Categorical → allowed only when NDV is close enough.
        if (leftValue is ColumnRole.Categorical && rightValue is ColumnRole.Categorical)
        {
            long leftNdv = left.EstimatedDistinctCount;
            long rightNdv = right.EstimatedDistinctCount;

            if (leftNdv <= 0 || rightNdv <= 0)
            {
                return false;
            }

            double ratio = (double)Math.Min(leftNdv, rightNdv) / Math.Max(leftNdv, rightNdv);

            return ratio >= CategoricalNdvRatioThreshold;
        }

        // All other combinations (e.g. Temporal↔Measure, Text↔Categorical) are rejected
        // unless one side is Identifier/ForeignKey (already handled above).
        return false;
    }

    /// <summary>
    /// Computes the Levenshtein edit distance between two strings.
    /// Uses a single-row DP approach for O(min(m,n)) memory.
    /// </summary>
    private static int LevenshteinDistance(string source, string target)
    {
        if (source.Length == 0)
        {
            return target.Length;
        }

        if (target.Length == 0)
        {
            return source.Length;
        }

        // Ensure source is the shorter string for memory efficiency.
        if (source.Length > target.Length)
        {
            (source, target) = (target, source);
        }

        int targetLength = target.Length;
        int[] previousRow = new int[targetLength + 1];

        for (int j = 0; j <= targetLength; j++)
        {
            previousRow[j] = j;
        }

        for (int i = 1; i <= source.Length; i++)
        {
            int previousDiagonal = previousRow[0];
            previousRow[0] = i;

            for (int j = 1; j <= targetLength; j++)
            {
                int cost = source[i - 1] == target[j - 1] ? 0 : 1;
                int currentValue = Math.Min(
                    Math.Min(previousRow[j] + 1, previousRow[j - 1] + 1),
                    previousDiagonal + cost);

                previousDiagonal = previousRow[j];
                previousRow[j] = currentValue;
            }
        }

        return previousRow[targetLength];
    }
}
