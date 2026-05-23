using System.Text;
using System.Text.Json;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Pooling;
using Heliosoph.DatumV.Web.Api;
using Heliosoph.DatumV.Web.Dtos.Files;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Heliosoph.DatumV.Web.Tests;

/// <summary>
/// Covers the file contents + per-catalog state surfaces added on top of
/// the original read-only GET /api/files. A real <see cref="TableCatalog"/>
/// rooted at a temp directory exercises path resolution, gitignore
/// bootstrap, and the .datumv/tabs.json round-trip end-to-end without
/// spinning up the HTTP pipeline.
/// </summary>
public sealed class FilesControllerTests : IDisposable
{
    private readonly string _root;
    private readonly TableCatalog _catalog;
    private readonly ServiceProvider _sp;

    public FilesControllerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "datumv-files-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);

        ServiceCollection services = new();
        services.AddDatumV();
        _sp = services.BuildServiceProvider();
        Pool pool = _sp.GetRequiredService<Pool>();
        string catalogPath = Path.Combine(_root, CatalogStore.DefaultFileName);
        _catalog = new TableCatalog(pool, catalogPath);
    }

    public void Dispose()
    {
        _catalog.Dispose();
        _sp.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private FilesController CreateController() => new(_catalog);

    private static JsonElement Parse(string json) =>
        JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void GetRoot_ReturnsCatalogDirectory()
    {
        ActionResult<CatalogRootDto> result = CreateController().GetRoot();
        CatalogRootDto dto = Assert.IsType<CatalogRootDto>(result.Value);
        Assert.Equal(_root, dto.CatalogRoot);
    }

    [Fact]
    public async Task PutContents_ThenGetContents_RoundTripsUtf8()
    {
        FileContentsRequestDto body = new("SELECT 1; -- 你好\n");
        ActionResult<FileContentsResponseDto> put = await CreateController()
            .PutContents("queries/hello.sql", body, default);
        FileContentsResponseDto putDto = Assert.IsType<FileContentsResponseDto>(put.Value);
        Assert.True(putDto.ModifiedAt <= DateTimeOffset.UtcNow);

        string written = await File.ReadAllTextAsync(
            Path.Combine(_root, "queries", "hello.sql"), Encoding.UTF8);
        Assert.Equal("SELECT 1; -- 你好\n", written);

        ActionResult<FileContentsDto> get = await CreateController()
            .GetContents("queries/hello.sql", default);
        FileContentsDto getDto = Assert.IsType<FileContentsDto>(get.Value);
        Assert.Equal("SELECT 1; -- 你好\n", getDto.Contents);
    }

    [Fact]
    public async Task GetContents_FileMissing_Returns404()
    {
        ActionResult<FileContentsDto> result = await CreateController()
            .GetContents("does-not-exist.sql", default);
        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Theory]
    [InlineData("../escape.sql")]
    [InlineData("queries/../../escape.sql")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/System32/cmd.exe")]
    public async Task GetContents_PathEscape_Returns400(string path)
    {
        ActionResult<FileContentsDto> result = await CreateController()
            .GetContents(path, default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetContents_EmptyPath_Returns400()
    {
        ActionResult<FileContentsDto> result = await CreateController()
            .GetContents("", default);
        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task PutTabsState_CreatesStateDir_AndGitignoreEntry()
    {
        const string body = """{"root":{"kind":"leaf","tabs":[]}}""";
        ActionResult result = await CreateController().PutTabsState(Parse(body), default);
        Assert.IsType<NoContentResult>(result);

        string tabsPath = Path.Combine(_root, ".datumv", "tabs.json");
        Assert.True(File.Exists(tabsPath));
        // GetRawText preserves the original JSON byte-for-byte.
        Assert.Equal(body, await File.ReadAllTextAsync(tabsPath, Encoding.UTF8));

        string gitignorePath = Path.Combine(_root, ".gitignore");
        Assert.True(File.Exists(gitignorePath));
        string gi = await File.ReadAllTextAsync(gitignorePath, Encoding.UTF8);
        Assert.Contains(".datumv/", gi);
    }

    [Fact]
    public async Task PutTabsState_GitignoreAppend_IsIdempotent()
    {
        string gitignorePath = Path.Combine(_root, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "node_modules/\n.datumv/\n");
        const string body = """{"root":{"kind":"leaf","tabs":[]}}""";

        await CreateController().PutTabsState(Parse(body), default);
        await CreateController().PutTabsState(Parse(body), default);

        string gi = await File.ReadAllTextAsync(gitignorePath, Encoding.UTF8);
        // Exactly one .datumv/ line — no duplicate appended on the second write.
        int count = gi.Split('\n').Count(l => l.Trim().TrimEnd('\r') == ".datumv/");
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task PutTabsState_AcceptsBareEntryInExistingGitignore()
    {
        string gitignorePath = Path.Combine(_root, ".gitignore");
        // The trailing-slash form and the bare-name form both cover the
        // directory; either should suppress the append.
        await File.WriteAllTextAsync(gitignorePath, ".datumv\n");

        await CreateController().PutTabsState(Parse("{}"), default);

        string gi = await File.ReadAllTextAsync(gitignorePath, Encoding.UTF8);
        Assert.DoesNotContain(".datumv/", gi);
    }

    [Fact]
    public async Task GetTabsState_Missing_Returns204()
    {
        ActionResult<string> result = await CreateController().GetTabsState(default);
        Assert.IsType<NoContentResult>(result.Result);
    }

    [Fact]
    public async Task GetTabsState_AfterPut_RoundTrips()
    {
        const string body = """{"root":{"kind":"leaf","tabs":[],"id":"l1","activeTabId":null},"focusedLeafId":"l1"}""";
        await CreateController().PutTabsState(Parse(body), default);

        ActionResult<string> result = await CreateController().GetTabsState(default);
        ContentResult content = Assert.IsType<ContentResult>(result.Result);
        Assert.Equal(body, content.Content);
        Assert.Equal("application/json; charset=utf-8", content.ContentType);
    }
}
