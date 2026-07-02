# CORD

The **Co**nsolidated **R**eceipt **D**ataset — 1,000 photographed
restaurant receipts with unusually rich ground truth. Park et al.
(NAVER Clova, 2019) annotated each receipt two ways at once:

- **`valid_line`** — word-level OCR: every word's transcription, its
  quadrilateral box, and a semantic category. The detection +
  recognition view.
- **`gt_parse`** — the receipt distilled into a structured object:
  menu items (name / count / price), sub-totals, and totals. The
  post-OCR **key-information-extraction** view CORD was built to
  benchmark.

So CORD works for pure OCR *and* for the harder "turn a receipt photo
into structured data" task. Unlike **SROIE** (the other well-known
receipt set, whose licensing is ambiguous), CORD is cleanly CC-BY-4.0.

## When to use which split

| Variant | Receipts | Best for |
| ------- | --------:| -------- |
| **cord_test** | **100** | **Default** for examples — ingests in seconds. The standard eval slice. |
| cord_validation | 100 | Tuning before you report on test. |
| cord_train | 800 | The bulk — fine-tune a small OCR / KIE model. |

Receipts are real photos (skew, glare, thermal-paper fading, varied
resolution), so this is a meaningfully harder OCR target than clean
document renders like DocBank.

## Example SQL

Look at one receipt — image, dimensions, and its ground-truth bundle:

```sql
SELECT receipt_id, width, height, ground_truth, img
FROM datasets.cord_test
LIMIT 1;
```

Run an OCR model over the receipt image next to the structured parse so
you can compare — swap in your own recognizer:

```sql
SELECT receipt_id, models.your_ocr_model(img) AS predicted, ground_truth
FROM datasets.cord_test
LIMIT 10;
```

Sanity-check the row count and image dimensions came through:

```sql
SELECT COUNT(*) AS receipts, MIN(width) AS min_w, MAX(width) AS max_w
FROM datasets.cord_test;
```

## Output schema

Every variant produces the same `receipts` table:

```
receipt_id:   String   -- unique per receipt (the mirror image filename)
img:          Image     -- decoded receipt photo, sidecar-backed
width:        Int64     -- image width in pixels
height:       Int64     -- image height in pixels
ground_truth: Json      -- full CORD annotation (see below)
```

The `ground_truth` cell mirrors CORD's structure:

```
gt_parse:   { menu, sub_total, total }         -- structured KIE target
valid_line: [ { words:[{text, quad, ...}], category, ... }, ... ]  -- word-level OCR
meta:       { image_size, ... }
roi:        region-of-interest quad
```

## Tips

- **Two tasks, one dataset** — for detection/recognition, read
  `valid_line` (word text + quad boxes); for information extraction,
  target `gt_parse`. Pick the view that matches what you're testing.
- **Real photos, not renders** — expect skew, glare, and low-contrast
  thermal paper. Score OCR with a fuzzy / normalized metric, and don't
  be surprised if numbers sit well below clean-document benchmarks.
- **Images are JPEG-re-encoded** — the upstream release ships
  full-resolution PNGs (~2 MB each); the mirror re-encodes to JPEG q90
  to keep the download compact. Visually lossless for OCR, but if you
  need pristine pixels pull the original from
  `naver-clova-ix/cord-v1`.
- **Sidecar-backed images** — `img` is a handle into a `.datum-blob`
  companion file; filter / count queries skip the pixel data.
- **Document, not scene text** — for photographed text in the wild use
  TextOCR / HierText; for dense printed pages, DocBank.

## License & attribution

CC-BY-4.0 — permits commercial use, modification, and redistribution
with attribution. Credit NAVER Clova and the CORD authors.

- Paper: [CORD: A Consolidated Receipt Dataset for Post-OCR Parsing](https://openreview.net/forum?id=SJl3z659UH)
- Upstream: [naver-clova-ix/cord-v1](https://huggingface.co/datasets/naver-clova-ix/cord-v1)
