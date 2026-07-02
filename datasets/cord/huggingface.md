---
license: cc-by-4.0
pretty_name: CORD (receipts) — OCR + key-information-extraction ground truth
task_categories:
  - image-to-text
  - object-detection
language:
  - en
  - id
size_categories:
  - n<1K
tags:
  - cord
  - ocr
  - receipts
  - document-ai
  - key-information-extraction
  - post-ocr-parsing
source_datasets:
  - naver-clova-ix/cord-v1
---

# CORD (receipts) — reshaped mirror

A reshaped mirror of [naver-clova-ix/cord-v1](https://huggingface.co/datasets/naver-clova-ix/cord-v1) (Park et al., NAVER Clova, 2019), packaged for one-row-per-receipt ingestion. Upstream stores the receipt as a HuggingFace Image feature (a `{bytes, path}` struct) plus a `ground_truth` JSON string; pipelines that can't decode a struct image column (or don't want a full `datasets` dependency) can't consume it directly. This mirror splits each receipt into an image file + a JSONL annotation line, joinable by filename.

Re-hosted under Heliosoph for ingestion-pipeline stability. The annotations are CORD's, unchanged in content; the images are re-encoded (see below). Pin a revision for reproducible fetches.

Credit: Seunghyun Park, Seung Shin, Bado Lee, Junyeop Lee, Jaeheung Surh, Minjoon Seo, Hwalsuk Lee (CORD); NAVER Clova.

## Why a mirror?

1. **Struct image column.** Upstream's `image` is a HuggingFace Image feature — a `{bytes, path}` struct in parquet. Readers without nested-type support can't pull the bytes out.
2. **Heavy PNGs.** The embedded images are full-resolution PNG photos (~2 MB each, ~2 GB for the full set). This mirror re-encodes them to **JPEG q90** — visually lossless for OCR, ~6× smaller (~210 MB total).

The `ground_truth` annotations are copied verbatim (parsed from the upstream JSON string into a nested object).

## What this repo contains

```
cord-train-images.zip        # 800 receipt JPEGs (entry name == image_file)
cord-train.jsonl.gz          # 800 lines, one JSON object per receipt
cord-validation-images.zip   # 100 receipts
cord-validation.jsonl.gz     # 100 lines
cord-test-images.zip         # 100 receipts
cord-test.jsonl.gz           # 100 lines
```

### Annotation JSONL — one receipt per line

```json
{
  "image_file": "cord-test-0000.jpg",
  "width": 432,
  "height": 648,
  "ground_truth": {
    "gt_parse":   { "menu": { "nm": "-TICKET CP", "cnt": "2", "price": "60.000" },
                    "sub_total": { "subtotal_price": "60.000", "tax_price": "5.455" },
                    "total": { "total_price": "60.000" } },
    "valid_line": [ { "words": [ { "text": "...", "quad": { "x1": 0, "y1": 0, ... } } ],
                      "category": "menu.nm" } ],
    "meta":       { "image_size": { "width": 432, "height": 648 } },
    "roi":        { "x1": 0, "y1": 0, ... }
  }
}
```

- `image_file` is the **exact** entry name inside the images zip — join on it directly, no filename parsing.
- **`valid_line`** is the word-level OCR ground truth: each word's `text`, quadrilateral box, and semantic `category`.
- **`gt_parse`** is the structured key-information-extraction target: menu lines, sub-totals, totals.

## How to use

```python
import gzip, json, zipfile, io
from PIL import Image

with gzip.open("cord-test.jsonl.gz", "rt", encoding="utf-8") as f:
    receipts = [json.loads(line) for line in f]

with zipfile.ZipFile("cord-test-images.zip") as z:
    r = receipts[0]
    img = Image.open(io.BytesIO(z.read(r["image_file"])))
    print(img.size, r["ground_truth"]["gt_parse"])
```

## Dataset specs

| | Spec |
|---|---|
| Receipts | 800 train / 100 validation / 100 test |
| Row granularity | one receipt (image + width/height + full ground_truth) |
| OCR ground truth | word-level: text + quad box + category (`valid_line`) |
| Parse ground truth | structured menu / sub_total / total (`gt_parse`) |
| Images | JPEG q90 (re-encoded from upstream full-res PNG) |
| Domain | Indonesian restaurant receipts (photographed) |
| Format | images as zip per split; annotations as gzipped JSONL |

## When to pick CORD

- **Receipt OCR**: detection + recognition on real photographed receipts (skew, glare, thermal paper).
- **Key-information extraction / post-OCR parsing**: turn a receipt image into structured menu/total fields — the task CORD was built for.
- **Document-AI prototyping**: word text + box + category is the shape LayoutLM / Donut-style models consume.

For dense printed pages use **DocBank**; for scene text in the wild, **TextOCR** / **HierText**.

## License

**CC BY 4.0**, as released for CORD. Permits commercial use, modification, and redistribution with attribution to NAVER Clova and the CORD authors.

- Paper: [CORD: A Consolidated Receipt Dataset for Post-OCR Parsing](https://openreview.net/forum?id=SJl3z659UH)
- Upstream: [naver-clova-ix/cord-v1](https://huggingface.co/datasets/naver-clova-ix/cord-v1)
