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
