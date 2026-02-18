using System.Text;
using System.Text.Json;

using DatumIngest.Catalog;
using DatumIngest.Model;

namespace DatumIngest.Tests.Functions.Tokenization;

/// <summary>
/// Round-trip + dispatch tests for the four tokenizer functions:
/// <c>tokenizer.encode</c> / <c>tokenizer.decode</c> (tokenizer.json form)
/// and <c>tokenizer.encode_bpe</c> / <c>tokenizer.decode_bpe</c> (separate
/// vocab.json + merges.txt files).
/// </summary>
/// <remarks>
/// Tokenizer fixtures are synthesised in test setup: a minimal alphabet
/// vocab (one token per ASCII lowercase letter + space) with zero merges,
/// written to a temp directory. That gives a deterministic 1-character-per-
/// token segmentation we can pin specific expected ids against without
/// depending on a downloaded model's tokenizer.
/// </remarks>
public sealed class TokenizerFunctionTests : ServiceTestBase, IDisposable
{
    private readonly string _tmpDir;
    private readonly string _tokenizerJsonPath;
    private readonly string _vocabJsonPath;
    private readonly string _mergesPath;

    public TokenizerFunctionTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), $"tokenizer-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tmpDir);
        _tokenizerJsonPath = Path.Combine(_tmpDir, "tokenizer.json");
        _vocabJsonPath = Path.Combine(_tmpDir, "vocab.json");
        _mergesPath = Path.Combine(_tmpDir, "merges.txt");

        WriteFixtures();
    }

    public override void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { /* best-effort */ }
        base.Dispose();
    }

    private void WriteFixtures()
    {
        // Build a minimal vocab: <unk> at id 0, ASCII lowercase a–z, then a
        // few special characters and a space. No merges → BPE collapses to
        // one token per matched character (or <unk> when nothing matches).
        Dictionary<string, int> vocab = new();
        int next = 0;
        vocab["<unk>"] = next++;
        for (char c = 'a'; c <= 'z'; c++) vocab[c.ToString()] = next++;
        vocab[" "] = next++;

        // Write tokenizer.json (BPE-format wrapper).
        using (FileStream fs = File.Create(_tokenizerJsonPath))
        using (Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("version", "1.0");
            w.WriteStartObject("model");
            w.WriteString("type", "BPE");
            w.WritePropertyName("vocab");
            w.WriteStartObject();
            foreach ((string token, int id) in vocab)
            {
                w.WriteNumber(token, id);
            }
            w.WriteEndObject();
            w.WriteStartArray("merges");
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }

        // Write vocab.json (same shape — BPE just wants the token→id dict).
        using (FileStream fs = File.Create(_vocabJsonPath))
        using (Utf8JsonWriter w = new(fs, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach ((string token, int id) in vocab)
            {
                w.WriteNumber(token, id);
            }
            w.WriteEndObject();
        }

        // Empty merges.txt — no pair merges.
        File.WriteAllText(_mergesPath, "", Encoding.UTF8);
    }

    private static async Task<long[]> CollectFirstArrayAsync(IQueryPlan plan)
    {
        long[]? result = null;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                Assert.True(cell.IsArray, $"Expected array, got Kind={cell.Kind}");
                result = cell.AsArraySpan<long>(batch.Arena).ToArray();
            }
        }
        Assert.NotNull(result);
        return result!;
    }

    private static async Task<string> CollectFirstStringAsync(IQueryPlan plan)
    {
        string? result = null;
        await foreach (RowBatch batch in plan.ExecuteAsync(CancellationToken.None))
        {
            for (int i = 0; i < batch.Count; i++)
            {
                DataValue cell = batch[i][0];
                Assert.Equal(DataKind.String, cell.Kind);
                result = cell.AsString(batch.Arena);
            }
        }
        Assert.NotNull(result);
        return result!;
    }

    [Fact]
    public async Task Encode_TokenizerJson_SegmentsKnownString()
    {
        TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{_tokenizerJsonPath}')");

        long[] ids = await CollectFirstArrayAsync(plan);

        // No merges + alphabet-only vocab → one id per character.
        Assert.Equal(5, ids.Length);
    }

    [Fact]
    public async Task Encode_BpeForm_AgreesWithTokenizerJsonForm()
    {
        TableCatalog catalog = CreateCatalog();
        IQueryPlan planJson = catalog.Plan(
            $"SELECT tokenizer.encode('cat dog', 'file://{_tokenizerJsonPath}')");
        IQueryPlan planBpe = catalog.Plan(
            $"SELECT tokenizer.encode_bpe('cat dog', 'file://{_vocabJsonPath}', 'file://{_mergesPath}')");

        long[] idsJson = await CollectFirstArrayAsync(planJson);
        long[] idsBpe = await CollectFirstArrayAsync(planBpe);

        Assert.Equal(idsJson, idsBpe);
    }

    [Fact]
    public async Task EncodeDecode_RoundTrips_TokenizerJson()
    {
        // Round-trip on a no-whitespace input. The minimal vocab + zero-
        // merges fixture relies on BpeTokenizer's default pre-tokenizer,
        // which strips inter-word spaces on decode unless special Ġ-prefixed
        // tokens are in vocab (GPT-2 convention). Real model tokenizers ship
        // those tokens; the fixture intentionally doesn't — so testing the
        // round-trip property without whitespace keeps the assertion
        // independent of pre-tokenizer choice.
        TableCatalog catalog = CreateCatalog();
        IQueryPlan encodePlan = catalog.Plan(
            $"SELECT tokenizer.encode('helloworld', 'file://{_tokenizerJsonPath}')");
        long[] ids = await CollectFirstArrayAsync(encodePlan);

        string idLiteral = "[" + string.Join(", ", ids.Select(id => $"CAST({id} AS Int64)")) + "]";
        IQueryPlan decodePlan = catalog.Plan(
            $"SELECT tokenizer.decode({idLiteral}, 'file://{_tokenizerJsonPath}')");

        string decoded = await CollectFirstStringAsync(decodePlan);
        Assert.Equal("helloworld", decoded);
    }

    [Fact]
    public async Task EncodeDecode_RoundTrips_BpeForm()
    {
        TableCatalog catalog = CreateCatalog();
        IQueryPlan encodePlan = catalog.Plan(
            $"SELECT tokenizer.encode_bpe('helloworld', 'file://{_vocabJsonPath}', 'file://{_mergesPath}')");
        long[] ids = await CollectFirstArrayAsync(encodePlan);

        string idLiteral = "[" + string.Join(", ", ids.Select(id => $"CAST({id} AS Int64)")) + "]";
        IQueryPlan decodePlan = catalog.Plan(
            $"SELECT tokenizer.decode_bpe({idLiteral}, 'file://{_vocabJsonPath}', 'file://{_mergesPath}')");

        string decoded = await CollectFirstStringAsync(decodePlan);
        Assert.Equal("helloworld", decoded);
    }

    /// <summary>
    /// Drains the query plan and returns the root-cause exception walking
    /// past the planner's <c>ExpressionEvaluationException</c> wrapper.
    /// Per-cell function failures get wrapped with a positional context;
    /// the diagnostic users want comes from the inner exception.
    /// </summary>
    private static async Task<Exception> CaptureRootExceptionAsync(IQueryPlan plan)
    {
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (RowBatch _ in plan.ExecuteAsync(CancellationToken.None)) { }
        });
        Exception current = thrown;
        while (current.InnerException is not null) current = current.InnerException;
        return current;
    }

    [Fact]
    public async Task Encode_RelativePath_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(
            "SELECT tokenizer.encode('hello', 'tokenizer.json')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.Contains("relative path", root.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Encode_MissingFile_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalog();
        string missing = Path.Combine(_tmpDir, "does-not-exist.json");
        IQueryPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{missing}')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.IsType<FileNotFoundException>(root);
        Assert.Contains("tokenizer.json", root.Message);
    }

    [Fact]
    public async Task Encode_UnsupportedModelType_ThrowsClearError()
    {
        string unigramPath = Path.Combine(_tmpDir, "unigram.json");
        File.WriteAllText(unigramPath,
            """{"version":"1.0","model":{"type":"Unigram","vocab":[],"unk_id":0}}""");

        TableCatalog catalog = CreateCatalog();
        IQueryPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{unigramPath}')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.IsType<NotSupportedException>(root);
        Assert.Contains("Unigram", root.Message);
        Assert.Contains("BPE", root.Message);
    }
}
