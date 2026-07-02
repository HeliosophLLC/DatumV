# DocBank

A document-OCR corpus with **exact** ground truth. Li et al. (Microsoft
Research Asia, 2020) built DocBank by rendering 500,000 pages from
arXiv LaTeX sources — and because the markup is known, every token's
text, bounding box, font, and semantic role is recovered precisely.
No manual OCR, no annotator disagreement: the transcription is correct
by construction. That makes it the reach-for permissive benchmark for
**document** OCR (dense printed pages) as opposed to scene text
(photos of signs) or isolated characters (MNIST / EMNIST).

These variants ingest a sampled cut re-hosted as a per-page mirror
(the upstream release is 54 GB with images split across a ten-part
archive). One row = one page.

## When to use which split

| Variant | Pages | Best for |
| ------- | -----:| -------- |
| **docbank_test** | **1 000** | **Default** for examples — ingests in under a minute, enough to measure an OCR/layout number. |
| docbank_train | 5 000 | More pages for the same schema when you want tighter statistics or a small fine-tune. |

Pages are academic-paper renders: two-column layouts, dense
equations, figures, and reference lists. Expect that domain — it's not
receipts or forms.

## Example SQL

Look at one page — the image alongside its ground-truth text:

```sql
SELECT page_id, width, height, "text", img
FROM datasets.docbank_test
LIMIT 1;
```

Run an OCR model over the page image next to the ground-truth text so
you can eyeball (or diff) the two — swap in your own recognizer:

```sql
SELECT page_id, models.your_ocr_model(img) AS predicted, "text" AS truth
FROM datasets.docbank_test
LIMIT 20;
```

Distribution of page sizes across the split (a quick sanity check that
the images decoded and the dimensions came through):

```sql
SELECT width, height, COUNT(*) AS n
FROM datasets.docbank_test
GROUP BY width, height
ORDER BY n DESC
LIMIT 10;
```

The `tokens` column is a JSON array of `{text, x0, y0, x1, y1, label,
font}` (bboxes normalized to 0–1000) — carried through as a `Json`
cell for downstream box- and label-level evaluation.

## Output schema

Both variants produce the same `pages` table:

```
page_id: String   -- unique page identifier (matches the source image name)
img:     Image    -- decoded full-page render, sidecar-backed
text:    String   -- full-page reading-order text (tokens space-joined)
width:   Int64    -- page width in pixels
height:  Int64    -- page height in pixels
tokens:  Json     -- array of {text, x0, y0, x1, y1, label, font}
```

The twelve semantic labels are: `title`, `author`, `abstract`,
`paragraph`, `section`, `list`, `caption`, `equation`, `figure`,
`table`, `reference`, `footer`. Token bounding boxes are normalized to
a 0–1000 coordinate space (independent of the rendered image size), so
scale by `width/1000` and `height/1000` to project onto `img`.

## Tips

- **Ground truth is machine-perfect, not human** — because it comes
  from the LaTeX source, the text is exactly right, but it reflects the
  *source* rather than a human reading of the pixels. Ligatures,
  hyphenation, and math tokenization follow LaTeX conventions, which
  can differ from what a pixel-level OCR model emits. Score with a
  fuzzy / normalized-edit-distance metric, not exact string match.
- **`(cid:NN)` tokens on math-heavy pages** — glyphs from subset or
  embedded fonts that LaTeX couldn't map back to Unicode surface as
  literal `(cid:88)`-style placeholders in `text` and `tokens`. Pages
  dense in equations (physics / math papers) can carry many of these;
  text-heavy pages (abstracts, intros, references) are clean. Filter
  them out — e.g. skip tokens matching `(cid:` — before scoring, or
  restrict to text-dominant pages for a cleaner OCR benchmark.
- **Sidecar-backed images** — `img` is a handle into a `.datum-blob`
  companion file; filter / count queries skip the pixel data.
- **Document, not scene text** — for photographed text in the wild use
  a scene-text set (TextOCR / HierText); for receipts, CORD.

## License & attribution

Apache-2.0 — the underlying arXiv documents are used under their
submission terms; the DocBank annotations and packaging are released
by the authors under Apache-2.0, which permits commercial use,
modification, and redistribution with attribution.

- Paper: [DocBank: A Benchmark Dataset for Document Layout Analysis](https://arxiv.org/abs/2006.01038)
- Upstream: [doc-analysis/DocBank](https://github.com/doc-analysis/DocBank)
- Source documents: [arXiv.org](https://arxiv.org/)
