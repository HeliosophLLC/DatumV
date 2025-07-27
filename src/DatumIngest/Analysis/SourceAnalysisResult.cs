using DatumIngest.Indexing;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Analysis;

/// <summary>
/// The combined output of <see cref="SourceAnalyzer"/>, containing the schema,
/// chunk-level index, and column-level statistics manifest for one or more tables
/// within a single source file.
/// </summary>
/// <param name="Schema">Per-table schemas.</param>
/// <param name="IndexSet">Per-table indexes with a shared source fingerprint.</param>
/// <param name="Manifest">Per-table column statistics and feature manifests.</param>
public sealed record SourceAnalysisResult(
    SourceSchema Schema,
    SourceIndexSet IndexSet,
    SourceManifest Manifest);
