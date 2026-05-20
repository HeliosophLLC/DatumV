namespace Heliosoph.DatumV.Manifest.Insights;

/// <summary>
/// The type of transformation an <see cref="InsightAction"/> represents.
/// </summary>
public enum ActionKind
{
    /// <summary>Remove the column from the projection.</summary>
    Drop,

    /// <summary>Replace the column's expression in the projection (e.g., log-transform).</summary>
    Replace,

    /// <summary>Append a new derived column to the projection.</summary>
    Append,

    /// <summary>Add a WHERE clause predicate to filter rows.</summary>
    Filter
}
