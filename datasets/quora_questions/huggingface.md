---
license: other
license_name: quora-qp-release-terms
license_link: https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs
pretty_name: Quora Question Pairs (2017 canonical release)
task_categories:
  - sentence-similarity
  - text-classification
language:
  - en
size_categories:
  - 100K<n<1M
tags:
  - embeddings
  - sentence-similarity
  - paraphrase
  - duplicate-questions
  - quora
  - qqp
source_datasets:
  - original
---

# Quora Question Pairs — canonical 2017 release

A verbatim mirror of [Quora's January 2017 Question Pairs release](https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs), packaged as a single tab-delimited file. No rows added, removed, or reordered relative to the upstream `quora_duplicate_questions.tsv` — only the hosting moved.

Re-hosted under Heliosoph for ingestion-pipeline stability — Quora's original CDN at `qim.fs.quoracdn.net` has been intermittently unreachable since the Kaggle competition wrapped, and the file has no checksumed permanent URL upstream.

Credit: Shankar Iyer, Nikhil Dandekar, and Kornél Csernai (Quora, 2017) — questions authored by Quora users.

## Why a mirror?

The original release URL (`qim.fs.quoracdn.net/quora_duplicate_questions.tsv`) is the single canonical source, but it has gone offline for weeks at a time in past years, and there's no published checksum to verify a recovered copy against. Pinning a HuggingFace revision gives ingestion pipelines a stable, reproducible handle: the same SHA fetches the same bytes forever.

The file itself is unmodified — md5 / sha256 against the upstream copy (when it's online) will match exactly.

## What this repo contains

```
quora_duplicate_questions.tsv     # 404,290 rows, ~55 MB, UTF-8, tab-delimited
```

One file, six columns, header row included:

| Column | Type | Meaning |
|---|---|---|
| `id` | int | Row index (0-based). |
| `qid1` | int | Stable id of `question1` in Quora's internal question table. |
| `qid2` | int | Stable id of `question2`. |
| `question1` | string | First question, original casing + punctuation. |
| `question2` | string | Second question, original casing + punctuation. |
| `is_duplicate` | int (0/1) | Human label: 1 if the pair asks the same thing, else 0. |

The `qid1` / `qid2` columns let you build a question-level view by deduping — the corpus is ~537k distinct questions arranged into 404k pairs.

## How to use

Load with the `datasets` library:

```python
from datasets import load_dataset

ds = load_dataset("Heliosoph/Quora-Question-Pairs", split="train")
print(ds[0])
# {'id': 0, 'qid1': 1, 'qid2': 2,
#  'question1': 'What is the step by step guide to invest in share market in india?',
#  'question2': 'What is the step by step guide to invest in share market?',
#  'is_duplicate': 0}
```

Or read the TSV directly with pandas:

```python
import pandas as pd

df = pd.read_csv("quora_duplicate_questions.tsv", sep="\t")
print(df.shape)  # (404290, 6)
print(df["is_duplicate"].mean())  # ~0.369 — about 37% of pairs are duplicates
```

## Dataset specs

| | Spec |
|---|---|
| Rows | 404,290 question pairs |
| Distinct questions | ~537,000 |
| Duplicate share | ~36.9% labelled `is_duplicate = 1` |
| Encoding | UTF-8 |
| Delimiter | Tab (`\t`) |
| Quoting | Minimal — embedded tabs in question text are rare but present; standard CSV/TSV readers handle them with double-quote escaping |
| File size | ~55 MB on disk |
| Language | English |

## When to pick Quora Question Pairs

- **Sentence-embedding evaluation**: encode `question1` + `question2`, take cosine similarity of the L2-normalised vectors, threshold for a binary classifier, report accuracy / F1 against `is_duplicate`. This is the canonical paired-cosine evaluation that every English sentence embedder (MiniLM, BGE, Jina, E5, GTE) reports.
- **Duplicate-detection training**: 404k labelled pairs is enough to fine-tune a small encoder end-to-end on a single GPU in hours.
- **Paraphrase identification**: the label space (same question or not) is a clean two-class proxy for paraphrase / non-paraphrase.

For larger-scale retrieval (millions of passages) reach for **MS MARCO** instead — Quora QP is purpose-built for pairwise similarity, not document retrieval.

## Label notes

The `is_duplicate` flag is human-annotated but not gold-standard: Quora's release post explicitly notes that some labels are noisy ("the ground-truth labels contain some amount of noise: they are not guaranteed to be perfect"). For benchmark numbers, report on the full corpus rather than a hand-cleaned subset to keep results comparable.

## License

**Non-standard research-use terms** from Quora's 2017 release. Allows research, experimentation, and unmodified redistribution with attribution back to Quora. Commercial deployment is not explicitly addressed in the original announcement and should be reviewed separately.

The HuggingFace metadata tags this `other` because no SPDX identifier matches; the canonical terms are the [release announcement](https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs).

- Release announcement: [First Quora Dataset Release: Question Pairs](https://quoradata.quora.com/First-Quora-Dataset-Release-Question-Pairs)
- Kaggle competition (same data): [Quora Question Pairs](https://www.kaggle.com/competitions/quora-question-pairs)
