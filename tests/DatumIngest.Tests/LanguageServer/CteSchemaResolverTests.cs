namespace DatumIngest.Tests.LanguageServer;

using DatumIngest.LanguageServer;
using DatumIngest.Manifest;

/// <summary>
/// Direct tests for <see cref="CteSchemaResolver"/> — covers the
/// projection-derivation logic in isolation so failures don't get hidden by
/// the broader completion / hover pipelines.
/// </summary>
public sealed class CteSchemaResolverTests : ServiceTestBase
{
    private static LanguageServerManifest CreateManifest() => new()
    {
        Tables = [],
        Functions =
        [
            new FunctionSignature
            {
                SchemaName = "system",
                Name = "video_unnest_frames",
                Parameters = [new ParameterSignature { Name = "source", Kind = "String" }],
                ReturnType = "table(frame_index Int32, frame VideoFrame)",
                IsTableValued = true,
                OutputColumns =
                [
                    new TableColumnEntry { Name = "frame_index", Kind = "Int32", Nullable = false },
                    new TableColumnEntry { Name = "frame", Kind = "VideoFrame", Nullable = false },
                ],
            },
        ],
        Keywords = [],
    };

    [Fact]
    public void Resolve_BareCte_BuildsSchemaFromTvfSource()
    {
        const string sql =
            "WITH frames AS (SELECT frame_index, frame FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT f1.x FROM frames f1";

        CteSchemaResult result = CteSchemaResolver.Resolve(sql, CreateManifest());

        Assert.True(result.Schemas.ContainsKey("frames"));
        IReadOnlyList<TableColumnEntry> cols = result.Schemas["frames"];
        Assert.Equal(2, cols.Count);
        Assert.Equal("frame_index", cols[0].Name);
        Assert.Equal("Int32", cols[0].Kind);
        Assert.Equal("frame", cols[1].Name);
        Assert.Equal("VideoFrame", cols[1].Kind);
    }

    [Fact]
    public void Resolve_FromAliasOfCte_RecordedInAliasMap()
    {
        const string sql =
            "WITH frames AS (SELECT frame_index FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT f1.x FROM frames f1";

        CteSchemaResult result = CteSchemaResolver.Resolve(sql, CreateManifest());

        Assert.True(result.FromAliasToCteName.ContainsKey("f1"));
        Assert.Equal("frames", result.FromAliasToCteName["f1"]);
    }
}
