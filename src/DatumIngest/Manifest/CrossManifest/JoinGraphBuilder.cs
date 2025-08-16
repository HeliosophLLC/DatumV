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

        return edges;
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
}
