using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Heliosoph.DatumV.Functions.Tokenization;

/// <summary>
/// A minimal SentencePiece <em>Unigram</em> tokenizer for the XLM-RoBERTa
/// family (multilingual encoders, rerankers, NLI). Loaded from a HuggingFace
/// <c>tokenizer.json</c> whose <c>model.type</c> is <c>Unigram</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why hand-rolled.</strong> Microsoft.ML.Tokenizers' SentencePiece
/// support (<c>LlamaTokenizer</c> / <c>SentencePieceTokenizer</c>) only reads
/// SentencePiece <em>BPE</em> models — it throws "The model type is not Bpe"
/// on an XLM-R Unigram model, in every released version. So the Unigram
/// best-path decode is implemented directly here.
/// </para>
/// <para>
/// <strong>Ids are final.</strong> Reading the vocabulary from
/// <c>tokenizer.json</c> (rather than the raw <c>sentencepiece.bpe.model</c>)
/// means the piece ids are already in HuggingFace/fairseq space — the special
/// ids <c>&lt;s&gt;=0 / &lt;pad&gt;=1 / &lt;/s&gt;=2 / &lt;unk&gt;=3</c> and the
/// +1 content offset are baked into the array order — so callers emit
/// <see cref="Encode"/>'s output verbatim with no remapping.
/// </para>
/// <para>
/// <strong>Normalization.</strong> Reproduces SentencePiece's default
/// preprocessing as the HuggingFace fast tokenizer applies it: NFKC, a single
/// dummy-prefix space, whitespace-run collapsing (leading runs fold into the
/// dummy prefix; a trailing run survives as a lone <c>▁</c>), then the
/// whitespace-to-<c>▁</c> (U+2581) escape. Validated id-for-id against the
/// <c>tokenizers</c> reference across Latin, CJK, accented (NFKC), full-width,
/// emoji, and whitespace-edge inputs.
/// </para>
/// </remarks>
internal sealed class UnigramTokenizer
{
    private const char Whitespace = '▁'; // ▁ metaspace marker
    private const int UnkFallbackId = 3;      // <unk> in fairseq/XLM-R id space

    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    private readonly Dictionary<string, int> _pieceToId;
    private readonly double[] _scores;
    private readonly int _unkId;
    private readonly int _maxPieceLength;

    private UnigramTokenizer(Dictionary<string, int> pieceToId, double[] scores, int unkId, int maxPieceLength)
    {
        _pieceToId = pieceToId;
        _scores = scores;
        _unkId = unkId;
        _maxPieceLength = maxPieceLength;
    }

    /// <summary>
    /// Parses a HuggingFace <c>tokenizer.json</c> with <c>model.type='Unigram'</c>
    /// and builds the piece→id table, score array, and unknown-token id. Throws
    /// <see cref="NotSupportedException"/> when the model isn't Unigram and
    /// <see cref="InvalidOperationException"/> when required fields are missing.
    /// </summary>
    public static UnigramTokenizer FromTokenizerJson(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"tokenizer.json file not found at '{path}'.", path);
        }

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllBytes(path));
        if (!doc.RootElement.TryGetProperty("model", out JsonElement model))
        {
            throw new InvalidOperationException(
                $"tokenizer.json at '{path}' has no 'model' field.");
        }

        string type = model.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()!
            : "<unspecified>";
        if (!string.Equals(type, "Unigram", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"tokenizer.json at '{path}' declares model.type='{type}'. " +
                "UnigramTokenizer requires a SentencePiece Unigram model (XLM-RoBERTa family).");
        }

        if (!model.TryGetProperty("vocab", out JsonElement vocab) || vocab.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"tokenizer.json at '{path}' has model.type=Unigram but no model.vocab array.");
        }

        Dictionary<string, int> pieceToId = new(StringComparer.Ordinal);
        List<double> scores = [];
        int maxPieceLength = 0;
        int id = 0;
        foreach (JsonElement entry in vocab.EnumerateArray())
        {
            // Each entry is a 2-tuple: [piece (string), score (double)].
            string piece = entry[0].GetString()
                ?? throw new InvalidOperationException(
                    $"tokenizer.json at '{path}' has a Unigram vocab entry with a null piece at id {id}.");
            scores.Add(entry[1].GetDouble());
            // Array order is the id; keep the first binding if a piece repeats.
            pieceToId.TryAdd(piece, id);
            if (piece.Length > maxPieceLength) maxPieceLength = piece.Length;
            id++;
        }

        int unkId = model.TryGetProperty("unk_id", out JsonElement u) && u.ValueKind == JsonValueKind.Number
            ? u.GetInt32()
            : UnkFallbackId;

        return new UnigramTokenizer(pieceToId, scores.ToArray(), unkId, maxPieceLength);
    }

    /// <summary>
    /// Encodes <paramref name="text"/> to its Unigram piece ids (no special
    /// tokens — callers add <c>&lt;s&gt;</c> / <c>&lt;/s&gt;</c>). Returns an
    /// empty list for empty input, matching the fast tokenizer.
    /// </summary>
    public List<int> Encode(string text)
    {
        List<int> ids = [];
        if (text.Length == 0) return ids;

        // SentencePiece preprocessing (as the HF fast tokenizer applies it):
        // NFKC, prepend one dummy-prefix space, collapse whitespace runs WITHOUT
        // trimming (a leading run merges into the dummy prefix; a trailing run
        // survives as a lone ▁), then escape spaces to ▁.
        string norm = text.Normalize(NormalizationForm.FormKC);
        norm = WhitespaceRun.Replace(" " + norm, " ");
        norm = norm.Replace(' ', Whitespace);

        int n = norm.Length;
        double[] best = new double[n + 1];
        int[] back = new int[n + 1];
        int[] pieceAt = new int[n + 1];
        for (int i = 1; i <= n; i++)
        {
            best[i] = double.NegativeInfinity;
            back[i] = -1;
        }

        for (int i = 0; i < n; i++)
        {
            if (double.IsNegativeInfinity(best[i])) continue;

            int limit = Math.Min(_maxPieceLength, n - i);
            for (int len = limit; len >= 1; len--)
            {
                if (_pieceToId.TryGetValue(norm.Substring(i, len), out int pid))
                {
                    double candidate = best[i] + _scores[pid];
                    if (candidate > best[i + len])
                    {
                        best[i + len] = candidate;
                        back[i + len] = i;
                        pieceAt[i + len] = pid;
                    }
                }
            }

            // Unknown fallback: consume one Unicode scalar (a surrogate pair
            // counts as one) as <unk>. A large negative penalty keeps this off
            // the best path unless nothing in the vocab covers the position.
            int unkLen = (i + 1 < n && char.IsHighSurrogate(norm[i]) && char.IsLowSurrogate(norm[i + 1])) ? 2 : 1;
            double unkScore = best[i] - 1e6;
            if (unkScore > best[i + unkLen])
            {
                best[i + unkLen] = unkScore;
                back[i + unkLen] = i;
                pieceAt[i + unkLen] = _unkId;
            }
        }

        for (int cur = n; cur > 0; cur = back[cur])
        {
            ids.Add(pieceAt[cur]);
        }
        ids.Reverse();
        return ids;
    }
}
