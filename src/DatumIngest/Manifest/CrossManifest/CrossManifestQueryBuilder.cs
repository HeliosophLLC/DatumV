namespace DatumIngest.Manifest.CrossManifest;

using System.Text;
using DatumIngest.Manifest.Insights;

/// <summary>
/// Generates JOIN SQL from cross-manifest join candidates. Produces queries that
/// join tables using discovered key columns, with optional quality annotations
/// as SQL comments.
/// </summary>
internal static class CrossManifestQueryBuilder
{
    /// <summary>
    /// Builds a JOIN SQL query from the given candidates, optionally annotated with
    /// quality warnings and per-column insights. Tables are joined in a chain based on the best candidates.
    /// Returns <see langword="null"/> when no candidates meet the confidence threshold.
    /// </summary>
    /// <param name="candidates">Scored join candidates.</param>
    /// <param name="options">Options controlling SQL generation.</param>
    /// <param name="perTableInsights">
    /// Optional per-table column insights from single-manifest analysis.
    /// When present, the generated SQL includes column-level comments noting
    /// nullity, skew, type coercions, and other data quality findings.
    /// </param>
    /// <param name="alwaysIncludeCandidates">
    /// Optional set of candidates that bypass the <see cref="CrossManifestQueryOptions.MinConfidence"/>
    /// threshold. Used for inherited hub edges whose structural validity was proven by
    /// equivalent table detection even though their confidence score is low.
    /// </param>
    /// <returns>The generated SQL string, or <see langword="null"/> if no candidates qualify.</returns>
    internal static string? BuildQuery(
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestQueryOptions options,
        IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? perTableInsights = null,
        IReadOnlySet<JoinCandidate>? alwaysIncludeCandidates = null)
    {
        // Filter to candidates above the confidence threshold (or structurally validated).
        List<JoinCandidate> qualifying = new();

        foreach (JoinCandidate candidate in candidates)
        {
            if (candidate.Confidence >= options.MinConfidence ||
                alwaysIncludeCandidates?.Contains(candidate) == true)
            {
                qualifying.Add(candidate);
            }
        }

        if (qualifying.Count == 0)
        {
            return null;
        }

        // Sort by confidence descending — prefer the strongest joins.
        qualifying.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        // Build a join tree by greedy table accumulation: start with the first
        // candidate's left table, then add joins as we encounter new tables.
        HashSet<string> joinedTables = new();
        List<JoinCandidate> orderedJoins = new();

        // Seed with the best candidate.
        joinedTables.Add(qualifying[0].LeftTable);
        joinedTables.Add(qualifying[0].RightTable);
        orderedJoins.Add(qualifying[0]);

        // Greedily add remaining candidates that connect to already-joined tables.
        bool changed = true;

        while (changed)
        {
            changed = false;

            for (int i = 0; i < qualifying.Count; i++)
            {
                JoinCandidate candidate = qualifying[i];

                if (orderedJoins.Contains(candidate))
                {
                    continue;
                }

                bool leftJoined = joinedTables.Contains(candidate.LeftTable);
                bool rightJoined = joinedTables.Contains(candidate.RightTable);

                // Include if at least one side is already joined and it brings a new table.
                if ((leftJoined || rightJoined) && !(leftJoined && rightJoined))
                {
                    joinedTables.Add(candidate.LeftTable);
                    joinedTables.Add(candidate.RightTable);
                    orderedJoins.Add(candidate);
                    changed = true;
                }
            }
        }

        // Also include any disconnected candidates (tables not yet reachable).
        foreach (JoinCandidate candidate in qualifying)
        {
            if (!orderedJoins.Contains(candidate))
            {
                joinedTables.Add(candidate.LeftTable);
                joinedTables.Add(candidate.RightTable);
                orderedJoins.Add(candidate);
            }
        }

        return FormatQuery(orderedJoins, options, perTableInsights);
    }

