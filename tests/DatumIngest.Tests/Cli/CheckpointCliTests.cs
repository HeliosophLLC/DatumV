using DatumIngest.Cli;

namespace DatumIngest.Tests.Cli;

/// <summary>
/// Tests for the <c>--checkpoint</c> CLI flag.
/// </summary>
public sealed class CheckpointCliTests
{
    [Fact]
    public void Parse_CheckpointFlag_SetsProperty()
    {
        CliOptions options = CliOptions.Parse(
        [
            "query",
            "SELECT * FROM data INTO 'output.csv' SHARD ON sample_count 100",
            "--source", "csv:data=data.csv",
            "--checkpoint"
        ]);

        Assert.True(options.Checkpoint);
    }

    [Fact]
    public void Parse_NoCheckpointFlag_DefaultsFalse()
    {
        CliOptions options = CliOptions.Parse(
        [
            "query",
            "SELECT * FROM data INTO 'output.csv'",
            "--source", "csv:data=data.csv"
        ]);

        Assert.False(options.Checkpoint);
    }
}
