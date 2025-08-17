namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Detects multi-column composite key candidates from a set of single-column
/// join candidates. Groups single-column matches by table pair and produces
/// composite candidates when multiple columns match between the same tables.
/// When component columns have vocabularies (from Phase 4), uses per-component
/// containment to validate the composite and improve join classification.
/// </summary>
internal static class CompositeKeyDetector
{
    /// <summary>
    /// Containment threshold above which a component is considered fully contained
    /// in the target, indicating a foreign-key → primary-key relationship direction.
    /// </summary>
    private const double FullContainmentThreshold = 0.8;

    /// <summary>
    /// Containment boost applied to composite confidence when all component columns
    /// show high bidirectional containment (both directions ≥ <see cref="FullContainmentThreshold"/>).
    /// </summary>
    private const double ContainmentBoostMultiplier = 1.25;

    /// <summary>
    /// Detects composite key candidates from single-column join candidates.
    /// </summary>
    /// <param name="singleKeyCandidates">Single-column join candidates to analyze.</param>
    /// <param name="thresholds">Thresholds controlling composite key detection.</param>
    /// <param name="manifests">
    /// Named table manifests, used to look up per-column feature manifests for
    /// containment-based join classification. May be <see langword="null"/> when
    /// manifests are unavailable (classification defaults to <see cref="JoinClassification.ManyToMany"/>).
    /// </param>
    /// <returns>Composite key candidates (multi-column). Does not include the original single-column candidates.</returns>
    internal static IReadOnlyList<JoinCandidate> DetectCompositeKeys(
        IReadOnlyList<JoinCandidate> singleKeyCandidates,
        CrossManifestThresholds thresholds,
        IReadOnlyList<ManifestWithName>? manifests = null)
    {
        // Group by (leftTable, rightTable).
        Dictionary<(string Left, string Right), List<JoinCandidate>> groups = new();

        foreach (JoinCandidate candidate in singleKeyCandidates)
        {
            (string Left, string Right) key = (candidate.LeftTable, candidate.RightTable);

            if (!groups.TryGetValue(key, out List<JoinCandidate>? group))
            {
                group = new List<JoinCandidate>();
                groups[key] = group;
            }

            group.Add(candidate);
        }

        List<JoinCandidate> composites = new();

        foreach (KeyValuePair<(string Left, string Right), List<JoinCandidate>> entry in groups)
        {
            List<JoinCandidate> group = entry.Value;

            if (group.Count < 2)
            {
                continue; // Need at least 2 columns for a composite key.
            }

            // Skip if any individual column is already PK-like (unique key score ≥ 0.95).
            // A composite key makes sense when no single column is independently unique.
            bool anyIndividuallyUnique = false;

            foreach (JoinCandidate candidate in group)
            {
                if (candidate.Evidence.UniqueKeyScore >= 0.95)
                {
                    anyIndividuallyUnique = true;
                    break;
                }
            }

            if (anyIndividuallyUnique)
            {
                continue;
            }

            // Take the top N columns by confidence, up to max composite size.
            List<JoinCandidate> sorted = new(group);
            sorted.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

            int compositeSize = Math.Min(sorted.Count, thresholds.CompositeKeyMaxColumns);

            if (compositeSize < 2)
            {
                continue;
            }

            List<string> leftColumns = new(compositeSize);
            List<string> rightColumns = new(compositeSize);
            double compositeConfidence = 1.0;
            double totalNameSimilarity = 0.0;
            double totalTypeCompatibility = 0.0;
            double totalTopKJaccard = 0.0;
            double totalCardinalityRatio = 0.0;
            double totalNullKeyRatio = 0.0;
            double totalUniqueKeyScore = 0.0;
            double? totalRangeOverlap = null;
            int rangeOverlapCount = 0;
            List<string> allWarnings = new();

            // Containment tracking across components.
            double? minContainmentLeftInRight = null;
            double? minContainmentRightInLeft = null;
            double? minExactJaccard = null;
            int containmentComponentCount = 0;

            for (int i = 0; i < compositeSize; i++)
            {
                JoinCandidate component = sorted[i];
                leftColumns.Add(component.LeftColumns[0]);
                rightColumns.Add(component.RightColumns[0]);
                compositeConfidence *= component.Confidence;
                totalNameSimilarity += component.Evidence.NameSimilarity;
                totalTypeCompatibility += component.Evidence.TypeCompatibility;
                totalTopKJaccard += component.Evidence.TopKJaccard;
                totalCardinalityRatio += component.Evidence.CardinalityRatio;
                totalNullKeyRatio = Math.Max(totalNullKeyRatio, component.Evidence.NullKeyRatio);
                totalUniqueKeyScore = Math.Max(totalUniqueKeyScore, component.Evidence.UniqueKeyScore);

                if (component.Evidence.RangeOverlap.HasValue)
                {
                    totalRangeOverlap = (totalRangeOverlap ?? 0.0) + component.Evidence.RangeOverlap.Value;
                    rangeOverlapCount++;
                }

                if (component.QualityWarnings is not null)
                {
                    allWarnings.AddRange(component.QualityWarnings);
                }

                // Track per-component containment (conservative: minimum across all components).
                if (component.Evidence.ContainmentLeftInRight.HasValue &&
                    component.Evidence.ContainmentRightInLeft.HasValue)
                {
                    containmentComponentCount++;

                    minContainmentLeftInRight = minContainmentLeftInRight.HasValue
                        ? Math.Min(minContainmentLeftInRight.Value, component.Evidence.ContainmentLeftInRight.Value)
                        : component.Evidence.ContainmentLeftInRight.Value;

                    minContainmentRightInLeft = minContainmentRightInLeft.HasValue
                        ? Math.Min(minContainmentRightInLeft.Value, component.Evidence.ContainmentRightInLeft.Value)
                        : component.Evidence.ContainmentRightInLeft.Value;
                }

                if (component.Evidence.ExactJaccard.HasValue)
                {
                    minExactJaccard = minExactJaccard.HasValue
                        ? Math.Min(minExactJaccard.Value, component.Evidence.ExactJaccard.Value)
                        : component.Evidence.ExactJaccard.Value;
                }
            }

            // Apply composite penalty.
            compositeConfidence *= thresholds.CompositeKeyPenalty;

            // Containment boost: when ALL component columns have vocab-based containment
            // and both directions show high containment, the composite is strongly validated.
            bool allComponentsHaveContainment = containmentComponentCount == compositeSize;

            if (allComponentsHaveContainment &&
                minContainmentLeftInRight.HasValue && minContainmentRightInLeft.HasValue &&
                minContainmentLeftInRight.Value >= FullContainmentThreshold &&
                minContainmentRightInLeft.Value >= FullContainmentThreshold)
            {
                compositeConfidence *= ContainmentBoostMultiplier;
            }

            if (compositeConfidence < thresholds.CandidateMinConfidence)
            {
                continue;
            }

            JoinEvidence compositeEvidence = new()
            {
                NameSimilarity = totalNameSimilarity / compositeSize,
                TypeCompatibility = totalTypeCompatibility / compositeSize,
                TopKJaccard = totalTopKJaccard / compositeSize,
                ExactJaccard = minExactJaccard,
                ContainmentLeftInRight = allComponentsHaveContainment ? minContainmentLeftInRight : null,
                ContainmentRightInLeft = allComponentsHaveContainment ? minContainmentRightInLeft : null,
                CardinalityRatio = totalCardinalityRatio / compositeSize,
                RangeOverlap = rangeOverlapCount > 0 ? totalRangeOverlap / rangeOverlapCount : null,
                NullKeyRatio = totalNullKeyRatio,
                UniqueKeyScore = totalUniqueKeyScore,
                CompositeConfidence = compositeConfidence,
            };

            JoinClassification joinType = ClassifyCompositeJoin(
                minContainmentLeftInRight, minContainmentRightInLeft,
                allComponentsHaveContainment, manifests,
                entry.Key.Left, entry.Key.Right, leftColumns, rightColumns);

            composites.Add(new JoinCandidate
            {
                LeftTable = entry.Key.Left,
                RightTable = entry.Key.Right,
                LeftColumns = leftColumns,
                RightColumns = rightColumns,
                Evidence = compositeEvidence,
                Confidence = compositeConfidence,
                EstimatedJoinType = joinType,
                EstimatedFanout = null,
                QualityWarnings = allWarnings.Count > 0 ? allWarnings : null,
            });
        }

        return composites;
    }

