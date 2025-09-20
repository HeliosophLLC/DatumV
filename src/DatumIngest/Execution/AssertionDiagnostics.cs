namespace DatumIngest.Execution;

/// <summary>
/// Accumulates skip and warn counters together with a capped sample of failure messages
/// produced by <c>ASSERT … ON FAIL SKIP</c> and <c>ASSERT … ON FAIL WARN</c> clauses
/// during query execution.
/// </summary>
/// <remarks>
/// One instance is created per query and stored on <see cref="ExecutionContext.AssertionDiagnostics"/>.
/// Counter increments use <see cref="System.Threading.Interlocked"/> operations so the class
/// is safe for concurrent operator writes (e.g. parallel hash-join probe branches), though
/// the message sample list uses a simple lock to bound memory.
/// </remarks>
public sealed class AssertionDiagnostics
{
    private const int MaxSampleMessages = 10;

    private long _skippedRowCount;
    private long _warnedRowCount;

    private readonly object _messageLock = new();
    private readonly List<string> _sampleMessages = new(MaxSampleMessages);

    /// <summary>Number of rows discarded by <c>ASSERT … ON FAIL SKIP</c> clauses.</summary>
    public long SkippedRowCount => Interlocked.Read(ref _skippedRowCount);

    /// <summary>Number of rows that triggered a <c>ASSERT … ON FAIL WARN</c> clause.</summary>
    public long WarnedRowCount => Interlocked.Read(ref _warnedRowCount);

    /// <summary>
    /// Up to <c>10</c> sample failure messages collected from SKIP and WARN assertions.
    /// Sampling stops after 10 to bound memory.
    /// </summary>
    public IReadOnlyList<string> SampleMessages
    {
        get
        {
            lock (_messageLock)
            {
                return _sampleMessages.ToArray();
            }
        }
    }

    /// <summary>Records a skipped row, optionally collecting one sample message.</summary>
    /// <param name="message">The failure message, or <see langword="null"/> to use the default.</param>
    internal void RecordSkip(string? message)
    {
        Interlocked.Increment(ref _skippedRowCount);
        TryAddSample(message ?? "Assertion failed (SKIP)");
    }

    /// <summary>Records a warned row, optionally collecting one sample message.</summary>
    /// <param name="message">The failure message, or <see langword="null"/> to use the default.</param>
    internal void RecordWarn(string? message)
    {
        Interlocked.Increment(ref _warnedRowCount);
        TryAddSample(message ?? "Assertion failed (WARN)");
    }

    private void TryAddSample(string message)
    {
        lock (_messageLock)
        {
            if (_sampleMessages.Count < MaxSampleMessages)
            {
                _sampleMessages.Add(message);
            }
        }
    }
}
