namespace DatumIngest.Manifest.Insights;

/// <summary>
/// Synthesizes DatumIngest SQL queries from finalized <see cref="DatasetInsight"/> lists.
/// Produces a recommended query (AutoSafe + Suggest actions only) and a full query
/// (all actions including ManualOnly proposals). Respects bundle atomicity and
/// conflict group resolution.
/// </summary>
internal static class QuerySynthesizer
{
    /// <summary>
    /// Synthesizes the recommended query using only actions from insights whose
    /// <see cref="DatasetInsight.RecommendedApplyMode"/> is <see cref="ApplyMode.AutoSafe"/>
    /// or <see cref="ApplyMode.Suggest"/>. Returns <see langword="null"/> when no
    /// applicable actions exist.
    /// </summary>
    internal static string? SynthesizeRecommended(
        IReadOnlyList<DatasetInsight> insights,
        IReadOnlyList<string> originalColumns,
        QuerySynthesisOptions options)
    {
        List<ResolvedAction> actions = CollectActions(insights, includeProposed: false);

        if (actions.Count == 0)
        {
            return null;
        }

        return BuildQuery(actions, originalColumns, options);
    }

    /// <summary>
    /// Synthesizes the full suggested query using all actions, including proposed actions
    /// from <see cref="ApplyMode.ManualOnly"/> insights. Returns <see langword="null"/>
    /// when no actions exist at all.
    /// </summary>
    internal static string? SynthesizeFull(
        IReadOnlyList<DatasetInsight> insights,
        IReadOnlyList<string> originalColumns,
        QuerySynthesisOptions options)
    {
        List<ResolvedAction> actions = CollectActions(insights, includeProposed: true);

        if (actions.Count == 0)
        {
            return null;
        }

        return BuildQuery(actions, originalColumns, options);
    }

    /// <summary>
    /// Generates annotations mapping each transformed column back to its originating insight.
    /// </summary>
    internal static IReadOnlyList<QueryAnnotation> GenerateAnnotations(
        IReadOnlyList<DatasetInsight> insights)
    {
        List<QueryAnnotation> annotations = new();

        foreach (DatasetInsight insight in insights)
        {
            AddAnnotationsFromActions(insight, insight.Actions, annotations);

            if (insight.ProposedActions is not null)
            {
                AddAnnotationsFromActions(insight, insight.ProposedActions, annotations);
            }
        }

        return annotations;
    }

    private static void AddAnnotationsFromActions(
        DatasetInsight insight,
        IReadOnlyList<InsightAction> actions,
        List<QueryAnnotation> annotations)
    {
        foreach (InsightAction action in actions)
        {
            string column = action.Kind switch
            {
                ActionKind.Append => action.Alias ?? action.Column ?? "unknown",
                ActionKind.Drop => action.Column ?? "unknown",
                ActionKind.Replace => action.Column ?? "unknown",
                ActionKind.Filter => "WHERE",
                _ => "unknown",
            };

            string note = action.Kind switch
            {
                ActionKind.Drop => $"Dropped: {insight.Observation}",
                ActionKind.Replace => $"Replaced: {action.Expression}",
                ActionKind.Append => $"Added: {action.Expression} AS {action.Alias}",
                ActionKind.Filter => $"Filter: {action.Expression}",
                _ => insight.Recommendation,
            };

            annotations.Add(new QueryAnnotation(column, insight.Kind, note, insight.Confidence));
        }
    }

