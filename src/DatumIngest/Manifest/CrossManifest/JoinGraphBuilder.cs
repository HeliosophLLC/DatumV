namespace DatumIngest.Manifest.CrossManifest;

/// <summary>
/// Builds a join graph from scored candidates and discovers transitive join chains
/// via breadth-first search. Edges are included only when candidate confidence
/// meets or exceeds the configured threshold.
/// </summary>
internal static class JoinGraphBuilder
{
    /// <summary>
    /// Builds graph edges from candidates whose confidence meets the threshold.
    /// </summary>
    /// <param name="candidates">All scored join candidates (single-column + composite).</param>
    /// <param name="thresholds">Thresholds controlling graph edge inclusion.</param>
    /// <param name="excludedTables">
    /// Optional set of table names to exclude. Candidates touching any excluded table are
    /// skipped but their indices are preserved, so <see cref="JoinGraphEdge.CandidateIndex"/>
    /// values remain valid against the original <paramref name="candidates"/> list.
    /// </param>
    /// <returns>Graph edges referencing candidates by index.</returns>
    internal static IReadOnlyList<JoinGraphEdge> BuildGraph(
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds,
        IReadOnlySet<string>? excludedTables = null)
    {
        List<JoinGraphEdge> edges = new();

        for (int i = 0; i < candidates.Count; i++)
        {
            JoinCandidate candidate = candidates[i];

            if (candidate.Confidence < thresholds.GraphEdgeMinConfidence)
            {
                continue;
            }

            if (excludedTables is not null &&
                (excludedTables.Contains(candidate.LeftTable) ||
                 excludedTables.Contains(candidate.RightTable)))
            {
                continue;
            }

            edges.Add(new JoinGraphEdge(
                candidate.LeftTable,
                candidate.RightTable,
                CandidateIndex: i,
                candidate.Confidence));
        }

        edges = ApplyEdgeCaps(edges, candidates, thresholds);

        return edges;
    }

    /// <summary>
    /// Applies edge caps to enforce per-table-pair limits, per-column limits, and
    /// minimum margin requirements. Edges are processed in descending confidence order
    /// so the strongest edges survive.
    /// </summary>
    private static List<JoinGraphEdge> ApplyEdgeCaps(
        List<JoinGraphEdge> edges,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        if (edges.Count <= 1)
        {
            return edges;
        }

        // Sort descending by confidence for greedy selection.
        edges.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        // Phase 1: Per-table-pair cap with margin filtering.
        edges = ApplyTablePairCaps(edges, thresholds);

        // Phase 2: Per-column cap — each column participates in at most N graph edges.
        edges = ApplyColumnCaps(edges, candidates, thresholds);

        return edges;
    }

    /// <summary>
    /// Retains at most <see cref="CrossManifestThresholds.MaxEdgesPerTablePair"/> edges
    /// per ordered table pair. Within each pair, the next-best edge must exceed the
    /// <see cref="CrossManifestThresholds.MinMarginOverNextBest"/> margin relative to the
    /// top edge to survive.
    /// </summary>
    private static List<JoinGraphEdge> ApplyTablePairCaps(
        List<JoinGraphEdge> edges,
        CrossManifestThresholds thresholds)
    {
        // Group by unordered table pair.
        Dictionary<(string, string), List<JoinGraphEdge>> byPair = new();

        foreach (JoinGraphEdge edge in edges)
        {
            // Normalize to ordered pair to treat (A,B) and (B,A) the same.
            (string, string) key = string.Compare(edge.LeftTable, edge.RightTable, StringComparison.OrdinalIgnoreCase) < 0
                ? (edge.LeftTable, edge.RightTable)
                : (edge.RightTable, edge.LeftTable);

            if (!byPair.TryGetValue(key, out List<JoinGraphEdge>? group))
            {
                group = new List<JoinGraphEdge>();
                byPair[key] = group;
            }

            group.Add(edge);
        }

        List<JoinGraphEdge> result = new();

        foreach (List<JoinGraphEdge> group in byPair.Values)
        {
            // Already sorted descending by confidence from the caller.
            double topConfidence = group[0].Confidence;
            result.Add(group[0]);

            for (int i = 1; i < group.Count && i < thresholds.MaxEdgesPerTablePair; i++)
            {
                // Drop near-duplicate edges: the next-best must differ from the top
                // by at least MinMarginOverNextBest to justify its own edge.
                if (topConfidence - group[i].Confidence < thresholds.MinMarginOverNextBest)
                {
                    continue;
                }

                result.Add(group[i]);
            }
        }

        return result;
    }

