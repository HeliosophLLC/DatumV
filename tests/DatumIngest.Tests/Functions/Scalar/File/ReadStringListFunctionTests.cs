using System.Text.Json;

using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Functions.Scalar.File;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Scalar.File;

/// <summary>
/// Covers <c>read_string_list(path String) → Array&lt;String&gt;</c>: the
/// shared catalog-relative JSON-array reader used by SQL-defined models
/// (YOLOX label lookup, future classifier vocabularies).
/// </summary>
public sealed class ReadStringListFunctionTests : ServiceTestBase, IDisposable
{
    private readonly string _tempPath;

    public ReadStringListFunctionTests()
    {
        _tempPath = Path.Combine(Path.GetTempPath(),
            $"datum-test-string-list-{Guid.NewGuid():N}.json");
    }

    public override void Dispose()
    {
        if (System.IO.File.Exists(_tempPath))
        {
            System.IO.File.Delete(_tempPath);
        }
    }

    private async Task<string[]> InvokeAsync(string path, EvaluationFrame? frame = null)
    {
        ReadStringListFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.FromString(path) }.AsMemory(),
            frame ?? CreateEvaluationFrame(),
            CancellationToken.None);
        Assert.True(result.IsArray);
        Assert.False(result.IsNull);
        ReadOnlySpan<ValueRef> elements = result.GetArrayElements();
        string[] strings = new string[elements.Length];
        for (int i = 0; i < elements.Length; i++)
        {
            strings[i] = elements[i].AsString();
        }
        return strings;
    }

    [Fact]
    public async Task ReadsJsonArrayOfStrings_AbsolutePath_Roundtrips()
    {
        string[] labels = ["person", "bicycle", "car"];
        await System.IO.File.WriteAllTextAsync(
            _tempPath, JsonSerializer.Serialize(labels));

        string[] read = await InvokeAsync(_tempPath);

        Assert.Equal(labels, read);
    }

    [Fact]
    public async Task FileUriPrefix_StripsAndResolves()
    {
        string[] labels = ["a", "b"];
        await System.IO.File.WriteAllTextAsync(
            _tempPath, JsonSerializer.Serialize(labels));

        string[] read = await InvokeAsync("file://" + _tempPath);

        Assert.Equal(labels, read);
    }

    [Fact]
    public async Task RelativePath_OutsideModelBody_ThrowsWithClearMessage()
    {
        // No CurrentModel on the frame — relative paths can't resolve.
        FunctionArgumentException ex = await Assert.ThrowsAsync<FunctionArgumentException>(
            () => InvokeAsync("coco-classes.json"));
        Assert.Contains("relative path", ex.Message);
        Assert.Contains("CREATE MODEL", ex.Message);
    }

    [Fact]
    public async Task MissingFile_ThrowsFileNotFound()
    {
        string nonexistent = Path.Combine(Path.GetTempPath(),
            $"datum-nonexistent-{Guid.NewGuid():N}.json");
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => InvokeAsync(nonexistent));
    }

    [Fact]
    public async Task NonArrayJson_ThrowsInvalidData()
    {
        await System.IO.File.WriteAllTextAsync(_tempPath, """{"not": "array"}""");
        await Assert.ThrowsAsync<InvalidDataException>(
            () => InvokeAsync(_tempPath));
    }

    [Fact]
    public async Task NonStringElement_ThrowsInvalidData()
    {
        await System.IO.File.WriteAllTextAsync(_tempPath, """["ok", 42]""");
        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => InvokeAsync(_tempPath));
        Assert.Contains("index 1", ex.Message);
    }

    [Fact]
    public async Task NullPath_ReturnsNullArray()
    {
        ReadStringListFunction fn = new();
        ValueRef result = await fn.ExecuteAsync(
            new ValueRef[] { ValueRef.Null(DataKind.String) }.AsMemory(),
            CreateEvaluationFrame(),
            CancellationToken.None);
        Assert.True(result.IsNull);
        Assert.Equal(DataKind.String, result.Kind);
    }

    [Fact]
    public async Task SecondCall_HitsCache_ReturnsSameContent()
    {
        // Write the file, read once, mutate the file on disk, read again.
        // Cache is by absolute path so second read should return the
        // original content (proves the file isn't re-read every call).
        await System.IO.File.WriteAllTextAsync(_tempPath, """["a", "b"]""");
        string[] first = await InvokeAsync(_tempPath);

        await System.IO.File.WriteAllTextAsync(_tempPath, """["x", "y", "z"]""");
        string[] second = await InvokeAsync(_tempPath);

        Assert.Equal(first, second);  // cached — second write doesn't show up
        Assert.Equal(["a", "b"], first);
    }
}
