# BGE Small (English)

Sentence embeddings from the Beijing Academy of Artificial Intelligence
(BAAI). A small BERT-family encoder (same MiniLM-class footprint — ~33M
params, 384 hidden units) fine-tuned with contrastive + retrieval-flavoured
objectives that consistently top MiniLM on the MTEB English leaderboard.
~130 MB on disk, CPU-friendly, and the reach-for "MiniLM but a bit
better" upgrade when retrieval accuracy matters more than the marginal
download size.

One SQL-visible model: `bge_small_en_v1_5`. Takes a `String`, returns a
length-384 L2-normalised `Float32[]` — identical shape to MiniLM, so
swapping the two is a one-line change to the model name in any query.
Because every vector lies on the unit sphere, `dot_product` and
`cosine_similarity` produce identical scores; `dot_product` is the
faster of the two.

## Example SQL

Embed a sentence:

```sql
SELECT models.bge_small_en_v1_5('a quick brown fox jumps over the lazy dog') AS embedding;
```

Compare similarity between two questions against the duplicate-question
gold labels:

```sql
SELECT
    LET q1 = models.bge_small_en_v1_5(question1),
    LET q2 = models.bge_small_en_v1_5(question2),
    dot_product(q1, q2) similarity,
    is_duplicate,
    question1,
    question2
FROM datasets.quora_question_pairs
LIMIT 10;
```

## Output shape

`Float32[]` — length 384, L2-normalised.

## Tips

- **Drop-in upgrade for MiniLM.** Same 384-d output, same retrieval
  workflow, materially better MTEB numbers — flip the model name and
  re-embed if your corpus already lives in MiniLM space.
- **English only.** BGE Small is monolingual; for cross-lingual work
  reach for the multilingual `bge-m3` or a multilingual sibling.
- **Context is 512 tokens** (WordPiece). Long documents need chunking
  before embedding; embed the chunks and aggregate at query time.
- **Embed once, compare many.** Store embeddings as a `Float32[]`
  column and rank in SQL — re-embedding per query is dramatically
  slower.

## License & attribution

MIT. Original model by BAAI (Beijing Academy of Artificial Intelligence).
ONNX export ships in the upstream HF repo.

- Paper: [C-Pack: Packed Resources For General Chinese Embeddings](https://arxiv.org/abs/2309.07597)
- Upstream: [BAAI/bge-small-en-v1.5](https://huggingface.co/BAAI/bge-small-en-v1.5)
