---
license: cc-by-sa-4.0
pretty_name: NFCorpus (corpus.jsonl re-encoding)
task_categories:
  - sentence-similarity
  - text-retrieval
language:
  - en
size_categories:
  - 1K<n<10K
tags:
  - retrieval
  - passage-ranking
  - embeddings
  - dense-retrieval
  - medical
  - nutrition
  - beir
  - nfcorpus
source_datasets:
  - original
---

# NFCorpus — `corpus.jsonl` re-encoding

A re-encoding of the [NFCorpus](https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/) medical-IR corpus by Boteva et al. (Heidelberg StatNLP, ECIR 2016), distilled into a single gzip-compressed JSON-Lines file. The 3,633 rows and the four-key per-line schema (`_id`, `title`, `text`, `metadata`) match the [BEIR-reformatted](https://public.ukp.informatik.tu-darmstadt.de/thakur/BEIR/datasets/nfcorpus.zip) `corpus.jsonl` byte-for-byte — only the outer container differs.

Re-hosted under Heliosoph for ingestion-pipeline stability — the upstream BEIR archive (`nfcorpus.zip`) wraps `corpus.jsonl` alongside queries and qrels in a single zip, and the original Heidelberg release splits docs across three per-split TSVs with overlap. Dropping the zip wrapper down to a plain `corpus.jsonl.gz` lets a streaming JSON-Lines reader work directly against the file with no intermediate extraction step.

Credit: Vera Boteva, Demian Gholipour, Artem Sokolov, Stefan Riezler (Heidelberg StatNLP, ECIR 2016) — corpus extracted from [NutritionFacts.org](https://nutritionfacts.org/) under CC BY-SA 4.0. BEIR-reformatted distribution by Nandan Thakur et al. (UKP, TU Darmstadt, NeurIPS 2021).

## Why a mirror?

The two upstream NFCorpus distributions both add work for an ingestion pipeline that just wants the passages:

- **Heidelberg's `nfcorpus.tar.gz`** splits documents across `train.docs`, `dev.docs`, and `test.docs` with overlap — recovering the canonical 3,633 unique passages requires a union+dedup step on the doc-id column.
- **BEIR's `nfcorpus.zip`** wraps the unified `corpus.jsonl` together with queries and qrels in a zip — pipelines that ingest one corpus file at a time have to either extract the zip first or know which member to seek to.

This mirror strips the zip wrapper down to a single gzipped JSON-Lines file — `corpus.jsonl.gz` — that any streaming JSONL reader can consume directly. The line-level schema is preserved verbatim, so the `_id` field can still be joined against the official BEIR `qrels/test.tsv` for nDCG@10 evaluation without translation.

The decompressed contents match BEIR's `corpus.jsonl` line-for-line, byte-for-byte.

## What this repo contains

```
corpus.jsonl.gz   # 3,633 lines, ~1.9 MB compressed, UTF-8, one JSON object per line
```

One file, one JSON object per non-empty line, four keys per object:

| Key | Type | Meaning |
|---|---|---|
| `_id` | string | BEIR-style passage id (e.g. `"MED-10"`). Stable join key against the official `queries.jsonl` and `qrels/test.tsv`. |
| `title` | string | Article title from NutritionFacts.org, original casing + punctuation. |
| `text` | string | Article body, original casing + punctuation. |
| `metadata` | object | `{"url": "..."}` — the source NutritionFacts.org permalink. |

Lines are not sorted by `_id` in any meaningful order; the order matches BEIR's upstream `corpus.jsonl` exactly.

## How to use

Stream the gzipped JSONL row-by-row with the standard library (zero-memory streaming, ~3,633 dict allocations):

```python
import gzip, json

with gzip.open("corpus.jsonl.gz", "rt", encoding="utf-8") as f:
    for line in f:
        rec = json.loads(line)
        passage_id = rec["_id"]
        title      = rec["title"]
        body       = rec["text"]
        # Most BEIR embedder evaluations concatenate title + body before encoding:
        passage    = f"{title}. {body}"
```

Or load the whole corpus into a pandas frame (fits in <50 MB RAM):

```python
import pandas as pd

df = pd.read_json("corpus.jsonl.gz", lines=True, compression="gzip")
print(df.shape)              # (3633, 4)
print(df.columns.tolist())   # ['_id', 'title', 'text', 'metadata']
print(df["text"].str.len().describe())
```

## Dataset specs

| | Spec |
|---|---|
| Lines | 3,633 passages |
| Distinct passages | 3,633 (`_id` is unique) |
| Encoding | UTF-8 |
| Format | JSON Lines (one JSON object per non-empty line, no enclosing array) |
| Compressed size | ~1.9 MB |
| Uncompressed size | ~5.9 MB |
| Average passage length | ~232 words / ~330 BERT word-piece tokens (title + body concatenated) |
| Max passage length | ~5,000+ tokens — exercises long-context embedders |
| Language | English |
| Domain | Medical / nutrition (NutritionFacts.org articles) |

## When to pick NFCorpus

- **Fast iteration on a retrieval recipe**: 3.6k passages embed end-to-end on CPU in seconds with MiniLM-L6. The right substrate for prototyping a dense-retrieval pipeline before stepping up to MS MARCO Passages (~2,400× larger).
- **Domain-shifted IR evaluation**: medical / nutrition vocabulary is meaningfully out-of-distribution for embedders pretrained on generic web text. A genuine zero-shot test, not a re-run of the training distribution.
- **Long-context embedder showcase**: the passage length tail runs into the thousands of tokens — full articles, not snippets. Embedders with 4K / 8K context (Jina v2, E5 long, BGE long) actually see passages worth more than the first 512 tokens.
- **BEIR / MTEB leaderboard comparison**: NFCorpus is one of the original BEIR zero-shot retrieval benchmarks, so a fresh nDCG@10 number from your recipe is directly comparable to dozens of published embedders.

For the much larger general-domain counterpart, reach for **MS MARCO Passages** — passage-retrieval shape, 2,400× the rows, web-search vocabulary instead of medical.

## Companion splits (not in this repo)

A full NFCorpus / BEIR retrieval evaluation requires two more files from the upstream BEIR `nfcorpus.zip`:

- `queries.jsonl` — 3,237 natural-language health queries (`_id`, `text`).
- `qrels/test.tsv` — graded (query_id, passage_id, relevance) judgments for the test split.

Run a full nDCG@10 / MRR@10 evaluation by pairing this corpus with those two. A follow-up Heliosoph variant may bundle them.

## License

**CC BY-SA 4.0** — the same license under which the underlying NutritionFacts.org articles are published and under which Boteva et al. distribute the corpus. Permits research, commercial use, and redistribution, including derivative works, provided attribution is preserved and derivatives are shared under the same license.

- Project site: [NFCorpus at Heidelberg StatNLP](https://www.cl.uni-heidelberg.de/statnlpgroup/nfcorpus/)
- BEIR reformatted distribution: [BEIR Benchmark](https://github.com/beir-cellar/beir)
- Paper: [NFCorpus — A Full-Text Learning to Rank Dataset for Medical Information Retrieval (ECIR 2016)](https://www.cl.uni-heidelberg.de/statnlpgroup/publications/ECIR2016_boteva.pdf)
- BEIR paper: [BEIR: A Heterogeneous Benchmark for Zero-shot Evaluation of Information Retrieval Models (NeurIPS 2021)](https://arxiv.org/abs/2104.08663)
