---
license: cc0-1.0
pretty_name: Jigsaw Toxic Comment Classification (2017–2018 Kaggle release)
task_categories:
  - text-classification
language:
  - en
size_categories:
  - 100K<n<1M
tags:
  - toxicity
  - content-moderation
  - multi-label
  - classification
  - jigsaw
  - wikipedia
  - kaggle
source_datasets:
  - original
---

# Jigsaw Toxic Comments — canonical 2017–2018 Kaggle release

A verbatim mirror of the [Jigsaw Toxic Comment Classification Challenge](https://www.kaggle.com/competitions/jigsaw-toxic-comment-classification-challenge) training and test data, repackaged as three gzip-compressed CSVs. No rows added, removed, or reordered relative to the upstream Kaggle release — only the hosting moved and each file was gzipped.

Re-hosted under Heliosoph for ingestion-pipeline stability — the upstream files live behind Kaggle's competition-rules click-through and require an authenticated API token to download. Pinning a HuggingFace revision lets pipelines fetch the same bytes anonymously and reproducibly.

Credit: Jigsaw (Google) — comment text drawn from Wikipedia talk-page revision history.

## Why a mirror?

The Kaggle competition page gates downloads behind two layers — a Kaggle account + a one-time "I accept the competition rules" click — and requires the Kaggle CLI to script. Neither layer is hostile, but neither plays nicely with a reproducible ingestion pipeline that just wants the bytes. Pinning a HuggingFace revision gives ingestion pipelines a stable, reproducible handle: the same SHA fetches the same bytes forever, with no auth and no click-through.

The decompressed contents match Kaggle's `train.csv`, `test.csv`, and `test_labels.csv` line-for-line, byte-for-byte.

## What this repo contains

```
train.csv.gz        # 159,571 rows, ~25 MB compressed, UTF-8, CSV with header
test.csv.gz         # 153,164 rows, ~22 MB compressed, UTF-8, CSV with header
test_labels.csv.gz  #     "       , ~1261 KB compressed, UTF-8, CSV with header
```

Three files. `train.csv` carries comment text + gold labels in one row; the held-out test set splits the comment text (`test.csv`) from the gold labels (`test_labels.csv`, released after the competition closed) — join on `id` to recombine.

### `train.csv` — eight columns, header row included

| Column | Type | Meaning |
|---|---|---|
| `id` | string | 16-char hex Kaggle row id. Stable join key. |
| `comment_text` | string | Wikipedia talk-page comment, original casing + punctuation. |
| `toxic` | int (0/1) | 1 if any annotator flagged the comment as generally toxic. |
| `severe_toxic` | int (0/1) | 1 for hateful / aggressive / very-likely-to-make-someone-leave-the-conversation toxicity. |
| `obscene` | int (0/1) | 1 if the comment contains obscene language. |
| `threat` | int (0/1) | 1 if the comment contains a threat. |
| `insult` | int (0/1) | 1 if the comment is an insult directed at a person. |
| `identity_hate` | int (0/1) | 1 if the comment targets identity (race, religion, gender, orientation, …). |

Labels are independent: a comment can carry any combination of flags, including none (about 90% of training rows are unflagged on every axis).

### `test.csv` — two columns, header row included

| Column | Type | Meaning |
|---|---|---|
| `id` | string | 16-char hex row id; joins against `test_labels.csv`. |
| `comment_text` | string | Held-out comment text. |

### `test_labels.csv` — seven columns, header row included

Same six label columns as `train.csv`, plus the `id` column for joining. **Roughly half the rows carry `-1` in every label column** — those are the rows Kaggle held out from private-leaderboard scoring and never released gold labels for. The conventional evaluation drops them:

```sql
SELECT t.id, t.comment_text, l.toxic, l.severe_toxic, l.obscene, l.threat, l.insult, l.identity_hate
FROM test t JOIN test_labels l ON t.id = l.id
WHERE l.toxic >= 0
```

That yields 63,978 scoring rows out of the 153,164 raw test rows.

## How to use

Load with the `datasets` library:

```python
from datasets import load_dataset

train = load_dataset("Heliosoph/Jigsaw-Toxic-Comments", data_files="train.csv.gz", split="train")
print(train[0])
# {'id': '0000997932d777bf',
#  'comment_text': "Explanation\nWhy the edits made under my username...",
#  'toxic': 0, 'severe_toxic': 0, 'obscene': 0,
#  'threat': 0, 'insult': 0, 'identity_hate': 0}
```

Or read the CSVs directly with pandas — the `test` and `test_labels` files want a join + filter:

```python
import pandas as pd

train = pd.read_csv("train.csv.gz", compression="gzip")
print(train.shape)                          # (159571, 8)
print(train[["toxic","severe_toxic","obscene","threat","insult","identity_hate"]].sum())

test_text   = pd.read_csv("test.csv.gz",        compression="gzip")
test_labels = pd.read_csv("test_labels.csv.gz", compression="gzip")
test = test_text.merge(test_labels, on="id")
test = test[test["toxic"] >= 0]             # drop the -1 (excluded-from-scoring) rows
print(test.shape)                           # (63978, 8)
```

## Dataset specs

| | Spec |
|---|---|
| Train rows | 159,571 |
| Test rows (raw) | 153,164 |
| Test rows (scored, after filtering `-1`) | 63,978 |
| Label columns | 6 binary flags (toxic, severe_toxic, obscene, threat, insult, identity_hate) |
| Label cardinality | ~10% of train rows carry at least one flag |
| Encoding | UTF-8 |
| Format | CSV with header, RFC-4180-style quoting |
| Compressed size | ~48 MB total across the three files |
| Uncompressed size | ~131 MB total |
| Average comment length | ~395 characters / ~70 words |
| Max comment length | ~5,000 characters — exercises the tail of most encoders' context windows |
| Language | English |
| Domain | Wikipedia talk-page discussion |

## When to pick Jigsaw Toxic Comments

- **Multi-label text classification**: each comment can carry any combination of six independent flags. A genuine multi-label problem (with strong label correlations) rather than a one-hot multi-class proxy.
- **Embedding-classifier evaluation**: encode comments with a sentence encoder, train a small classifier head per label, report per-label and macro F1 against the 64k scored test rows. Comparable to dozens of published encoder benchmarks.
- **Content-moderation prototyping**: 160k labelled training rows fits comfortably in memory and fine-tunes a small encoder end-to-end on a single GPU in under an hour.
- **Imbalanced-classification practice**: severe_toxic, threat, and identity_hate are all <1% positive in the training set — exercises threshold calibration, focal loss, oversampling, and other long-tail tricks.

For pure sentence-similarity / paraphrase evaluation, reach for **Quora Question Pairs** instead — labelled pair structure, not multi-label per-row classification.

## Label notes

The six labels are independent, not mutually exclusive. They are also noisy: each comment was rated by multiple annotators and the binary flags collapse those ratings into a majority-vote decision, which makes the rare-class labels (`severe_toxic`, `threat`, `identity_hate`) particularly thin and disputed. The community reports macro-F1 in the 0.55–0.75 range for strong models, vs >0.99 ROC-AUC on the dominant `toxic` axis — the headline ROC-AUC numbers from the Kaggle leaderboard look stronger than the per-label classification difficulty actually is.

## Companion competitions (not in this repo)

Three follow-up Jigsaw competitions reuse this same multi-label shape against different corpora:

- [Jigsaw Unintended Bias in Toxicity Classification](https://www.kaggle.com/competitions/jigsaw-unintended-bias-in-toxicity-classification) (2019) — 1.8M Civil Comments with identity-subgroup attributes.
- [Jigsaw Multilingual Toxic Comment Classification](https://www.kaggle.com/competitions/jigsaw-multilingual-toxic-comment-classification) (2020) — train English, test 6 other languages.
- [Jigsaw Rate Severity of Toxic Comments](https://www.kaggle.com/competitions/jigsaw-toxic-severity-rating) (2021) — pairwise severity ranking.

This mirror covers only the original 2017–2018 release.

## License

**CC0 1.0 Universal** — released by Jigsaw under the public-domain dedication on the original Kaggle competition page. Permits any use — research, commercial, redistribution, derivative works — with no attribution requirement, though attribution to Jigsaw and to Wikipedia remains good practice for traceability.

- Kaggle competition: [Jigsaw Toxic Comment Classification Challenge](https://www.kaggle.com/competitions/jigsaw-toxic-comment-classification-challenge)
- Jigsaw: [jigsaw.google.com](https://jigsaw.google.com/)
- Underlying comments: Wikipedia talk-page revision history