    /// <summary>
    /// Generates query annotations mapping each join to its confidence and quality warnings.
    /// </summary>
    /// <param name="candidates">Scored join candidates.</param>
    /// <param name="options">Options controlling annotation generation.</param>
    /// <param name="alwaysIncludeCandidates">
    /// Optional set of candidates that bypass the <see cref="CrossManifestQueryOptions.MinConfidence"/>
    /// threshold. Used for inherited hub edges whose structural validity was proven by
    /// equivalent table detection even though their confidence score is low.
    /// </param>
    /// <returns>Annotations for each qualifying join candidate.</returns>
    internal static IReadOnlyList<QueryAnnotation> GenerateAnnotations(
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestQueryOptions options,
        IReadOnlySet<JoinCandidate>? alwaysIncludeCandidates = null)
    {
        List<QueryAnnotation> annotations = new();

        foreach (JoinCandidate candidate in candidates)
        {
            if (candidate.Confidence < options.MinConfidence &&
                alwaysIncludeCandidates?.Contains(candidate) != true)
            {
                continue;
            }

            string joinColumns = FormatJoinColumns(candidate);
            string note = $"JOIN {candidate.LeftTable} ↔ {candidate.RightTable} on ({joinColumns}), type={candidate.EstimatedJoinType}";

            if (candidate.QualityWarnings is { Count: > 0 })
            {
                note += $", warnings=[{string.Join("; ", candidate.QualityWarnings)}]";
            }

            annotations.Add(new QueryAnnotation(
                joinColumns,
                InsightKind.StarSchema, // Cross-manifest annotation — use a representative kind.
                note,
                candidate.Confidence));
        }

        return annotations;
    }

    /// <summary>
    /// Formats the full SQL query string.
    /// </summary>
    private static string FormatQuery(
        List<JoinCandidate> orderedJoins,
        CrossManifestQueryOptions options,
        IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>>? perTableInsights)
    {
        StringBuilder builder = new();

        if (options.IncludeAnnotations)
        {
            builder.AppendLine("-- Auto-generated cross-manifest JOIN query");
            builder.AppendLine("-- Candidates are ordered by confidence (strongest first)");
            builder.AppendLine();
        }

        // SELECT *
        builder.AppendLine("SELECT *");

        // FROM first table
        builder.Append("FROM ");
        builder.AppendLine(QuoteIdentifier(orderedJoins[0].LeftTable));

        // JOIN clauses
        HashSet<string> emitted = new() { orderedJoins[0].LeftTable };

        foreach (JoinCandidate candidate in orderedJoins)
        {
            // Determine which side is the new table to join.
            string existingTable;
            string newTable;

            if (!emitted.Contains(candidate.RightTable))
            {
                existingTable = candidate.LeftTable;
                newTable = candidate.RightTable;
            }
            else if (!emitted.Contains(candidate.LeftTable))
            {
                existingTable = candidate.RightTable;
                newTable = candidate.LeftTable;
            }
            else
            {
                // Both tables already emitted — still emit the join condition as a comment.
                if (options.IncludeAnnotations)
                {
                    builder.Append("-- Additional relationship: ");
                    builder.AppendLine(FormatJoinCondition(candidate));
                }

                continue;
            }

            emitted.Add(newTable);

            // Determine join type.
            string joinType = "INNER JOIN";

            if (options.UseLeftJoinForNullableKeys &&
                candidate.Evidence.NullKeyRatio > options.LeftJoinNullKeyThreshold)
            {
                joinType = "LEFT JOIN";
            }

            if (options.IncludeAnnotations)
            {
                builder.AppendFormat(
                    "  -- confidence={0:F2}, type={1}",
                    candidate.Confidence,
                    candidate.EstimatedJoinType);

                if (candidate.EstimatedFanout.HasValue)
                {
                    builder.AppendFormat(", fanout={0:F1}x", candidate.EstimatedFanout.Value);
                }

                if (candidate.QualityWarnings is { Count: > 0 })
                {
                    builder.AppendFormat(", warnings: {0}", string.Join("; ", candidate.QualityWarnings));
                }

                builder.AppendLine();
            }

            builder.Append("  ");
            builder.Append(joinType);
            builder.Append(' ');
            builder.Append(QuoteIdentifier(newTable));
            builder.Append(" ON ");
            builder.AppendLine(FormatOnClause(candidate, existingTable, newTable));
        }

        // Emit per-table column insights as comments.
        if (options.IncludeAnnotations && perTableInsights is not null)
        {
            AppendPerTableInsightComments(builder, emitted, perTableInsights);
        }

        builder.AppendLine("LIMIT 100;");

        return builder.ToString();
    }

