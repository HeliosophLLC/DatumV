namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Abstract base for any catalog-bound preparable unit that the
/// in-process data API (<c>InProcessDatumDbReader</c>) can open. The two
/// concrete subtypes carry different result-set shapes:
/// </summary>
/// <list type="bullet">
///   <item><description><see cref="StatementPlan"/> — a single statement.
///     Opens as a single result set. <c>NextResult</c> always returns
///     <see langword="false"/>.</description></item>
///   <item><description><see cref="Plans.StatementBatch"/> — a sequence of
///     unrelated top-level statements (typically the result of
///     semicolon-separated SQL text). Opens as one result set per
///     statement. <c>NextResultAsync</c> advances between them, planning
///     each child against catalog state that already reflects all prior
///     children's iteration.</description></item>
/// </list>
/// <remarks>
/// <para>
/// The split exists because flattening a batch into a single
/// <c>RowBatch</c> stream loses statement boundaries — the reader has no
/// signal when one statement's result set ends and the next begins, and
/// the schema can shift across that boundary. Keeping
/// <see cref="StatementPlan"/> and <see cref="Plans.StatementBatch"/>
/// as separate types lets the reader pick the right iteration shape via
/// a type test, with no overloaded "is this batch a real batch or just
/// one statement" runtime gymnastics.
/// </para>
/// </remarks>
public abstract class PreparedSql
{
    /// <summary>The catalog this prepared unit will execute against.</summary>
    public abstract TableCatalog Catalog { get; }
}
