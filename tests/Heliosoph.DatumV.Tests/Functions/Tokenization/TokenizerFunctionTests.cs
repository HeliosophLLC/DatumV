using System.Text;
using System.Text.Json;

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Tests.Functions.Tokenization;

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

    private static async Task<long[]> CollectFirstArrayAsync(StatementPlan plan)
    {
        long[]? result = null;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
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

    private static async Task<string> CollectFirstStringAsync(StatementPlan plan)
    {
        string? result = null;
        await foreach (RowBatch batch in ExecutePlanAsync(plan))
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
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{_tokenizerJsonPath}')");

        long[] ids = await CollectFirstArrayAsync(plan);

        // No merges + alphabet-only vocab → one id per character.
        Assert.Equal(5, ids.Length);
    }

    [Fact]
    public async Task Encode_BpeForm_AgreesWithTokenizerJsonForm()
    {
        TableCatalog catalog = CreateCatalog();
        StatementPlan planJson = catalog.Plan(
            $"SELECT tokenizer.encode('cat dog', 'file://{_tokenizerJsonPath}')");
        StatementPlan planBpe = catalog.Plan(
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
        StatementPlan encodePlan = catalog.Plan(
            $"SELECT tokenizer.encode('helloworld', 'file://{_tokenizerJsonPath}')");
        long[] ids = await CollectFirstArrayAsync(encodePlan);

        string idLiteral = "[" + string.Join(", ", ids.Select(id => $"CAST({id} AS Int64)")) + "]";
        StatementPlan decodePlan = catalog.Plan(
            $"SELECT tokenizer.decode({idLiteral}, 'file://{_tokenizerJsonPath}')");

        string decoded = await CollectFirstStringAsync(decodePlan);
        Assert.Equal("helloworld", decoded);
    }

    [Fact]
    public async Task EncodeDecode_RoundTrips_BpeForm()
    {
        TableCatalog catalog = CreateCatalog();
        StatementPlan encodePlan = catalog.Plan(
            $"SELECT tokenizer.encode_bpe('helloworld', 'file://{_vocabJsonPath}', 'file://{_mergesPath}')");
        long[] ids = await CollectFirstArrayAsync(encodePlan);

        string idLiteral = "[" + string.Join(", ", ids.Select(id => $"CAST({id} AS Int64)")) + "]";
        StatementPlan decodePlan = catalog.Plan(
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
    private static async Task<Exception> CaptureRootExceptionAsync(StatementPlan plan)
    {
        Exception thrown = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (RowBatch _ in ExecutePlanAsync(plan)) { }
        });
        Exception current = thrown;
        while (current.InnerException is not null) current = current.InnerException;
        return current;
    }

    [Fact]
    public async Task Encode_RelativePath_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalog();
        StatementPlan plan = catalog.Plan(
            "SELECT tokenizer.encode('hello', 'tokenizer.json')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.Contains("relative path", root.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Encode_MissingFile_ThrowsClearError()
    {
        TableCatalog catalog = CreateCatalog();
        string missing = Path.Combine(_tmpDir, "does-not-exist.json");
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{missing}')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.IsType<FileNotFoundException>(root);
        Assert.Contains("tokenizer.json", root.Message);
    }

    [Fact]
    public async Task EncodeRoberta_TokenizerJson_WrapsWithSpecialTokens()
    {
        // Encode "abc" — 3 alphabet tokens — and verify the RoBERTa wrapping:
        //   input_ids = [<s>=0, ...3 char tokens..., </s>=2]   length 5
        //   attention_mask = [1, 1, 1, 1, 1]                    length 5
        // Field names match RoBERTa ONNX inputs (input_ids + attention_mask
        // only — no token_type_ids unlike BERT).
        TableCatalog catalog = CreateCatalog();
        StatementPlan idsPlan = catalog.Plan(
            $"SELECT tokenizer.encode_roberta('abc', 'file://{_tokenizerJsonPath}')['input_ids']");
        long[] inputIds = await CollectFirstArrayAsync(idsPlan);

        Assert.Equal(5, inputIds.Length);
        Assert.Equal(0L, inputIds[0]);     // <s>
        Assert.Equal(2L, inputIds[^1]);    // </s>
        // Middle 3 ids are the BPE tokens for 'a', 'b', 'c'. The fixture
        // assigns ids 1..26 to 'a'..'z'.
        Assert.Equal(1L, inputIds[1]); // 'a'
        Assert.Equal(2L, inputIds[2]); // 'b'
        Assert.Equal(3L, inputIds[3]); // 'c'

        StatementPlan maskPlan = catalog.Plan(
            $"SELECT tokenizer.encode_roberta('abc', 'file://{_tokenizerJsonPath}')['attention_mask']");
        long[] mask = await CollectFirstArrayAsync(maskPlan);

        Assert.Equal(5, mask.Length);
        foreach (long m in mask) Assert.Equal(1L, m);
    }

    [Fact]
    public async Task EncodeRoberta_EmptyText_StillEmitsBosEosOnly()
    {
        TableCatalog catalog = CreateCatalog();
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode_roberta('', 'file://{_tokenizerJsonPath}')['input_ids']");
        long[] inputIds = await CollectFirstArrayAsync(plan);

        // Empty input → just <s> + </s>.
        Assert.Equal(2, inputIds.Length);
        Assert.Equal(0L, inputIds[0]);
        Assert.Equal(2L, inputIds[1]);
    }

    /// <summary>
    /// Writes a minimal BERT-style <c>vocab.txt</c> at <paramref name="path"/>:
    /// the five canonical special tokens (<c>[PAD]</c>, <c>[UNK]</c>,
    /// <c>[CLS]</c>, <c>[SEP]</c>, <c>[MASK]</c>) followed by the lowercase
    /// alphabet (one-char word starts) and the <c>##</c>-prefixed continuation
    /// pieces (one per letter) needed for WordPiece to segment multi-char
    /// tokens without falling back to <c>[UNK]</c>. BertTokenizer keys off
    /// line number for the id, so <c>[CLS]=2</c>, <c>[SEP]=3</c>,
    /// <c>a=5</c>..<c>z=30</c>, <c>##a=31</c>..<c>##z=56</c>.
    /// </summary>
    private static void WriteBertVocab(string path)
    {
        StringBuilder sb = new();
        sb.AppendLine("[PAD]");
        sb.AppendLine("[UNK]");
        sb.AppendLine("[CLS]");
        sb.AppendLine("[SEP]");
        sb.AppendLine("[MASK]");
        for (char c = 'a'; c <= 'z'; c++) sb.AppendLine(c.ToString());
        for (char c = 'a'; c <= 'z'; c++) sb.AppendLine("##" + c);
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
    }

    [Fact]
    public async Task EncodeBertPair_AssemblesQueryAndPassageWithSeparator()
    {
        // Pair-encode "ab" + "cd". With WordPiece + ##-continuation pieces in
        // the fixture vocab, "ab" segments as [a=5, ##b=32] and "cd" as
        // [c=7, ##d=34]. Expected layout:
        //   input_ids      = [2, 5, 32, 3, 7, 34, 3]   ; [CLS] a ##b [SEP] c ##d [SEP]
        //   attention_mask = [1, 1, 1, 1, 1, 1, 1]
        //   token_type_ids = [0, 0, 0, 0, 1, 1, 1]    ; query segment then passage segment
        string bertVocab = Path.Combine(_tmpDir, "bert-pair-vocab.txt");
        WriteBertVocab(bertVocab);

        TableCatalog catalog = CreateCatalog();
        StatementPlan idsPlan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('ab', 'cd', 'file://{bertVocab}')['input_ids']");
        long[] ids = await CollectFirstArrayAsync(idsPlan);
        Assert.Equal(new long[] { 2, 5, 32, 3, 7, 34, 3 }, ids);

        StatementPlan maskPlan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('ab', 'cd', 'file://{bertVocab}')['attention_mask']");
        long[] mask = await CollectFirstArrayAsync(maskPlan);
        Assert.Equal(new long[] { 1, 1, 1, 1, 1, 1, 1 }, mask);

        StatementPlan typePlan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('ab', 'cd', 'file://{bertVocab}')['token_type_ids']");
        long[] types = await CollectFirstArrayAsync(typePlan);
        Assert.Equal(new long[] { 0, 0, 0, 0, 1, 1, 1 }, types);
    }

    [Fact]
    public async Task EncodeBertPair_SingleCharPair_StillHasFiveTokens()
    {
        // Smallest non-empty pair: each side is one token. Layout has the
        // five specials/glue + the two content tokens.
        string bertVocab = Path.Combine(_tmpDir, "bert-pair-min-vocab.txt");
        WriteBertVocab(bertVocab);

        TableCatalog catalog = CreateCatalog();
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('a', 'b', 'file://{bertVocab}')['input_ids']");
        long[] ids = await CollectFirstArrayAsync(plan);

        // [CLS=2] a=5 [SEP=3] b=6 [SEP=3]
        Assert.Equal(new long[] { 2, 5, 3, 6, 3 }, ids);
    }

    [Fact]
    public async Task EncodeBertPair_TokenTypeIds_FlipAtTheFirstSep()
    {
        // The "right place for the flip" property: every token at or before
        // the first [SEP] should have type=0; every token after it should
        // have type=1. Encodes "aa bb" + "cc" so the query side has multiple
        // tokens — verifies the flip isn't off-by-one.
        string bertVocab = Path.Combine(_tmpDir, "bert-pair-flip-vocab.txt");
        WriteBertVocab(bertVocab);

        TableCatalog catalog = CreateCatalog();
        StatementPlan idsPlan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('aa bb', 'cc', 'file://{bertVocab}')['input_ids']");
        long[] ids = await CollectFirstArrayAsync(idsPlan);

        StatementPlan typePlan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair('aa bb', 'cc', 'file://{bertVocab}')['token_type_ids']");
        long[] types = await CollectFirstArrayAsync(typePlan);

        Assert.Equal(ids.Length, types.Length);

        // First [SEP] (id=3) appears once on the query→passage boundary.
        int firstSepIndex = Array.IndexOf(ids, 3L);
        Assert.True(firstSepIndex > 0, "Expected a [SEP] separator between query and passage.");

        for (int i = 0; i <= firstSepIndex; i++) Assert.Equal(0L, types[i]);
        for (int i = firstSepIndex + 1; i < types.Length; i++) Assert.Equal(1L, types[i]);
    }

    [Fact]
    public void EncodeBertPair_NullArgument_ReturnsNullStruct()
    {
        // Mirrors encode_bert / encode_roberta: any NULL argument short-circuits
        // to a null struct so downstream propagation works the same way.
        string bertVocab = Path.Combine(_tmpDir, "bert-pair-null-vocab.txt");
        WriteBertVocab(bertVocab);

        TableCatalog catalog = CreateCatalog();
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode_bert_pair(CAST(NULL AS String), 'cd', 'file://{bertVocab}') IS NULL");
        DataValue? value = null;
        foreach (RowBatch batch in ExecutePlanAsync(plan)
                     .ToBlockingEnumerable(CancellationToken.None))
        {
            value = batch[0][0];
        }
        Assert.NotNull(value);
        Assert.True(value.Value.AsBoolean());
    }

    [Fact]
    public async Task Encode_UnsupportedModelType_ThrowsClearError()
    {
        string unigramPath = Path.Combine(_tmpDir, "unigram.json");
        File.WriteAllText(unigramPath,
            """{"version":"1.0","model":{"type":"Unigram","vocab":[],"unk_id":0}}""");

        TableCatalog catalog = CreateCatalog();
        StatementPlan plan = catalog.Plan(
            $"SELECT tokenizer.encode('hello', 'file://{unigramPath}')");

        Exception root = await CaptureRootExceptionAsync(plan);
        Assert.IsType<NotSupportedException>(root);
        Assert.Contains("Unigram", root.Message);
        Assert.Contains("BPE", root.Message);
    }
}
