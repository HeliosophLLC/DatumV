using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Data;

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
///   <item><description><see cref="ExecuteReaderAsync"/> — opens an
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
/// <see cref="BatchContext"/> the reader owns and disposes when the
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
    /// Optional original SQL slice for DDL persistence — procedural
    /// <c>CREATE FUNCTION</c> / <c>CREATE PROCEDURE</c> bodies don't have a
    /// faithful AST formatter, so without the slice they fall back to a
    /// synthesised header. Ignored for non-DDL statements.
    /// </summary>
    public string? SourceText { get; set; }

    /// <summary>Named-parameter bindings.</summary>
    public InProcessDatumDbParameterCollection Parameters { get; }

    /// <summary>Opens a reader that yields the plan's row stream.</summary>
    public async Task<InProcessDatumDbReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        StatementPlan plan = await PrepareAsync(cancellationToken).ConfigureAwait(false);
        BatchContext batchContext = new(_connection.Catalog);
        batchContext.Accountant.StartProfiling();
        return await InProcessDatumDbReader
            .OpenAsync(plan, batchContext, ownsBatchContext: true, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the plan to completion, discarding any yielded rows. Returns the
    /// number of rows the plan reported as affected, or <c>-1</c> when the
    /// plan does not surface a count (DDL, SELECT, and current-version DML).
    /// </summary>
    public async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken = default)
    {
        await using InProcessDatumDbReader reader = await ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            // Drain — the plan's side effect applies during iteration; the rows
            // (if any) are intentionally discarded.
        }
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
    /// Builds and returns the <see cref="StatementPlan"/> without iterating
    /// it. Use for <c>EXPLAIN</c> — read the plan's
    /// <see cref="StatementPlan.ExplainTree"/> structure without applying
    /// side effects.
    /// </summary>
    public async Task<StatementPlan> PrepareAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Statement statement = Statement ?? (CommandText is null
            ? throw new InvalidOperationException(
                "InProcessDatumDbCommand: either CommandText or Statement must be set before executing.")
            : SqlParser.ParseStatement(CommandText));

        if (Parameters.Count > 0)
        {
            statement = ParameterBinder.Bind(statement, Parameters.AsValueMap());
        }

        string? sourceText = SourceText ?? (Statement is null ? CommandText : null);
        return await _connection.Catalog.PlanAsync(statement, sourceText).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose() { }
}
