namespace DatumIngest.Execution;

/// <summary>
/// Global concurrency budget that bounds the total number of parallel operator
/// workers across all concurrent queries. Operators that spawn parallel workers
/// (e.g. parallel hash join probe, parallel hash aggregate) must acquire slots
/// from this budget before starting worker tasks and release them when done.
///
/// Without a budget (<see langword="null"/> on <see cref="ExecutionContext"/>),
/// operators may spawn up to <see cref="ExecutionContext.DegreeOfParallelism"/>
/// workers without limit — appropriate for single-query CLI usage.
///
/// With a budget, the total in-flight workers across all queries never exceeds
/// <see cref="MaxWorkers"/>, preventing thread pool oversubscription on the server.
/// </summary>
public sealed class ParallelismBudget : IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    /// <summary>
    /// Creates a parallelism budget that allows at most <paramref name="maxWorkers"/>
    /// parallel operator workers across all queries.
    /// </summary>
    /// <param name="maxWorkers">
    /// Maximum number of concurrent parallel workers. Typically
    /// <see cref="Environment.ProcessorCount"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxWorkers"/> is less than 1.
    /// </exception>
    public ParallelismBudget(int maxWorkers)
    {
        if (maxWorkers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWorkers), maxWorkers, "Must be at least 1.");
        }

        MaxWorkers = maxWorkers;
        _semaphore = new SemaphoreSlim(maxWorkers, maxWorkers);
    }

    /// <summary>Maximum number of concurrent parallel workers this budget allows.</summary>
    public int MaxWorkers { get; }

    /// <summary>
    /// Number of worker slots currently available. Useful for adaptive degree of
    /// parallelism: an operator can limit its own worker count to the available slots
    /// rather than blocking.
    /// </summary>
    public int AvailableWorkers => _semaphore.CurrentCount;

    /// <summary>
    /// Acquires <paramref name="count"/> worker slots, blocking asynchronously until
    /// they become available. The caller must release the same number of slots via
    /// <see cref="Release"/> when the workers complete.
    /// </summary>
    /// <param name="count">Number of worker slots to acquire.</param>
    /// <param name="cancellationToken">Token to cancel the wait.</param>
    /// <returns>
    /// The number of slots actually acquired. Always equals <paramref name="count"/>
    /// when the method returns without throwing.
    /// </returns>
    public async Task<int> AcquireAsync(int count, CancellationToken cancellationToken)
    {
        int acquired = 0;
        try
        {
            for (int i = 0; i < count; i++)
            {
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                acquired++;
            }
        }
        catch
        {
            // Release any partially-acquired slots before propagating the exception.
            if (acquired > 0)
            {
                _semaphore.Release(acquired);
            }

            throw;
        }

        return count;
    }

    /// <summary>
    /// Tries to acquire up to <paramref name="maxCount"/> worker slots without blocking.
    /// Returns the number of slots actually acquired (may be zero if the budget
    /// is fully exhausted). The caller must release the returned count via
    /// <see cref="Release"/> when the workers complete.
    /// </summary>
    /// <param name="maxCount">Maximum number of worker slots to acquire.</param>
    /// <returns>The number of slots actually acquired (0 to <paramref name="maxCount"/>).</returns>
    public int TryAcquire(int maxCount)
    {
        int acquired = 0;
        for (int i = 0; i < maxCount; i++)
        {
            if (!_semaphore.Wait(0))
            {
                break;
            }

            acquired++;
        }

        return acquired;
    }

    /// <summary>
    /// Releases <paramref name="count"/> worker slots back to the budget.
    /// </summary>
    /// <param name="count">Number of worker slots to release.</param>
    public void Release(int count)
    {
        _semaphore.Release(count);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _semaphore.Dispose();
    }
}
