---
license: apache-2.0
pretty_name: DocBank (sampled) — document pages with token-level OCR ground truth
task_categories:
  - image-to-text
  - object-detection
language:
  - en
size_categories:
  - 1K<n<10K
tags:
  - docbank
  - ocr
  - document-ai
  - document-layout
  - layout-analysis
  - text-recognition
  - arxiv
source_datasets:
  - doc-analysis/DocBank
---

# DocBank (sampled) — document pages + exact OCR ground truth

A sampled, reshaped mirror of [DocBank](https://github.com/doc-analysis/DocBank) (Li et al., COLING 2020), packaged for one-row-per-page ingestion. The upstream release is 54 GB with page images split across a **ten-part** archive (`DocBank_500K_ori_img.zip.001`…`.010`) and annotations as a separate 3 GB zip of per-page `.txt` files. This mirror carries a small cut — **5,000 train + 1,000 test pages** — with images repackaged into a single zip per split and the token annotations reshaped into gzipped JSONL, so a pipeline can fetch one split and join image ↔ annotation without reassembling a multi-part archive or parsing thousands of loose text files.

Re-hosted under Heliosoph for ingestion-pipeline stability; the page pixels and token ground truth are DocBank's, unchanged in content. Pin a revision for reproducible fetches.

Credit: Minghao Li, Yiheng Xu, Lei Cui, Shaohan Huang, Furu Wei, Zhoujun Li, Ming Zhou (DocBank). Source documents are LaTeX submissions on arXiv.org.

## Why a mirror?

Three frictions make raw DocBank awkward to ingest:

1. **Ten-part split zip.** The images ship as `*.zip.001` … `*.zip.010`; you must download all ~54 GB and reassemble before a single page is readable.
2. **Loose per-page text files.** Annotations are one `.txt` per page (tab-separated tokens) inside a 3 GB zip — millions of tiny files.
3. **Scale.** 500K pages is far more than a test corpus needs.

This mirror resolves all three: a bounded sample, images as one zip per split, and annotations pre-joined into JSONL (one page per line). The token text and boxes are byte-faithful to DocBank's `.txt` ground truth.

## What this repo contains

```
docbank-train-images.zip   # 5,000 page images (PNG/JPG), zip entry name == page_id + ext
docbank-train.jsonl.gz     # 5,000 lines, one JSON object per page
docbank-test-images.zip    # 1,000 page images
docbank-test.jsonl.gz      # 1,000 lines
```

### Annotation JSONL — one page per line

```json
{
  "page_id": "2004.xxxxx.tar_2004.01234.gz_paper_3_ori",
  "image_file": "2004.xxxxx.tar_2004.01234.gz_paper_3_ori.jpg",
  "width": 612,
  "height": 792,
  "text": "Full-page reading-order text, tokens space-joined ...",
  "tokens": [
    {"text": "Attention", "x0": 120, "y0": 88, "x1": 210, "y1": 104, "label": "title", "font": "NimbusRomNo9L-Medi"}
  ]
}
```

- `image_file` is the **exact** entry name inside the images zip — join on it directly, no filename parsing.
- `text` is every token joined by spaces in reading order — ready for an OCR-vs-truth diff.
- `tokens[]` preserves DocBank's per-token ground truth: `text`, the bounding box `(x0,y0)`–`(x1,y1)` **normalized to 0–1000**, one of twelve semantic `label`s, and the `font`.

### The twelve semantic labels

`title`, `author`, `abstract`, `paragraph`, `section`, `list`, `caption`, `equation`, `figure`, `table`, `reference`, `footer`.

## ⚠️ Ground truth is machine-perfect, not human

DocBank's labels come from the LaTeX **source**, not from reading the pixels. The transcription is exact by construction — but it reflects source tokenization (ligatures, hyphenation, math markup) rather than what a pixel-level OCR model would emit. Evaluate with a fuzzy / normalized-edit-distance metric, not exact string match. Bounding boxes are in a 0–1000 normalized space; multiply by `width/1000` and `height/1000` to project onto the image.

## How to use

```python
import gzip, json, zipfile, io
from PIL import Image

# annotations
with gzip.open("docbank-test.jsonl.gz", "rt", encoding="utf-8") as f:
    pages = [json.loads(line) for line in f]
print(len(pages), pages[0]["text"][:200])

# images — join by image_file
with zipfile.ZipFile("docbank-test-images.zip") as z:
    img = Image.open(io.BytesIO(z.read(pages[0]["image_file"])))
    print(img.size, "vs", (pages[0]["width"], pages[0]["height"]))
```

## Dataset specs

| | Spec |
|---|---|
| Pages | 5,000 train / 1,000 test (sampled from DocBank's official splits) |
| Row granularity | one page (image + full text + token array) |
| Bounding boxes | per token, normalized to 0–1000 |
| Semantic labels | 12 (title / author / abstract / paragraph / section / list / caption / equation / figure / table / reference / footer) |
| Domain | academic papers (arXiv), English, two-column + equations |
| Format | images as zip per split; annotations as gzipped JSONL |
| Ground truth | machine-exact (from LaTeX source) — use fuzzy scoring |

## When to pick DocBank

- **Document OCR eval**: run a recognizer over the page image, diff against the exact `text`. Real printed pages, not synthetic, not scene text.
- **Layout / detection**: token boxes with semantic labels support detection and reading-order experiments.
- **Document-AI prototyping**: per-token text + box + role is the shape LayoutLM-style models consume.

For photographed text in the wild use **TextOCR** / **HierText**; for receipts, **CORD**; for isolated handwritten characters, **EMNIST**.

## License

**Apache-2.0**, as released by the DocBank authors. Permits commercial use, modification, and redistribution with attribution. The underlying documents are arXiv submissions used under their distribution terms; cite the DocBank paper and arXiv.

- Paper: [DocBank: A Benchmark Dataset for Document Layout Analysis](https://arxiv.org/abs/2006.01038)
- Upstream: [doc-analysis/DocBank](https://github.com/doc-analysis/DocBank) · [liminghao1630/DocBank](https://huggingface.co/datasets/liminghao1630/DocBank)