    /// <summary>
    /// Collects actions from insights, enforcing bundle atomicity. A bundle is included
    /// only if all its member actions are available in the current collection scope.
    /// </summary>
    private static List<ResolvedAction> CollectActions(
        IReadOnlyList<DatasetInsight> insights,
        bool includeProposed)
    {
        // First pass: gather all candidate actions with their source insight.
        List<ResolvedAction> candidates = new();

        foreach (DatasetInsight insight in insights)
        {
            foreach (InsightAction action in insight.Actions)
            {
                candidates.Add(new ResolvedAction(action, insight));
            }

            if (includeProposed && insight.ProposedActions is not null)
            {
                foreach (InsightAction action in insight.ProposedActions)
                {
                    candidates.Add(new ResolvedAction(action, insight));
                }
            }
        }

        // Second pass: enforce bundle atomicity. Count expected members per bundle
        // across all insights (not just collected ones) and verify all are present.
        Dictionary<string, int> expectedBundleSizes = new();
        Dictionary<string, int> collectedBundleSizes = new();

        // Count expected sizes from all insights (all actions + all proposed actions).
        foreach (DatasetInsight insight in insights)
        {
            CountBundleMembers(insight.Actions, expectedBundleSizes);

            if (insight.ProposedActions is not null)
            {
                CountBundleMembers(insight.ProposedActions, expectedBundleSizes);
            }
        }

        // Count collected sizes.
        foreach (ResolvedAction candidate in candidates)
        {
            if (candidate.Action.BundleIdentifier is not null)
            {
                collectedBundleSizes.TryGetValue(candidate.Action.BundleIdentifier, out int count);
                collectedBundleSizes[candidate.Action.BundleIdentifier] = count + 1;
            }
        }

        // Remove candidates from incomplete bundles.
        HashSet<string> incompleteBundles = new();

        foreach (KeyValuePair<string, int> expected in expectedBundleSizes)
        {
            collectedBundleSizes.TryGetValue(expected.Key, out int collected);

            if (collected < expected.Value)
            {
                incompleteBundles.Add(expected.Key);
            }
        }

        if (incompleteBundles.Count > 0)
        {
            candidates.RemoveAll(resolved =>
                resolved.Action.BundleIdentifier is not null &&
                incompleteBundles.Contains(resolved.Action.BundleIdentifier));
        }

        return candidates;
    }

    private static void CountBundleMembers(
        IReadOnlyList<InsightAction> actions,
        Dictionary<string, int> sizes)
    {
        foreach (InsightAction action in actions)
        {
            if (action.BundleIdentifier is not null)
            {
                sizes.TryGetValue(action.BundleIdentifier, out int count);
                sizes[action.BundleIdentifier] = count + 1;
            }
        }
    }

    /// <summary>
    /// Builds a clean SQL SELECT statement from resolved actions applied to the original columns.
    /// </summary>
    private static string BuildQuery(
        List<ResolvedAction> actions,
        IReadOnlyList<string> originalColumns,
        QuerySynthesisOptions options)
    {
        // Index actions by column for O(1) lookup.
        Dictionary<string, List<ResolvedAction>> actionsByColumn = new();
        List<ResolvedAction> appendActions = new();
        List<ResolvedAction> filterActions = new();
        HashSet<string> droppedColumns = new();

        foreach (ResolvedAction resolved in actions)
        {
            switch (resolved.Action.Kind)
            {
                case ActionKind.Drop:
                    if (resolved.Action.Column is not null)
                    {
                        droppedColumns.Add(resolved.Action.Column);
                    }

                    break;

                case ActionKind.Replace:
                    if (resolved.Action.Column is not null)
                    {
                        if (!actionsByColumn.TryGetValue(resolved.Action.Column, out List<ResolvedAction>? list))
                        {
                            list = new List<ResolvedAction>();
                            actionsByColumn[resolved.Action.Column] = list;
                        }

                        list.Add(resolved);
                    }

                    break;

                case ActionKind.Append:
                    appendActions.Add(resolved);
                    break;

                case ActionKind.Filter:
                    filterActions.Add(resolved);
                    break;
            }
        }

        // Build SELECT projections.
        List<string> projections = new();

        foreach (string column in originalColumns)
        {
            if (droppedColumns.Contains(column))
            {
                continue;
            }

            if (actionsByColumn.TryGetValue(column, out List<ResolvedAction>? replacements))
            {
                // Use the highest-confidence replacement.
                ResolvedAction best = replacements[0];

                for (int i = 1; i < replacements.Count; i++)
                {
                    if (replacements[i].Insight.Confidence > best.Insight.Confidence)
                    {
                        best = replacements[i];
                    }
                }

                projections.Add($"{best.Action.Expression} AS {column}");
            }
            else
            {
                projections.Add(column);
            }
        }

        // Add appended columns.
        foreach (ResolvedAction append in appendActions)
        {
            string alias = append.Action.Alias ?? "derived";
            projections.Add($"{append.Action.Expression} AS {alias}");
        }

        // Build the query.
        System.Text.StringBuilder builder = new();
        builder.Append("SELECT ");

        for (int i = 0; i < projections.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(projections[i]);
        }

        builder.Append(" FROM ");
        builder.Append(options.SourceExpression);

        // Add WHERE clause.
        if (filterActions.Count > 0)
        {
            builder.Append(" WHERE ");

            for (int i = 0; i < filterActions.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(" AND ");
                }

                builder.Append(filterActions[i].Action.Expression);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Associates an action with its source insight for confidence-based conflict resolution.
    /// </summary>
    private readonly record struct ResolvedAction(InsightAction Action, DatasetInsight Insight);
}
