namespace Axon.QueryEngine.Cli;

using System.Diagnostics;

/// <summary>
/// Reports query execution progress to the console.
/// </summary>
internal sealed class ProgressReporter
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private long _rowCount;

    /// <summary>Gets the total rows processed.</summary>
    public long RowCount => _rowCount;

    /// <summary>Increments the row count and periodically reports progress.</summary>
    public void ReportRow()
    {
        _rowCount++;

        if (_rowCount % 10000 == 0)
        {
            WriteProgress();
        }
    }

    /// <summary>Writes the final progress summary.</summary>
    public void WriteSummary()
    {
        _stopwatch.Stop();
        double seconds = _stopwatch.Elapsed.TotalSeconds;
        double rowsPerSecond = seconds > 0 ? _rowCount / seconds : _rowCount;

        Console.WriteLine();
        Console.WriteLine($"Processed {_rowCount:N0} rows in {_stopwatch.Elapsed.TotalSeconds:F2}s ({rowsPerSecond:N0} rows/sec)");
    }

    private void WriteProgress()
    {
        double seconds = _stopwatch.Elapsed.TotalSeconds;
        double rowsPerSecond = seconds > 0 ? _rowCount / seconds : _rowCount;
        Console.Write($"\r  {_rowCount:N0} rows ({rowsPerSecond:N0} rows/sec)...");
    }
}
