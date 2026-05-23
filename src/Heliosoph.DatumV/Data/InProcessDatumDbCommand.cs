using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Plans;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Data;

/// <summary>
/// ADO.NET-style command. Holds the SQL text (or a pre-parsed
/// <see cref="Statement"/>), a parameter collection, and verbs that build
/// a <see cref="StatementPlan"/> via <see cref="TableCatalog.PlanAsync(string)"/>
/// and execute it.
/// </summary>
/// <remarks>
/// <para>
/// Three async execution verbs mirror ADO.NET:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ExecuteReaderAsync(CancellationToken)"/> — opens an
///     <see cref="InProcessDatumDbReader"/> that yields rows. Use for
///     <c>SELECT</c>, <c>CALL</c>, and <c>… RETURNING</c> DML.</description></item>
///   <item><description><see cref="ExecuteNonQueryAsync"/> — runs the
///     plan to completion and discards any yielded rows. Use for DDL and
///     no-RETURNING DML.</description></item>
///   <item><description><see cref="ExecuteScalarAsync"/> — reads the
///     first row, first column. Returns <see langword="null"/> when the
///     result set is empty.</description></item>
/// </list>
/// <para>
/// The surface is async-only on purpose — no sync wrappers in production
/// so callers cannot block a worker thread by accident. Test code that
/// wants sync iteration reaches for the
/// <c>InProcessDatumDbSyncExtensions</c> helper in the test assembly.
/// </para>
/// <para>
/// Each execute call constructs a fresh per-call
/// <see cref="ExecutionContext"/> the reader owns and disposes when the
/// reader is disposed.
/// </para>
/// </remarks>
public sealed class InProcessDatumDbCommand : IDisposable
{
    private readonly InProcessDatumDbConnection _connection;

    internal InProcessDatumDbCommand(InProcessDatumDbConnection connection)
    {
        _connection = connection;
        Parameters = new InProcessDatumDbParameterCollection();
    }

    /// <summary>The owning connection.</summary>
    public InProcessDatumDbConnection Connection => _connection;

    /// <summary>
    /// SQL text to execute. Mutually exclusive with <see cref="Statement"/>;
    /// if both are set, <see cref="Statement"/> wins and the text is used
    /// only as the persistence-time source slice for DDL.
    /// </summary>
    public string? CommandText { get; set; }

    /// <summary>
    /// Pre-parsed statement. When set, takes precedence over
    /// <see cref="CommandText"/> for dispatch (no re-parsing).
    /// </summary>
    public Statement? Statement { get; set; }

    /// <summary>
    /// Pre-parsed multi-statement script — each entry is a top-level
    /// <see cref="Statement"/> paired with an optional source slice
    /// (used by DDL persistence; see
    /// <see cref="TableCatalog.PlanAsync(Heliosoph.DatumV.Parsing.Ast.Statement, string?)"/>).
    /// When set, takes precedence over both <see cref="Statement"/> and
    /// <see cref="CommandText"/> and produces a
    /// <see cref="Heliosoph.DatumV.Catalog.Plans.StatementBatch"/> at
    /// <see cref="PrepareAsync"/> time — one result set per child,
    /// advanced via <see cref="InProcessDatumDbReader.NextResultAsync"/>.
    /// Parameters aren't bound through this path; pre-bind via
    /// <c>ParameterBinder</c> at the call site if needed.
    /// </summary>
    public IReadOnlyList<(Statement Statement, string? SourceText)>? Statements { get; set; }

    /// <summary>
    /// Optional original SQL slice for DDL persistence — procedural
    /// <c>CREATE FUNCTION</c> / <c>CREATE PROCEDURE</c> bodies don't have a
    /// faithful AST formatter, so without the slice they fall back to a
    /// synthesised header. Ignored for non-DDL statements.
    /// </summary>
    public string? SourceText { get; set; }

    /// <summary>Named-parameter bindings.</summary>
    public InProcessDatumDbParameterCollection Parameters { get; }

