using Heliosoph.DatumV.ModelLibrary;
using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Tests.ModelLibrary;

/// <summary>
/// URL-shape coverage for <see cref="HuggingFaceSourceClient"/>. The client
/// supports both model and dataset repos; the only thing that differs is the
/// API namespace segment (<c>api/models/...</c> vs <c>api/datasets/...</c>)
/// and the download URL prefix (root vs <c>datasets/</c>). Tests capture the
/// outbound HttpRequestMessage URL via a stub handler so we don't need a live
/// HF API call.
/// </summary>
public sealed class HuggingFaceSourceClientTests
{
    [Fact]
    public async Task ListFilesAsync_ModelRepo_HitsModelsApiNamespace()
    {
        CapturingHandler handler = new(
            """[{"type":"file","oid":"abc","size":42,"path":"model.onnx","lfs":null}]""");
        HuggingFaceSourceClient client = NewClient(handler);

        HuggingFaceSource source = new(
            Repo: "owner/some-model",
            Revision: "main",
            Include: ["*.onnx"]);

        await client.ListFilesAsync(source, CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            "https://huggingface.co/api/models/owner/some-model/tree/main?recursive=true",
            handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ListFilesAsync_DatasetRepo_HitsDatasetsApiNamespace()
    {
        CapturingHandler handler = new(
            """[{"type":"file","oid":"abc","size":42,"path":"data.tar.gz","lfs":null}]""");
        HuggingFaceSourceClient client = NewClient(handler);

        HuggingFaceSource source = new(
            Repo: "Heliosoph.DatumV/LJ-Speech-Dataset",
            Revision: "main",
            Include: ["*.tar.gz"],
            RepoType: "dataset");

        await client.ListFilesAsync(source, CancellationToken.None);

        Assert.NotNull(handler.LastRequestUri);
        Assert.Equal(
            "https://huggingface.co/api/datasets/Heliosoph.DatumV/LJ-Speech-Dataset/tree/main?recursive=true",
            handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task ListFilesAsync_UnknownRepoType_ThrowsWithUsefulMessage()
    {
        CapturingHandler handler = new("[]");
        HuggingFaceSourceClient client = NewClient(handler);

        HuggingFaceSource source = new(
            Repo: "x/y",
            Revision: "main",
            Include: ["*"],
            RepoType: "space");

        InvalidOperationException ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ListFilesAsync(source, CancellationToken.None).AsTask());
        Assert.Contains("repoType", ex.Message);
        Assert.Contains("space", ex.Message);
    }

    [Fact]
    public async Task DownloadFileAsync_DatasetRepo_HitsDatasetsResolvePath()
    {
        // Two-stage response: first ListFiles (returns one file), then the
        // download GET. We only care that the second request hits the
        // /datasets/{repo}/resolve/... path.
        SequencedHandler handler = new(
        [
            ("""[{"type":"file","oid":"abc","size":4,"path":"a.bin","lfs":null}]""", null),
            ("data", "application/octet-stream"),
        ]);
        HuggingFaceSourceClient client = NewClient(handler);

        HuggingFaceSource source = new(
            Repo: "Heliosoph.DatumV/LJ-Speech-Dataset",
            Revision: "main",
            Include: ["a.bin"],
            RepoType: "dataset");

        IReadOnlyList<SourceFile> files = await client.ListFilesAsync(source, CancellationToken.None);
        Assert.Single(files);

        string destPath = Path.Combine(Path.GetTempPath(), $"datumv-hf-test-{Guid.NewGuid():N}.bin");
        try
        {
            await client.DownloadFileAsync(source, files[0], destPath, progress: null, CancellationToken.None);
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }

        Assert.Equal(
            "https://huggingface.co/datasets/Heliosoph.DatumV/LJ-Speech-Dataset/resolve/main/a.bin",
            handler.LastRequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task DownloadFileAsync_ModelRepo_HitsRootResolvePath()
    {
        SequencedHandler handler = new(
        [
            ("""[{"type":"file","oid":"abc","size":4,"path":"model.onnx","lfs":null}]""", null),
            ("xxxx", "application/octet-stream"),
        ]);
        HuggingFaceSourceClient client = NewClient(handler);

        HuggingFaceSource source = new(
            Repo: "owner/some-model",
            Revision: "main",
            Include: ["*.onnx"]);

        IReadOnlyList<SourceFile> files = await client.ListFilesAsync(source, CancellationToken.None);

        string destPath = Path.Combine(Path.GetTempPath(), $"datumv-hf-test-{Guid.NewGuid():N}.onnx");
        try
        {
            await client.DownloadFileAsync(source, files[0], destPath, progress: null, CancellationToken.None);
        }
        finally
        {
            if (File.Exists(destPath)) File.Delete(destPath);
        }

        Assert.Equal(
            "https://huggingface.co/owner/some-model/resolve/main/model.onnx",
            handler.LastRequestUri!.AbsoluteUri);
    }

    private static HuggingFaceSourceClient NewClient(HttpMessageHandler handler)
    {
        HttpClient http = new(handler) { BaseAddress = new Uri("https://huggingface.co/") };
        return new HuggingFaceSourceClient(http, NullLogger<HuggingFaceSourceClient>.Instance);
    }

    private sealed class CapturingHandler(string jsonBody) : HttpMessageHandler
    {
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            HttpResponseMessage resp = new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(resp);
        }
    }

    private sealed class SequencedHandler(IReadOnlyList<(string Body, string? ContentType)> responses)
        : HttpMessageHandler
    {
        private int _index;
        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            (string body, string? contentType) = responses[_index++];
            HttpResponseMessage resp = new(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, contentType ?? "application/json"),
            };
            return Task.FromResult(resp);
        }
    }
}
