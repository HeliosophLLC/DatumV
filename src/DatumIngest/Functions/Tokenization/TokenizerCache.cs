using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

using Microsoft.ML.Tokenizers;

namespace DatumIngest.Functions.Tokenization;

/// <summary>
/// Process-wide cache of loaded <see cref="BpeTokenizer"/> instances, keyed by
/// the canonical source paths. Tokenizers are immutable post-load and the
/// shapes Microsoft.ML.Tokenizers builds (vocab dictionaries, merge-rank
/// tables, regex pre-tokenizers) are thread-safe to share across calls — so
/// one tokenizer per <c>(path…)</c> is correct *and* the only way to make a
/// batched <c>SELECT tokenizer.encode(text_col, '/path') FROM big_table</c>
/// not pay the load cost per row.
/// </summary>
/// <remarks>
/// <para>
/// <strong>v1 scope.</strong> Only BPE-format <c>tokenizer.json</c> is
/// supported. Unigram / WordPiece / SentencePiece tokenizers throw a clear
/// "not yet supported" error pointing at the algorithm; follow-ups can extend
/// <see cref="LoadFromTokenizerJson"/> with per-algorithm dispatch when a
/// real consumer needs them.
/// </para>
/// <para>
/// <strong>Eviction.</strong> The v1 cache is unbounded — tokenizers are
/// small (a few hundred KB to a few MB) and a process tends to use a fixed
/// set of them across its lifetime. A follow-up integrates this with
/// <c>ModelResidencyManager</c> (or a renamed sibling) so RAM pressure can
/// evict tokenizers alongside model weights instead of holding them forever.
/// </para>
/// </remarks>
internal static class TokenizerCache
{
    private static readonly ConcurrentDictionary<string, BpeTokenizer> _byTokenizerJson = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<(string Vocab, string Merges), BpeTokenizer> _byVocabMerges = new();
    private static readonly ConcurrentDictionary<string, BertTokenizer> _byBertVocab = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns the BERT/WordPiece tokenizer for <paramref name="vocabPath"/>
    /// (a vocab.txt with one wordpiece per line), loading it on first request
    /// and caching the result. BERT defaults assumed: lowercase input, basic
    /// tokenization on, CJK split, no accent stripping. Mirrors the
    /// <c>BertTokenizer</c> behaviour in HuggingFace's <c>transformers</c>
    /// for uncased English checkpoints — the most common case for the
    /// sentence-transformer family (MiniLM, BGE, GTE, …).
    /// </summary>
    public static BertTokenizer GetBertFromVocab(string vocabPath)
        => _byBertVocab.GetOrAdd(vocabPath, LoadBertFromVocab);