    /// <summary>
    /// Classifies the composite join type using containment asymmetry when available,
    /// falling back to <see cref="JoinClassification.ManyToMany"/> when containment
    /// data is incomplete.
    /// </summary>
    private static JoinClassification ClassifyCompositeJoin(
        double? minContainmentLeftInRight,
        double? minContainmentRightInLeft,
        bool allComponentsHaveContainment,
        IReadOnlyList<ManifestWithName>? manifests,
        string leftTable,
        string rightTable,
        List<string> leftColumns,
        List<string> rightColumns)
    {
        if (!allComponentsHaveContainment ||
            !minContainmentLeftInRight.HasValue ||
            !minContainmentRightInLeft.HasValue)
        {
            return JoinClassification.ManyToMany;
        }

        bool leftContainedInRight = minContainmentLeftInRight.Value >= FullContainmentThreshold;
        bool rightContainedInLeft = minContainmentRightInLeft.Value >= FullContainmentThreshold;

        return (leftContainedInRight, rightContainedInLeft) switch
        {
            // Both sides fully contained → likely same domain, one-to-one.
            (true, true) => JoinClassification.OneToOne,

            // Left FK values all exist in right PK → many-to-one (left has duplicates).
            (true, false) => JoinClassification.ManyToOne,

            // Right FK values all exist in left PK → one-to-many (right has duplicates).
            (false, true) => JoinClassification.OneToMany,

            // Neither side fully contained → many-to-many.
            (false, false) => JoinClassification.ManyToMany,
        };
    }
}