    /// <summary>
    /// Enforces a per-column-per-target-table participation limit. A single column name
    /// may participate in at most <see cref="CrossManifestThresholds.MaxEdgesPerColumn"/>
    /// edges toward any single other table. When a column has already reached its limit
    /// for a given target table, lower-confidence edges referencing it are dropped.
    /// </summary>
    private static List<JoinGraphEdge> ApplyColumnCaps(
        List<JoinGraphEdge> edges,
        IReadOnlyList<JoinCandidate> candidates,
        CrossManifestThresholds thresholds)
    {
        // Track how many edges each (table, column, otherTable) participates in.
        Dictionary<(string Table, string Column, string OtherTable), int> columnEdgeCount = new();
        List<JoinGraphEdge> result = new();

        // Edges are already sorted descending by confidence.
        foreach (JoinGraphEdge edge in edges)
        {
            JoinCandidate candidate = candidates[edge.CandidateIndex];

            bool anyColumnExceeded = false;

            // Check left-side columns against the right table.
            foreach (string column in candidate.LeftColumns)
            {
                (string, string, string) key = (edge.LeftTable, column, edge.RightTable);
                columnEdgeCount.TryGetValue(key, out int count);

                if (count >= thresholds.MaxEdgesPerColumn)
                {
                    anyColumnExceeded = true;
                    break;
                }
            }

            if (!anyColumnExceeded)
            {
                // Check right-side columns against the left table.
                foreach (string column in candidate.RightColumns)
                {
                    (string, string, string) key = (edge.RightTable, column, edge.LeftTable);
                    columnEdgeCount.TryGetValue(key, out int count);

                    if (count >= thresholds.MaxEdgesPerColumn)
                    {
                        anyColumnExceeded = true;
                        break;
                    }
                }
            }

            if (anyColumnExceeded)
            {
                continue;
            }

            result.Add(edge);

            // Update counts for all participating columns.
            foreach (string column in candidate.LeftColumns)
            {
                (string, string, string) key = (edge.LeftTable, column, edge.RightTable);
                columnEdgeCount.TryGetValue(key, out int count);
                columnEdgeCount[key] = count + 1;
            }

            foreach (string column in candidate.RightColumns)
            {
                (string, string, string) key = (edge.RightTable, column, edge.LeftTable);
                columnEdgeCount.TryGetValue(key, out int count);
                columnEdgeCount[key] = count + 1;
            }
        }

        return result;
    }

