using System.Text;

namespace DatumIngest.Execution;

/// <summary>
/// Represents one node in an EXPLAIN plan tree, carrying both static
/// plan information and optional runtime metrics from EXPLAIN ANALYZE.
/// </summary>
public sealed class ExplainPlanNode
{
    /// <summary>The operator type name (e.g. "ScanOperator", "FilterOperator").</summary>
    public string OperatorName { get; init; } = "";

    /// <summary>Human-readable details about the operator configuration.</summary>
    public string Details { get; init; } = "";

    /// <summary>Warnings about potential performance concerns.</summary>
    public List<string> Warnings { get; init; } = [];

    /// <summary>Child nodes in the operator tree.</summary>
    public List<ExplainPlanNode> Children { get; init; } = [];

    /// <summary>Optional label for this child edge (e.g. "probe", "build").</summary>
    public string? ChildLabel { get; set; }

    // ── Runtime metrics (populated only by EXPLAIN ANALYZE) ──

    /// <summary>Total number of rows produced by this operator.</summary>
    public long? RowsProduced { get; set; }

    /// <summary>Total number of rows consumed from child operators.</summary>
    public long? RowsConsumed { get; set; }

    /// <summary>Total elapsed time for this operator (inclusive of children).</summary>
    public TimeSpan? TotalTime { get; set; }

    /// <summary>Self time (total minus children's total).</summary>
    public TimeSpan? SelfTime { get; set; }

    /// <summary>Additional runtime annotations (e.g. "buffered: 10,000 rows").</summary>
    public List<string> RuntimeAnnotations { get; init; } = [];

    /// <summary>
    /// Renders this plan tree as a human-readable indented string.
    /// </summary>
    public string Render()
    {
        StringBuilder builder = new();
        Render(builder, prefix: "", isLast: true, isRoot: true);
        return builder.ToString();
    }

    private void Render(StringBuilder builder, string prefix, bool isLast, bool isRoot)
    {
        string connector = isRoot ? "" : (isLast ? "└─ " : "├─ ");
        string labelPrefix = ChildLabel is not null ? $"[{ChildLabel}] " : "";

        builder.Append(prefix);
        builder.Append(connector);
        builder.Append(labelPrefix);
        builder.Append(OperatorName);

        if (!string.IsNullOrEmpty(Details))
        {
            builder.Append(" (");
            builder.Append(Details);
            builder.Append(')');
        }

        // Append runtime metrics on the same line if available.
        if (RowsProduced.HasValue || SelfTime.HasValue)
        {
            builder.Append("  |");

            if (RowsConsumed.HasValue && RowsConsumed.Value != RowsProduced)
            {
                builder.Append($"  rows in: {RowsConsumed.Value:N0} → out: {RowsProduced!.Value:N0}");

                if (RowsConsumed.Value > 0)
                {
                    double selectivity = (double)RowsProduced.Value / RowsConsumed.Value * 100.0;
                    builder.Append($" ({selectivity:F1}%)");
                }
            }
            else if (RowsProduced.HasValue)
            {
                builder.Append($"  rows: {RowsProduced.Value:N0}");
            }

            if (SelfTime.HasValue)
            {
                builder.Append($"  |  self: {FormatTime(SelfTime.Value)}");
            }

            if (TotalTime.HasValue)
            {
                builder.Append($"  |  total: {FormatTime(TotalTime.Value)}");
            }
        }

        builder.AppendLine();

        // Render runtime annotations.
        string childPrefix = isRoot ? "" : (prefix + (isLast ? "    " : "│   "));

        foreach (string annotation in RuntimeAnnotations)
        {
            builder.Append(childPrefix);
            builder.Append("    ");
            builder.AppendLine(annotation);
        }

        // Render warnings.
        foreach (string warning in Warnings)
        {
            builder.Append(childPrefix);
            builder.Append("    ⚠ ");
            builder.AppendLine(warning);
        }

        // Render children.
        for (int i = 0; i < Children.Count; i++)
        {
            Children[i].Render(builder, childPrefix, isLast: i == Children.Count - 1, isRoot: false);
        }
    }

    private static string FormatTime(TimeSpan time)
    {
        if (time.TotalMilliseconds < 1.0)
        {
            return $"{time.TotalMicroseconds:F1} us";
        }

        if (time.TotalSeconds < 1.0)
        {
            return $"{time.TotalMilliseconds:F1} ms";
        }

        return $"{time.TotalSeconds:F2} s";
    }
}