    /// <summary>
    /// Opens a reader over the prepared SQL. A pre-parsed multi-statement
    /// <see cref="Statements"/> or multi-statement <see cref="CommandText"/>
    /// opens against a
    /// <see cref="Heliosoph.DatumV.Catalog.Plans.StatementBatch"/> (one
    /// result set per child; advance with
    /// <see cref="InProcessDatumDbReader.NextResultAsync"/>);
    /// single-statement <see cref="CommandText"/> or a pre-parsed
    /// <see cref="Statement"/> opens against a single
    /// <see cref="StatementPlan"/>.
    /// </summary>
    public async Task<InProcessDatumDbReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        PreparedSql prepared = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        Execution.ExecutionContext context = _connection.Catalog.CreateExecutionContext(cancellationToken: cancellationToken);
        context.Accountant.StartProfiling();
        return await InProcessDatumDbReader
            .OpenAsync(prepared, context, ownsContext: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a reader against <paramref name="context"/> instead of
    /// constructing a per-call context. The reader does not own
    /// <paramref name="context"/> — the caller is responsible for its
    /// lifetime (and for starting accountant profiling if memory samples
    /// are wanted). Used by the streaming surface to thread one
    /// <see cref="ExecutionContext"/> (with a wired
    /// <see cref="Execution.ExecutionContext.CellSink"/> + shared
    /// cell-id allocator) through every reader opened for a single SQL
    /// batch.
    /// </summary>
    public async Task<InProcessDatumDbReader> ExecuteReaderAsync(
        Execution.ExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        PreparedSql prepared = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        return await InProcessDatumDbReader
            .OpenAsync(prepared, context, ownsContext: false, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the prepared SQL to completion, advancing through all
    /// result sets and discarding any yielded rows. Returns the
    /// number of rows the plan reported as affected, or <c>-1</c> when the
    /// plan does not surface a count (DDL, SELECT, and current-version DML).
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        await using InProcessDatumDbReader reader = await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        do
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                // Drain — the plan's side effect applies during iteration;
                // the rows (if any) are intentionally discarded.
            }
        }
        while (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false));
        return reader.RecordsAffected;
    }

    /// <summary>
    /// Reads the first row, first column. Returns <see langword="null"/>
    /// when the result set is empty. The cell may itself be SQL NULL
    /// (the returned <see cref="DataValue"/> reports <see cref="DataValue.IsNull"/>).
    /// </summary>
    public async Task<DataValue?> ExecuteScalarAsync(CancellationToken cancellationToken = default)
    {
        await using InProcessDatumDbReader reader = await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (reader.FieldCount == 0) return null;
        return reader.GetValue(0);
    }

    /// <summary>
    /// Builds and returns the <see cref="PreparedSql"/> without iterating
    /// it — either a <see cref="StatementPlan"/> (single statement) or
    /// a <see cref="Heliosoph.DatumV.Catalog.Plans.StatementBatch"/> (multi-statement
    /// <see cref="CommandText"/>). Use for <c>EXPLAIN</c> — reading the
    /// plan's structure does not apply side effects.
    /// </summary>
    public async Task<PreparedSql> PrepareAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        // Pre-parsed multi-statement batch: highest precedence — used by
        // the streaming surface and tests that already hold a parsed
        // batch. Skips parser and parameter binding (callers bind ahead
        // if needed). Empty list throws via the StatementBatch ctor.
        if (Statements is not null)
        {
            return new StatementBatch(_connection.Catalog, Statements);
        }

        // Pre-parsed Statement overrides CommandText for dispatch — used by
        // callers that already have an AST (parameter binders, batch
        // re-execution). Multi-statement support only flows through the
        // CommandText path.
        if (Statement is not null)
        {
            Statement statement = Statement;
            if (Parameters.Count > 0)
            {
                statement = ParameterBinder.Bind(statement, Parameters.AsValueMap());
            }
            string? sourceTextOverride = SourceText ?? CommandText;
            return await _connection.Catalog.PlanAsync(statement, sourceTextOverride).ConfigureAwait(false);
        }

        if (CommandText is null)
        {
            throw new InvalidOperationException(
                "InProcessDatumDbCommand: either CommandText or Statement must be set before executing.");
        }

        // String path: PrepareAsync auto-detects single vs multi-statement.
        // Parameters are only bindable on single statements (the multi-stmt
        // PrepareAsync returns a StatementBatch whose children we'd need to
        // walk to bind — defer that to a future enhancement).
        if (Parameters.Count == 0)
        {
            return await _connection.Catalog.PrepareAsync(CommandText).ConfigureAwait(false);
        }

        // With parameters: parse, bind, plan as single statement (multi-stmt
        // + parameters is unsupported in v1; ParseStatement throws on multi).
        Statement parsed = SqlParser.ParseStatement(CommandText);
        Statement bound = ParameterBinder.Bind(parsed, Parameters.AsValueMap());
        return await _connection.Catalog.PlanAsync(bound, CommandText).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() { }
}
