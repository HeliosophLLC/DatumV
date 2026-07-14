using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog.Plans;

/// <summary>
/// <see cref="StatementPlan"/> for session-setting statements — currently
/// <c>SET TIME ZONE</c> / <c>SET timezone = …</c>. Structurally identical
/// to <see cref="SchemaPlan"/>'s <c>SET search_path</c> handling: zero
/// children, zero rows, one side-effect apply that swaps the session
/// setting on the catalog. <c>SHOW</c> is not planned here — it lowers to
/// a <c>SELECT current_setting(…)</c> query at plan time.
/// </summary>
/// <remarks>
/// <para>
/// Construction is pure: building a <see cref="SessionPlan"/> never
/// mutates the catalog, and the requested zone name is not validated
/// until execution. The apply runs at most once; subsequent
/// <c>ExecuteAsync</c> calls throw — re-plan the statement instead.
/// </para>
/// </remarks>
internal sealed class SessionPlan : StatementPlan
{
    private readonly SetTimeZoneStatement _statement;
    private int _executed;

    private SessionPlan(TableCatalog catalog, SetTimeZoneStatement statement, string details)
        : base(catalog)
    {
        _statement = statement;
        ExplainTree = new ExplainPlanNode
        {
            OperatorName = "SetTimeZone",
            Details = details,
            EstimatedRows = 0,
        };
    }

    public override string Kind => "settimezone";

    /// <summary>Builds a plan for <c>SET TIME ZONE</c>.</summary>
    public static SessionPlan ForSetTimeZone(
        TableCatalog catalog, SetTimeZoneStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        return new SessionPlan(catalog, statement, statement.TimeZoneName ?? "DEFAULT");
    }

    /// <inheritdoc />
    public override ExplainPlanNode ExplainTree { get; }

    /// <inheritdoc />
#pragma warning disable CS1998 // Async method lacks 'await' — leaf yields no rows.
    protected override async IAsyncEnumerable<RowBatch> ExecuteImplAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Execution.ExecutionContext context)
    {
        if (Interlocked.Exchange(ref _executed, 1) != 0)
        {
            throw new InvalidOperationException(
                "SessionPlan 'SetTimeZone' has already been executed. " +
                "Statement plans represent a single pending execution; re-plan the statement to apply it again.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        Catalog.SetSessionTimeZone(_statement.TimeZoneName);

        yield break;
    }
#pragma warning restore CS1998
}
