using System.Runtime.CompilerServices;
using DatumIngest.Execution;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog.Plans;

/// <summary>
/// A sequence of unrelated top-level SQL statements held for later
/// per-child planning + execution. The structural counterpart to
/// <see cref="StatementPlan"/> for multi-statement scripts.
/// </summary>
/// <remarks>
/// <para>
/// <b>Distinct from <see cref="BlockPlan"/> and <see cref="StatementPlan"/>:</b>
/// </para>
/// <list type="bullet">
///   <item><description><see cref="BlockPlan"/> models <c>BEGIN…END</c>
///     — a procedural scope with a shared <c>VariableScope</c> frame and
///     <c>BREAK</c>/<c>CONTINUE</c> semantics.</description></item>
///   <item><description><see cref="StatementBatch"/> models a script —
///     flat sequence of independent statements, no shared procedural
///     state, no control flow.</description></item>
/// </list>
/// <para>
/// <b>Per-child planning is deferred.</b> Multi-statement scripts
/// routinely declare state in one statement and reference it in the next
/// (<c>CREATE TABLE t; INSERT INTO t …; SELECT FROM t</c>); the catalog
/// state needed to plan a later child only exists after earlier children
/// apply. Each child is planned by <see cref="StreamChildPlansAsync"/>
/// on demand, with the catalog in its current state.
/// </para>
/// <para>
/// <b>Not a <see cref="StatementPlan"/>:</b> the in-process reader
/// surfaces each child as its own result set (one per
/// <see cref="StreamChildPlansAsync"/> yield). Flattening into a single
/// <c>RowBatch</c> stream would lose the statement-boundary signal the
/// reader needs for <c>NextResult</c> and per-statement schema
/// refreshes. Consumers that only want the side effects (no rows)
/// iterate <see cref="StreamChildPlansAsync"/> and drain each child.
/// </para>
/// </remarks>
public sealed class StatementBatch : PreparedSql
{
    private readonly IReadOnlyList<(Statement Statement, string? SourceText)> _entries;
    private readonly TableCatalog _catalog;

    /// <summary>
    /// Constructs a batch over <paramref name="entries"/>. Each entry is
    /// a top-level <see cref="Statement"/> paired with an optional
    /// original SQL slice (used by DDL persistence — see
    /// <see cref="TableCatalog.PlanAsync(Statement, string?)"/>).
    /// </summary>
    public StatementBatch(
        TableCatalog catalog,
        IReadOnlyList<(Statement Statement, string? SourceText)> entries)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "StatementBatch requires at least one statement.", nameof(entries));
        }
        _catalog = catalog;
        _entries = entries;

        ExplainPlanNode tree = new()
        {
            OperatorName = "Batch",
            Details = $"{entries.Count} statement(s)",
            EstimatedRows = 0,
        };
        foreach ((Statement statement, _) in entries)
        {
            tree.Children.Add(new ExplainPlanNode
            {
                OperatorName = OperatorLabel(statement),
                Details = "planned at iterate time",
                EstimatedRows = 0,
            });
        }
        ExplainTree = tree;
    }

    /// <inheritdoc />
    public override TableCatalog Catalog => _catalog;

    /// <summary>The statement entries in source order.</summary>
    public IReadOnlyList<(Statement Statement, string? SourceText)> Entries => _entries;

    /// <summary>
    /// Static EXPLAIN tree for the batch. Per-child operator details are
    /// omitted because children are planned lazily — reading the tree
    /// does not invoke the planner.
    /// </summary>
    public ExplainPlanNode ExplainTree { get; }

    /// <summary>
    /// Lazy per-child plan stream. Each yielded
    /// <see cref="StatementPlan"/> is constructed against catalog state
    /// that already reflects all prior children's iteration. Consumers
    /// iterate each yielded plan to drain rows (or just side effects),
    /// then advance the outer enumerator to plan the next child.
    /// </summary>
    public async IAsyncEnumerable<StatementPlan> StreamChildPlansAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        BatchContext batchContext)
    {
        ArgumentNullException.ThrowIfNull(batchContext);
        _ = batchContext;
        foreach ((Statement statement, string? sourceText) in _entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return await _catalog.PlanAsync(statement, sourceText).ConfigureAwait(false);
        }
    }

    private static string OperatorLabel(Statement statement)
    {
        string typeName = statement.GetType().Name;
        return typeName.EndsWith("Statement", StringComparison.Ordinal)
            ? typeName[..^"Statement".Length]
            : typeName;
    }
}