    /// <summary>
    /// Discovers transitive join chains through the graph using breadth-first search.
    /// Each chain is an acyclic path of at least 3 tables (2 hops).
    /// </summary>
    /// <param name="edges">Graph edges to traverse.</param>
    /// <param name="thresholds">Thresholds controlling maximum chain depth.</param>
    /// <returns>All discovered transitive chains, sorted by descending minimum confidence.</returns>
    internal static IReadOnlyList<JoinChain> FindTransitiveChains(
        IReadOnlyList<JoinGraphEdge> edges,
        CrossManifestThresholds thresholds)
    {
        // Build an adjacency list. Edges are undirected — a join between A and B
        // can be traversed in either direction.
        Dictionary<string, List<(string Neighbor, int EdgeIndex, double Confidence)>> adjacency = new();

        for (int i = 0; i < edges.Count; i++)
        {
            JoinGraphEdge edge = edges[i];
            AddAdjacency(adjacency, edge.LeftTable, edge.RightTable, i, edge.Confidence);
            AddAdjacency(adjacency, edge.RightTable, edge.LeftTable, i, edge.Confidence);
        }

        List<JoinChain> chains = new();
        int maxDepth = thresholds.ChainMaxDepth;

        // BFS from each table to find all acyclic paths of length ≥ 2 hops.
        foreach (string startTable in adjacency.Keys)
        {
            // Queue entries: (current table, path of tables, path of edge indices, min confidence so far).
            Queue<(string Current, List<string> Tables, List<int> Edges, double MinConfidence)> queue = new();
            queue.Enqueue((startTable, [startTable], [], double.MaxValue));

            while (queue.Count > 0)
            {
                (string current, List<string> pathTables, List<int> pathEdges, double minConfidence) = queue.Dequeue();

                if (!adjacency.TryGetValue(current, out List<(string Neighbor, int EdgeIndex, double Confidence)>? neighbors))
                {
                    continue;
                }

                foreach ((string neighbor, int edgeIndex, double confidence) in neighbors)
                {
                    // No cycles.
                    if (pathTables.Contains(neighbor))
                    {
                        continue;
                    }

                    double newMinConfidence = Math.Min(minConfidence, confidence);

                    List<string> newTables = new(pathTables) { neighbor };
                    List<int> newEdges = new(pathEdges) { edgeIndex };

                    // A chain needs at least 2 hops (3 tables).
                    if (newTables.Count >= 3)
                    {
                        // Only emit chains where the start table is lexicographically
                        // before the end table — this avoids duplicate A→B→C / C→B→A chains.
                        if (string.Compare(newTables[0], newTables[^1], StringComparison.Ordinal) < 0)
                        {
                            chains.Add(new JoinChain(newTables, newEdges, newMinConfidence));
                        }
                    }

                    // Continue extending if under max depth.
                    if (newTables.Count < maxDepth)
                    {
                        queue.Enqueue((neighbor, newTables, newEdges, newMinConfidence));
                    }
                }
            }
        }

        // Sort by descending minimum confidence.
        chains.Sort((a, b) => b.MinConfidence.CompareTo(a.MinConfidence));

        // Truncate to configured maximum.
        if (chains.Count > thresholds.MaxTransitiveChains)
        {
            chains.RemoveRange(thresholds.MaxTransitiveChains, chains.Count - thresholds.MaxTransitiveChains);
        }

        return chains;
    }

    /// <summary>
    /// Adds a directed adjacency entry.
    /// </summary>
    private static void AddAdjacency(
        Dictionary<string, List<(string Neighbor, int EdgeIndex, double Confidence)>> adjacency,
        string from,
        string to,
        int edgeIndex,
        double confidence)
    {
        if (!adjacency.TryGetValue(from, out List<(string Neighbor, int EdgeIndex, double Confidence)>? neighbors))
        {
            neighbors = new List<(string, int, double)>();
            adjacency[from] = neighbors;
        }

        neighbors.Add((to, edgeIndex, confidence));
    }

    /// <summary>
    /// Computes structural complexity metrics for a set of graph edges.
    /// Returns null when there are no edges.
    /// </summary>
    internal static GraphComplexity? ComputeComplexity(IReadOnlyList<JoinGraphEdge> edges)
    {
        if (edges.Count == 0)
        {
            return null;
        }

        HashSet<string> tables = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<(string, string), int> pairCounts = new();

        foreach (JoinGraphEdge edge in edges)
        {
            tables.Add(edge.LeftTable);
            tables.Add(edge.RightTable);

            (string, string) key = string.Compare(edge.LeftTable, edge.RightTable, StringComparison.OrdinalIgnoreCase) < 0
                ? (edge.LeftTable, edge.RightTable)
                : (edge.RightTable, edge.LeftTable);

            pairCounts.TryGetValue(key, out int count);
            pairCounts[key] = count + 1;
        }

        int tableCount = tables.Count;
        int maxEdgesPerPair = 0;

        foreach (int count in pairCounts.Values)
        {
            if (count > maxEdgesPerPair)
            {
                maxEdgesPerPair = count;
            }
        }

        // Maximum possible edges for N tables = N × (N−1) / 2.
        double maxPossible = tableCount * (tableCount - 1) / 2.0;
        double ambiguityRatio = maxPossible > 0.0 ? edges.Count / maxPossible : 0.0;

        return new GraphComplexity(edges.Count, tableCount, maxEdgesPerPair, ambiguityRatio);
    }
}
