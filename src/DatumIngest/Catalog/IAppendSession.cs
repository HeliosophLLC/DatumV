using DatumIngest.Model;

namespace DatumIngest.Catalog;

/// <summary>
/// Caller-owned append session for streaming row inserts into a single
/// table. Returned by <see cref="ITableProvider.BeginAppend"/>; lets the
/// call site control the commit boundary instead of committing per-batch.
/// </summary>
/// <remarks>
/// <para>
/// One session is permitted per provider at a time; concurrent
/// <c>BeginAppend</c> callers wait until the active session disposes.
/// This mirrors the .datum format's single-writer constraint
/// (<see cref="DatumIngest.DatumFile.V2.DatumFileWriterV2.OpenForAppend"/>
/// uses <c>FileShare.Read</c> to exclude other writers at the OS
/// level); the in-process semaphore makes the contention well-defined
/// across async awaits within one process too.
/// </para>
/// <para>
/// <strong>Commit boundary.</strong> Rows become visible to subsequent
/// scans only after <see cref="CommitAsync"/> returns. On the .datum
/// provider, commit is a single tail-flip atomic write — readers never
/// observe a partial-batch state.
/// </para>
/// <para>
/// <strong>Abort semantics.</strong> Disposing the session without
/// calling <see cref="CommitAsync"/> aborts. On the .datum provider,
/// the underlying writer closes without writing the new tail; partial
/// bytes past the previous committed tail are unreachable garbage that
/// the next writer's torn-tail recovery cleans up. On the in-memory
/// provider, staged cells are dropped.
/// </para>
/// </remarks>
public interface IAppendSession : IAsyncDisposable
{
    /// <summary>
    /// Appends one batch to the session. The batch's
    /// <see cref="ColumnLookup"/> must match the target table's schema —
    /// providers validate column count and (case-insensitive) column
    /// names; type coercion is the caller's responsibility (the SQL
    /// planner inserts CASTs).
    /// </summary>
    Task WriteAsync(RowBatch batch, CancellationToken cancellationToken = default);

    /// <summary>
    /// Commits all written batches atomically. After return, every
    /// row passed to <see cref="WriteAsync"/> in this session is
    /// visible to subsequent scans through the table provider.
    /// Calling <see cref="CommitAsync"/> twice or after <see cref="IAsyncDisposable.DisposeAsync"/>
    /// throws.
    /// </summary>
    Task CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Captures the table's <c>IDENTITY</c> spec and the live next-value
    /// at session-start time. <see langword="null"/> when the table has
    /// no IDENTITY column. Default returns <see langword="null"/>;
    /// providers that support IDENTITY override.
    /// </summary>
    IdentityState? IdentityState => null;

    /// <summary>
    /// Reserves the next IDENTITY value for an INSERT-driven row fill
    /// and advances the session-local counter. Throws when the table
    /// has no IDENTITY column. Persisted to the provider on
    /// <see cref="CommitAsync"/>; reverted on abort (dispose without
    /// commit), so a session that fails partway never exposes the
    /// reserved values to anyone else.
    /// </summary>
    long ReserveNextIdentityValue() =>
        throw new InvalidOperationException(
            "This session's table has no IDENTITY column.");
}