    /// <summary>
    /// Formats the ON clause for a join candidate.
    /// </summary>
    private static string FormatOnClause(
        JoinCandidate candidate,
        string existingTable,
        string newTable)
    {
        // Map columns to the correct table sides.
        bool isReversed = candidate.LeftTable != existingTable;

        StringBuilder builder = new();

        for (int i = 0; i < candidate.LeftColumns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            string leftColumn = isReversed ? candidate.RightColumns[i] : candidate.LeftColumns[i];
            string rightColumn = isReversed ? candidate.LeftColumns[i] : candidate.RightColumns[i];
            string leftTable = isReversed ? candidate.RightTable : candidate.LeftTable;
            string rightTable = isReversed ? candidate.LeftTable : candidate.RightTable;

            builder.Append(QuoteIdentifier(leftTable));
            builder.Append('.');
            builder.Append(QuoteIdentifier(leftColumn));
            builder.Append(" = ");
            builder.Append(QuoteIdentifier(rightTable));
            builder.Append('.');
            builder.Append(QuoteIdentifier(rightColumn));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats the join condition for annotation purposes.
    /// </summary>
    private static string FormatJoinCondition(JoinCandidate candidate)
    {
        StringBuilder builder = new();

        for (int i = 0; i < candidate.LeftColumns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(" AND ");
            }

            builder.Append(QuoteIdentifier(candidate.LeftTable));
            builder.Append('.');
            builder.Append(QuoteIdentifier(candidate.LeftColumns[i]));
            builder.Append(" = ");
            builder.Append(QuoteIdentifier(candidate.RightTable));
            builder.Append('.');
            builder.Append(QuoteIdentifier(candidate.RightColumns[i]));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Formats a join column pair as a readable string.
    /// </summary>
    private static string FormatJoinColumns(JoinCandidate candidate)
    {
        StringBuilder builder = new();

        for (int i = 0; i < candidate.LeftColumns.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(", ");
            }

            builder.Append(candidate.LeftColumns[i]);
            builder.Append('=');
            builder.Append(candidate.RightColumns[i]);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Appends per-table column insight comments before the LIMIT clause.
    /// Groups insights by table and emits a short summary for each affected column.
    /// </summary>
    private static void AppendPerTableInsightComments(
        StringBuilder builder,
        HashSet<string> tables,
        IReadOnlyDictionary<string, IReadOnlyList<DatasetInsight>> perTableInsights)
    {
        bool headerEmitted = false;

        foreach (string table in tables)
        {
            if (!perTableInsights.TryGetValue(table, out IReadOnlyList<DatasetInsight>? insights))
            {
                continue;
            }

            if (!headerEmitted)
            {
                builder.AppendLine("-- Column insights:");
                headerEmitted = true;
            }

            foreach (DatasetInsight insight in insights)
            {
                string columns = string.Join(", ", insight.AffectedFeatures);
                builder.AppendFormat(
                    "--   {0}: [{1}] {2}",
                    table,
                    columns,
                    FormatInsightSummary(insight));
                builder.AppendLine();
            }
        }
    }

    /// <summary>
    /// Produces a short one-line summary of an insight for SQL comment output.
    /// </summary>
    private static string FormatInsightSummary(DatasetInsight insight)
    {
        return $"{insight.Kind} ({insight.Severity}): {insight.Observation}";
    }

    /// <summary>
    /// Quotes a SQL identifier with double quotes (PostgreSQL style).
    /// </summary>
    private static string QuoteIdentifier(string identifier)
    {
        // Escape embedded double quotes by doubling them.
        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
