namespace DatumIngest.Catalog;

/// <summary>
/// Marker for catalog backends that support transactional operations.
/// <strong>Not implemented in S1b.</strong> This interface exists to mark
/// the seam where the transactions roadmap (BEGIN / COMMIT / ROLLBACK /
/// cross-table coordination / WAL) will plug in; backends today do not
/// implement it.
/// </summary>
/// <remarks>
/// See the transactions roadmap memory for the broader plan
/// (T0 append-only → T1 BEGIN/COMMIT → T2 FK enforcement → T3 WAL → T4 MVCC).
/// A future <c>DatumDbCatalog</c> (single <c>.datumdb</c> file + WAL) will
/// implement this interface; <see cref="FlatFileCatalog"/> deliberately
/// does not.
/// </remarks>
public interface ITransactionalCatalog : ITableCatalog
{
    /// <summary>Begins a new transaction. Returns the handle for commit / rollback.</summary>
    ITransactionHandle Begin();
}

/// <summary>
/// Caller-owned handle for an in-flight transaction on an
/// <see cref="ITransactionalCatalog"/>. Disposing without
/// <see cref="Commit"/> aborts. <strong>Not implemented in S1b.</strong>
/// </summary>
public interface ITransactionHandle : IDisposable
{
    /// <summary>Commits every change made under this transaction atomically.</summary>
    void Commit();

    /// <summary>Discards every change made under this transaction.</summary>
    void Rollback();
}
