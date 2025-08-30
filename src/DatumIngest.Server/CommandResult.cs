using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;

namespace DatumIngest.Server;

/// <summary>
/// Discriminated result type returned by <see cref="CommandDispatcher"/>.
/// Each variant carries the data appropriate for its kind.
/// </summary>
public sealed class CommandResult
{
    private CommandResult(CommandResultKind kind)
    {
        Kind = kind;
    }

    /// <summary>Gets the kind of result this instance represents.</summary>
    public CommandResultKind Kind { get; }

    /// <summary>Gets whether the result represents a successful operation.</summary>
    public bool IsSuccess => Kind != CommandResultKind.Error;

    /// <summary>Gets the message for <see cref="CommandResultKind.Success"/> and <see cref="CommandResultKind.Error"/> results.</summary>
    public string? Message { get; private init; }

    /// <summary>Gets the streaming row sequence for <see cref="CommandResultKind.StreamingRows"/> results.</summary>
    public IAsyncEnumerable<RowBatch>? Rows { get; private init; }

    /// <summary>Gets the result schema for <see cref="CommandResultKind.StreamingRows"/> and <see cref="CommandResultKind.SchemaResult"/> results.</summary>
    public Schema? Schema { get; private init; }

    /// <summary>Gets the string list for list-style results (tables, functions, providers, etc.).</summary>
    public IReadOnlyList<string>? Items { get; private init; }

    /// <summary>Gets function signatures for <see cref="CommandResultKind.FunctionList"/> results.</summary>
    public IReadOnlyList<FunctionSignature>? Functions { get; private init; }

    /// <summary>Gets session information for <see cref="CommandResultKind.SessionList"/> results.</summary>
    public IReadOnlyList<SessionInfo>? Sessions { get; private init; }

    /// <summary>Gets the structured explain plan tree for <see cref="CommandResultKind.Success"/> explain results.</summary>
    public ExplainPlanNode? ExplainPlan { get; private init; }

    /// <summary>Creates a success result with a message.</summary>
    /// <param name="message">Human-readable success message.</param>
    /// <returns>A success result.</returns>
    public static CommandResult Success(string message) => new(CommandResultKind.Success) { Message = message };

    /// <summary>Creates an error result with a message.</summary>
    /// <param name="message">Human-readable error description.</param>
    /// <returns>An error result.</returns>
    public static CommandResult Error(string message) => new(CommandResultKind.Error) { Message = message };

    /// <summary>Creates a streaming rows result.</summary>
    /// <param name="rows">The async enumerable of result rows.</param>
    /// <param name="schema">The schema describing the row columns.</param>
    /// <returns>A streaming rows result.</returns>
    public static CommandResult StreamingRows(IAsyncEnumerable<RowBatch> rows, Schema schema) =>
        new(CommandResultKind.StreamingRows) { Rows = rows, Schema = schema };

    /// <summary>Creates a schema inspection result.</summary>
    /// <param name="schema">The schema of the inspected table.</param>
    /// <returns>A schema result.</returns>
    public static CommandResult SchemaResult(Schema schema) => new(CommandResultKind.SchemaResult) { Schema = schema };

    /// <summary>Creates a list result (table names, provider names, etc.).</summary>
    /// <param name="items">The items to display.</param>
    /// <returns>A list result.</returns>
    public static CommandResult ListResult(IReadOnlyList<string> items) => new(CommandResultKind.ListResult) { Items = items };

    /// <summary>Creates a function list result with full signature metadata.</summary>
    /// <param name="functions">The function signatures to display.</param>
    /// <returns>A function list result.</returns>
    public static CommandResult FunctionList(IReadOnlyList<FunctionSignature> functions) =>
        new(CommandResultKind.FunctionList) { Functions = functions };

    /// <summary>Creates a session list result.</summary>
    /// <param name="sessions">Session information snapshots.</param>
    /// <returns>A session list result.</returns>
    public static CommandResult SessionList(IReadOnlyList<SessionInfo> sessions) =>
        new(CommandResultKind.SessionList) { Sessions = sessions };

    /// <summary>Creates a success result that also carries a structured explain plan.</summary>
    /// <param name="planText">Human-readable rendered plan text.</param>
    /// <param name="explainPlan">The structured explain plan tree.</param>
    /// <returns>A success result with the explain plan attached.</returns>
    public static CommandResult ExplainResult(string planText, ExplainPlanNode explainPlan) =>
        new(CommandResultKind.Success) { Message = planText, ExplainPlan = explainPlan };
}

/// <summary>
/// Describes the kind of data a <see cref="CommandResult"/> carries.
/// </summary>
public enum CommandResultKind
{
    /// <summary>Operation succeeded; check <see cref="CommandResult.Message"/>.</summary>
    Success,

    /// <summary>Operation failed; check <see cref="CommandResult.Message"/>.</summary>
    Error,

    /// <summary>Query produced streaming rows; check <see cref="CommandResult.Rows"/> and <see cref="CommandResult.Schema"/>.</summary>
    StreamingRows,

    /// <summary>Schema inspection result; check <see cref="CommandResult.Schema"/>.</summary>
    SchemaResult,

    /// <summary>A list of named items; check <see cref="CommandResult.Items"/>.</summary>
    ListResult,

    /// <summary>A list of function signatures; check <see cref="CommandResult.Functions"/>.</summary>
    FunctionList,

    /// <summary>A list of active sessions; check <see cref="CommandResult.Sessions"/>.</summary>
    SessionList,
}

/// <summary>
/// Snapshot of session state for administrative inspection.
/// </summary>
/// <param name="SessionId">Unique session identifier.</param>
/// <param name="Role">The session's authorization role.</param>
/// <param name="DatasetId">The dataset the session is serving, or <see langword="null"/>.</param>
/// <param name="CreatedAt">When the session was created.</param>
/// <param name="LastActivityAt">When the session last executed a command.</param>
/// <param name="QueryCount">Number of queries executed in this session.</param>
/// <param name="TotalQueryUnits">Cumulative Query Units consumed across all queries.</param>
public sealed record SessionInfo(
    Guid SessionId,
    SessionRole Role,
    string? DatasetId,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastActivityAt,
    int QueryCount,
    long TotalQueryUnits);
