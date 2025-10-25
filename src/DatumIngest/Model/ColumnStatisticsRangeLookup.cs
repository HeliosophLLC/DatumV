using System.Diagnostics.CodeAnalysis;

namespace DatumIngest.Model;

/// <summary>
/// Provides access to column-level statistics for a single partition.
/// </summary>
public sealed class ColumnStatisticsRangeLookup : IDisposable
{
    private Dictionary<string, ColumnStatisticsRange>? _statisticsByColumn;

    /// <summary>
    /// Initializes a new instance of the <see cref="ColumnStatisticsRangeLookup"/> class with the given column statistics.
    /// </summary>
    public ColumnStatisticsRangeLookup(Dictionary<string, ColumnStatisticsRange> statisticsByColumn)
    {
        _statisticsByColumn = statisticsByColumn;
    }

    /// <summary>
    /// Gets a value indicating whether this instance has been disposed.
    /// </summary>
    [MemberNotNullWhen(false, nameof(_statisticsByColumn))]
    public bool Disposed { get; private set; }

    /// <summary>
    /// The column names for which statistics are tracked.
    /// </summary>
    public IEnumerable<string> Keys
    {
        get
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            return _statisticsByColumn.Keys;
        }
    }

    /// <summary>
    /// Gets the column statistics for the specified column, or throws an <see cref="ArgumentException"/> if no statistics are available.
    /// </summary>
    /// <param name="columnName">The name of the column for which to retrieve statistics.</param>
    /// <returns>The <see cref="ColumnStatisticsRange"/> for the specified column.</returns>
    public ColumnStatisticsRange GetStatistics(string columnName)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        return _statisticsByColumn.TryGetValue(columnName, out ColumnStatisticsRange? stats) == true
            ? stats
            : throw new ArgumentException($"No statistics available for column '{columnName}'.", nameof(columnName));
    }

    /// <summary>
    /// Tries to get the column statistics for the specified column.
    /// </summary>
    /// <param name="columnName">The name of the column for which to retrieve statistics.</param>
    /// <param name="statistics">When this method returns, contains the <see cref="ColumnStatisticsRange"/> for the specified column if found; otherwise, <c>null</c>.</param>
    /// <returns><c>true</c> if the statistics for the specified column were found; otherwise, <c>false</c>.</returns>
    public bool TryGetStatistics(string columnName, [NotNullWhen(true)] out ColumnStatisticsRange? statistics)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);

        if (_statisticsByColumn.TryGetValue(columnName, out ColumnStatisticsRange? stats))
        {
            statistics = stats;
            return true;
        }
        else
        {
            statistics = null;
            return false;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed)
        {
            return;
        }

        _statisticsByColumn = null;

        Disposed = true;
    }
}