    private static BertTokenizer LoadBertFromVocab(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"BERT vocab file not found at '{path}'.", path);
        }
        BertOptions options = new()
        {
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true,
            IndividuallyTokenizeCjk = true,
        };
        return BertTokenizer.Create(path, options);
    }

    /// <summary>
    /// Returns the tokenizer for <paramref name="tokenizerJsonPath"/>, loading
    /// it on first request and caching the result. Throws when the file
    /// describes an unsupported tokenizer algorithm or when the file is
    /// missing / unreadable.
    /// </summary>
    public static BpeTokenizer GetFromTokenizerJson(string tokenizerJsonPath)
        => _byTokenizerJson.GetOrAdd(tokenizerJsonPath, LoadFromTokenizerJson);

    /// <summary>
    /// Returns the tokenizer for <c>(vocabJsonPath, mergesPath)</c>, loading
    /// it on first request and caching the result. Throws when either file
    /// is missing.
    /// </summary>
    public static BpeTokenizer GetFromVocabMerges(string vocabJsonPath, string mergesPath)
        => _byVocabMerges.GetOrAdd((vocabJsonPath, mergesPath), LoadFromVocabMerges);

    /// <summary>
    /// Reads a HuggingFace <c>tokenizer.json</c>, validates that its
    /// <c>model.type</c> is BPE, extracts the vocab dict + merges list, and
    /// synthesises the two streams <c>BpeTokenizer.Create</c> wants.
    /// Doing the extraction here (rather than asking the user to unpack the
    /// JSON to separate vocab.json / merges.txt files first) is what makes
    /// the SQL surface ergonomic.
    /// </summary>
    private static BpeTokenizer LoadFromTokenizerJson(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"tokenizer.json file not found at '{path}'.", path);
        }

        byte[] bytes = File.ReadAllBytes(path);
        byte[] vocabBytes;
        byte[] mergesBytes;
        using (JsonDocument doc = ParseOrThrow(bytes, path))
        {
            if (!doc.RootElement.TryGetProperty("model", out JsonElement model))
            {
                throw new InvalidOperationException(
                    $"tokenizer.json at '{path}' has no 'model' field. " +
                    "Expected a HuggingFace-format tokenizer.json with model.type / model.vocab / model.merges.");
            }

            string type = model.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()!
                : "<unspecified>";

            if (!string.Equals(type, "BPE", StringComparison.OrdinalIgnoreCase))
            {
                throw new NotSupportedException(
                    $"tokenizer.json at '{path}' declares model.type='{type}'. " +
                    "v1 of tokenizer.encode supports only 'BPE'; Unigram / WordPiece / " +
                    "SentencePiece dispatch will land in a follow-up. " +
                    "For BPE-family models that ship as SentencePiece, re-export via " +
                    "transformers' BPE converter or use tokenizer.encode_bpe(text, vocab_path, merges_path).");
            }

            if (!model.TryGetProperty("vocab", out JsonElement vocab) || vocab.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidOperationException(
                    $"tokenizer.json at '{path}' has model.type=BPE but no model.vocab object.");
            }

            if (!model.TryGetProperty("merges", out JsonElement merges) || merges.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException(
                    $"tokenizer.json at '{path}' has model.type=BPE but no model.merges array.");
            }

            // Synthesise the two streams BpeTokenizer.Create wants. vocab.json
            // layout matches tokenizer.json's model.vocab dict exactly — re-
            // serialise the JsonElement subtree as the vocab payload.
            using (MemoryStream ms = new())
            {
                using (Utf8JsonWriter writer = new(ms))
                {
                    vocab.WriteTo(writer);
                }
                vocabBytes = ms.ToArray();
            }

            // merges.txt is one pair per line, space-separated. tokenizer.json
            // carries each merge either as a single string ("a b") or a
            // 2-element string array (["a", "b"]) depending on producer
            // version; handle both.
            StringBuilder mergesBuilder = new();
            foreach (JsonElement merge in merges.EnumerateArray())
            {
                if (merge.ValueKind == JsonValueKind.String)
                {
                    mergesBuilder.Append(merge.GetString()).Append('\n');
                }
                else if (merge.ValueKind == JsonValueKind.Array && merge.GetArrayLength() == 2)
                {
                    mergesBuilder.Append(merge[0].GetString())
                        .Append(' ')
                        .Append(merge[1].GetString())
                        .Append('\n');
                }
                else
                {
                    throw new InvalidOperationException(
                        $"tokenizer.json at '{path}' has a merge entry that is neither a string " +
                        "(\"a b\") nor a 2-element string array — cannot interpret.");
                }
            }
            mergesBytes = Encoding.UTF8.GetBytes(mergesBuilder.ToString());
        }

        using MemoryStream vocabStream = new(vocabBytes, writable: false);
        using MemoryStream mergesStream = new(mergesBytes, writable: false);
        return BpeTokenizer.Create(vocabStream, mergesStream);
    }

    private static JsonDocument ParseOrThrow(byte[] bytes, string path)
    {
        try
        {
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"tokenizer.json at '{path}' is not valid JSON: {ex.Message}", ex);
        }
    }

    private static BpeTokenizer LoadFromVocabMerges((string Vocab, string Merges) key)
    {
        if (!File.Exists(key.Vocab))
        {
            throw new FileNotFoundException(
                $"vocab.json file not found at '{key.Vocab}'.", key.Vocab);
        }
        if (!File.Exists(key.Merges))
        {
            throw new FileNotFoundException(
                $"merges.txt file not found at '{key.Merges}'.", key.Merges);
        }
        using FileStream vocabStream = File.OpenRead(key.Vocab);
        using FileStream mergesStream = File.OpenRead(key.Merges);
        return BpeTokenizer.Create(vocabStream, mergesStream);
    }
}
