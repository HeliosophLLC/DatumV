using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Server;

namespace DatumIngest.Tests.Server;

/// <summary>
/// Tests for <see cref="CommandResult"/> factory methods and properties.
/// </summary>
public sealed class CommandResultTests
{
    /// <summary>
    /// Success result carries the message and IsSuccess is true.
    /// </summary>
    [Fact]
    public void Success_SetsMessageAndKind()
    {
        CommandResult result = CommandResult.Success("Done");
        Assert.Equal(CommandResultKind.Success, result.Kind);
        Assert.True(result.IsSuccess);
        Assert.Equal("Done", result.Message);
    }

    /// <summary>
    /// Error result carries the message and IsSuccess is false.
    /// </summary>
    [Fact]
    public void Error_SetsMessageAndKind()
    {
        CommandResult result = CommandResult.Error("Something failed");
        Assert.Equal(CommandResultKind.Error, result.Kind);
        Assert.False(result.IsSuccess);
        Assert.Equal("Something failed", result.Message);
    }

    /// <summary>
    /// StreamingRows result carries rows and schema.
    /// </summary>
    [Fact]
    public void StreamingRows_SetsRowsAndSchema()
    {
        Schema schema = new(new[] { new ColumnInfo("id", DataKind.Float32, false) });
        CommandResult result = CommandResult.StreamingRows(EmptyRows(), schema, new Arena());
        Assert.Equal(CommandResultKind.StreamingRows, result.Kind);
        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Rows);
        Assert.Same(schema, result.Schema);
    }

    /// <summary>
    /// SchemaResult carries the schema.
    /// </summary>
    [Fact]
    public void SchemaResult_SetsSchema()
    {
        Schema schema = new(new[] { new ColumnInfo("name", DataKind.String, true) });
        CommandResult result = CommandResult.SchemaResult(schema);
        Assert.Equal(CommandResultKind.SchemaResult, result.Kind);
        Assert.Same(schema, result.Schema);
    }

    /// <summary>
    /// ListResult carries the items list.
    /// </summary>
    [Fact]
    public void ListResult_SetsItems()
    {
        List<string> items = new() { "alpha", "beta" };
        CommandResult result = CommandResult.ListResult(items);
        Assert.Equal(CommandResultKind.ListResult, result.Kind);
        Assert.Same(items, result.Items);
    }

    /// <summary>
    /// SessionList carries session information records.
    /// </summary>
    [Fact]
    public void SessionList_SetsSessions()
    {
        List<SessionInfo> sessions = new()
        {
            new SessionInfo(Guid.NewGuid(), SessionRole.Admin, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 5, 0)
        };
        CommandResult result = CommandResult.SessionList(sessions);
        Assert.Equal(CommandResultKind.SessionList, result.Kind);
        Assert.Single(result.Sessions!);
    }

    /// <summary>
    /// ExplainResult carries both the rendered text and the structured plan tree.
    /// </summary>
    [Fact]
    public void ExplainResult_SetsMessageAndPlan()
    {
        ExplainPlanNode plan = new()
        {
            OperatorName = "Scan",
            Details = "table: t",
        };
        CommandResult result = CommandResult.ExplainResult("rendered text", plan);
        Assert.Equal(CommandResultKind.Success, result.Kind);
        Assert.True(result.IsSuccess);
        Assert.Equal("rendered text", result.Message);
        Assert.Same(plan, result.ExplainPlan);
    }

    private static async IAsyncEnumerable<RowBatch> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }
}
