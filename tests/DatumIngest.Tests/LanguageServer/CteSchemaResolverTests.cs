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
    public void Resolve_LetBoundFunctionCall_PropagatesReturnTypeToProjection()
    {
        // `LET prev_image = video_frame_to_image(prev, 800)` followed by
        // `prev_image` in the SELECT list — the projection must resolve to
        // Image (the function's declared return), not the `?` placeholder
        // the resolver used to emit for any non-trivial expression.
        LanguageServerManifest enrichedManifest = new()
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
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_frame_to_image",
                    Parameters =
                    [
                        new ParameterSignature { Name = "frame", Kind = "VideoFrame" },
                        new ParameterSignature { Name = "max_dim", Kind = "Int32", IsOptional = true },
                    ],
                    ReturnType = "Image",
                },
            ],
            Keywords = [],
        };

        const string sql =
            "WITH thumb AS (SELECT video_frame_to_image(frame, 800) AS prev_image " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT prev_image FROM thumb";

        CteSchemaResult result = CteSchemaResolver.Resolve(sql, enrichedManifest);

        Assert.True(result.Schemas.ContainsKey("thumb"));
        IReadOnlyList<TableColumnEntry> cols = result.Schemas["thumb"];
        TableColumnEntry? prev = cols.FirstOrDefault(c => c.Name == "prev_image");
        Assert.NotNull(prev);
        Assert.Equal("Image", prev.Kind);
    }

    [Fact]
    public void Resolve_LetBindingReferencedInProjection_ResolvesThroughLetScope()
    {
        // `LET prev_image = video_frame_to_image(frame, 800)` then a
        // sibling SELECT-list expression that references `prev_image AS
        // out`. The output `out` must resolve to Image — the resolver
        // walks the LET-scope when a column-ref name doesn't match a
        // FROM-source column.
        LanguageServerManifest enrichedManifest = new()
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
                new FunctionSignature
                {
                    SchemaName = "system",
                    Name = "video_frame_to_image",
                    Parameters =
                    [
                        new ParameterSignature { Name = "frame", Kind = "VideoFrame" },
                        new ParameterSignature { Name = "max_dim", Kind = "Int32", IsOptional = true },
                    ],
                    ReturnType = "Image",
                },
            ],
            Keywords = [],
        };

        // LET defines prev_image; SELECT-list `prev_image AS out` projects
        // it under a different name. The resolver must chase out → prev_image
        // → video_frame_to_image(...) → Image.
        const string sql =
            "WITH thumb AS (SELECT " +
            "  LET prev_image = video_frame_to_image(frame, 800), " +
            "  prev_image AS out_image " +
            "FROM video_unnest_frames('x.mp4') vid) " +
            "SELECT out_image FROM thumb";

        CteSchemaResult result = CteSchemaResolver.Resolve(sql, enrichedManifest);

        Assert.True(result.Schemas.ContainsKey("thumb"));
        TableColumnEntry? outCol = result.Schemas["thumb"].FirstOrDefault(c => c.Name == "out_image");
        Assert.NotNull(outCol);
        Assert.Equal("Image", outCol.Kind);
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
