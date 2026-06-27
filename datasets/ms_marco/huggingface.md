---
license: other
license_name: ms-marco-terms
license_link: https://microsoft.github.io/msmarco/
pretty_name: MS MARCO Passages (collection.tsv re-encoding)
task_categories:
  - sentence-similarity
  - text-retrieval
language:
  - en
size_categories:
  - 1M<n<10M
tags:
  - retrieval
  - passage-ranking
  - embeddings
  - dense-retrieval
  - bing
  - ms-marco
source_datasets:
  - original
---

# MS MARCO Passages — `collection.tsv` re-encoding

A verbatim mirror of Microsoft's [MS MARCO Passage Ranking](https://microsoft.github.io/msmarco/Datasets#passage-ranking-dataset) corpus, packaged as a single gzip-compressed tab-delimited file. The 8,841,823 rows, the integer `passage_id` column, and the passage `text` column match the upstream `collection.tsv` byte-for-byte — only the outer compression wrapper differs.

Re-hosted under Heliosoph for ingestion-pipeline stability — Microsoft's published archive (`collection.tar.gz` on `msmarco.blob.core.windows.net`) wraps the same data in a one-file tar, which forces ingestion pipelines to do tar extraction before they can read the TSV. Dropping the tar wrapper lets a streaming gzip CSV reader work directly against the file.

Credit: Bajaj, Campos, Craswell, Deng, Gao, Liu, Majumder, McNamara, Mitra, Nguyen, Rosenberg, Song, Stoica, Tiwary, Wang (Microsoft, 2016) — passages extracted from real Bing query results.

## Why a mirror?

The MS MARCO upstream archive is shipped as `collection.tar.gz` — a tarball containing one file (`collection.tsv`). For tools that materialize the archive end-to-end before reading (most database ingestion pipelines do this), the tar wrapper buys nothing and forces an extra extraction step. This mirror strips the tar wrapper so the file is just `collection.tsv.gz` — a plain gzipped TSV that any streaming CSV reader can consume directly.

The TSV content is identical: same 8.8M rows, same passage_id ordering, same text. md5 / sha256 of the *decompressed* `collection.tsv` will match the upstream archive's inner file exactly.

The tradeoff is size: gzip compresses similarly to the inner gzip Microsoft uses, so this mirror is roughly the same on-disk size as the upstream — **~948 MB compressed, ~2.85 GB uncompressed**.

## What this repo contains

```
collection.tsv.gz     # 8,841,823 rows, ~948 MB compressed, UTF-8, tab-delimited, no header
```

One file, two columns, **no header row**:

| Column | Type | Meaning |
|---|---|---|
| 0 | int | `passage_id` — stable id assigned by Microsoft, dense 0..8841822 with gaps from the source extraction. |
| 1 | string | `text` — passage body, original casing + punctuation. |

The passage_id is the join key used by the official MS MARCO `qrels` (relevance judgments) and `top1000` (BM25 baseline candidate lists). If you intend to evaluate against the standard dev / TREC DL splits, keep the id intact end-to-end.

## How to use

Stream the gzipped TSV directly with pandas:

```python
import pandas as pd

df = pd.read_csv(
    "collection.tsv.gz",
    sep="\t",
    header=None,
    names=["passage_id", "text"],
    dtype={"passage_id": "int32", "text": "string"},
    compression="gzip",
)
print(df.shape)  # (8841823, 2)
print(df["text"].str.len().describe())
```

Or row-by-row with the standard library (no full-corpus memory pressure):

```python
import csv, gzip

with gzip.open("collection.tsv.gz", "rt", encoding="utf-8", newline="") as f:
    for pid, text in csv.reader(f, delimiter="\t"):
        passage_id = int(pid)
        # passage_id: 0..8841822 (with gaps)
        # text: passage body
        ...
```

## Dataset specs

| | Spec |
|---|---|
| Rows | 8,841,823 passages |
| Distinct passages | 8,841,823 (passage_id is unique) |
| Encoding | UTF-8 |
| Delimiter | Tab (`\t`) |
| Header | None — first row is data |
| Quoting | Minimal — embedded tabs in passage text are rare but present in a handful of rows; standard CSV/TSV readers handle them with double-quote escaping |
| Compressed size | ~948 MB |
| Uncompressed size | ~2.85 GB |
| Average passage length | ~55 word-piece tokens (BERT) |
| Max passage length | ~300+ tokens — exercises long-context embedders |
| Language | English |

## When to pick MS MARCO Passages

- **Dense-retrieval evaluation**: encode every passage once, encode the query, rank by cosine similarity — the canonical workload for any modern English text embedder. MTEB / BEIR report against the MS MARCO dev split.
- **Long-context embedder showcase**: the passage length distribution has a real tail; embedders with 4K / 8K context (Jina v2, E5 long, etc.) actually see passages worth more than the first 512 tokens.
- **Reranker training / evaluation**: pair with the official qrels + top1000 splits (shipped separately) for cross-encoder evaluation.
- **Substrate for semantic-search demos**: an 8.8M-row real-world web corpus is a more honest backdrop for "search over a real corpus" than a hand-curated 10k-row toy.

For pairwise question-similarity work, reach for **Quora Question Pairs** instead — MS MARCO is purpose-built for one-to-many retrieval, not pairwise similarity scoring.

## Companion splits (not in this repo)

The full MS MARCO Passage Ranking task requires three more files, shipped separately by Microsoft:

- `queries.dev.small.tsv` — ~6,980 dev queries (`qid <TAB> query_text`).
- `qrels.dev.small.tsv` — ~7,437 (qid, passage_id, relevance) judgments.
- `top1000.dev.tsv` — BM25 baseline candidate lists per dev query.

Run a full MRR@10 / nDCG@10 evaluation by pairing this corpus with those three. A follow-up Heliosoph variant may bundle them.

## License

**Non-standard research-use terms** from Microsoft's MS MARCO release. Allows research, experimentation, benchmarking, and unmodified redistribution with attribution back to Microsoft. Commercial deployment is explicitly outside the scope of the release terms and should be reviewed separately.

The HuggingFace metadata tags this `other` because no SPDX identifier matches; the canonical terms are linked from the [project site](https://microsoft.github.io/msmarco/).

- Project site: [microsoft.github.io/msmarco](https://microsoft.github.io/msmarco/)
- Task overview: [Passage Ranking Dataset](https://microsoft.github.io/msmarco/Datasets#passage-ranking-dataset)
- Paper: [MS MARCO: A Human Generated Machine Reading Comprehension Dataset](https://arxiv.org/abs/1611.09268)
