namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Detects multi-column composite key candidates from a set of single-column
/// join candidates. Groups single-column matches by table pair and produces
/// composite candidates when multiple columns match between the same tables.
/// </summary>
internal static class CompositeKeyDetector
{
    /// <summary>
    /// Detects composite key candidates from single-column join candidates.
    /// </summary>
    /// <param name="singleKeyCandidates">Single-column join candidates to analyze.</param>
    /// <param name="thresholds">Thresholds controlling composite key detection.</param>
    /// <returns>Composite key candidates (multi-column). Does not include the original single-column candidates.</returns>
    internal static IReadOnlyList<JoinCandidate> DetectCompositeKeys(
        IReadOnlyList<JoinCandidate> singleKeyCandidates,
        CrossManifestThresholds thresholds)
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
            }

            // Apply composite penalty.
            compositeConfidence *= thresholds.CompositeKeyPenalty;

            if (compositeConfidence < thresholds.CandidateMinConfidence)
            {
                continue;
            }

            JoinEvidence compositeEvidence = new()
            {
                NameSimilarity = totalNameSimilarity / compositeSize,
                TypeCompatibility = totalTypeCompatibility / compositeSize,
                TopKJaccard = totalTopKJaccard / compositeSize,
                CardinalityRatio = totalCardinalityRatio / compositeSize,
                RangeOverlap = rangeOverlapCount > 0 ? totalRangeOverlap / rangeOverlapCount : null,
                NullKeyRatio = totalNullKeyRatio,
                UniqueKeyScore = totalUniqueKeyScore,
                CompositeConfidence = compositeConfidence,
            };

            composites.Add(new JoinCandidate
            {
                LeftTable = entry.Key.Left,
                RightTable = entry.Key.Right,
                LeftColumns = leftColumns,
                RightColumns = rightColumns,
                Evidence = compositeEvidence,
                Confidence = compositeConfidence,
                EstimatedJoinType = JoinClassification.ManyToMany, // Composite keys are typically non-unique.
                EstimatedFanout = null,
                QualityWarnings = allWarnings.Count > 0 ? allWarnings : null,
            });
        }

        return composites;
    }
}
