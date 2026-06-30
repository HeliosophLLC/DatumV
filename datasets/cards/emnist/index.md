# EMNIST

The 2017 Cohen–Afshar–Tapson–van Schaik extension of MNIST to the
full alphanumeric character set. EMNIST re-runs the MNIST conversion
pipeline over the complete NIST Special Database 19, so you get
handwritten **letters as well as digits** in the exact same 28×28
grayscale IDX layout — any recipe, model, or training loop written for
MNIST runs here unchanged.

Pick a split by how you want to trade class count against balance:

| Variant | Glyphs | Classes | Best for |
| ------- | ------:| -------:| -------- |
| **emnist_balanced_test** | **18 800** | 47 | **Default** for examples — class-balanced, so plain accuracy is meaningful. |
| emnist_balanced_train | 112 800 | 47 | The reach-for training cut. Equal samples per class. |
| emnist_letters_train | 124 800 | 26 | Pure A–Z (case merged). Labels are **1-indexed** (1=A … 26=Z). |
| emnist_letters_test | 20 800 | 26 | Held-out eval for the letters cut. |
| emnist_byclass_train | 697 932 | 62 | The full unbalanced set: 10 digits + 26 upper + 26 lower. |
| emnist_byclass_test | 116 323 | 62 | Held-out eval for byclass. Report macro-F1, not just accuracy. |

Start with **emnist_balanced_test** for new tinkering — it ingests in
seconds and the balanced classes keep accuracy interpretable.

## Orientation

EMNIST stores its glyphs **transposed** relative to display
orientation — a 90° rotation plus a mirror baked into the source
pixels by the NIST → MNIST conversion. The install recipe applies
`image_transpose()` to each glyph, so the `img` you query is already
upright. (If you compare against a raw upstream EMNIST loader that
skips this step, expect its images to look rotated/flipped.)

## Class labels

The `label` column is the raw integer class from the IDX file. The
meaning depends on the split:

- **balanced** (0–46): `0`–`9` are the digits; `10`–`46` are letters,
  with the 15 upper/lowercase pairs that are visually identical
  (C/c, O/o, S/s, …) merged into a single class.
- **byclass** (0–61): `0`–`9` digits, `10`–`35` are `A`–`Z`, `36`–`61`
  are `a`–`z`.
- **letters** (1–26): `1`–`26` map to `A`–`Z` with case merged. Note
  the **1-based** indexing — subtract 1 before indexing a zero-based
  class array.

## Example SQL

Sanity-check the install and look at a single row:

```sql
SELECT idx, label, img
FROM datasets.emnist_balanced_test
LIMIT 1;
```

Class distribution (the balanced split should be ~flat; byclass will
be heavily skewed toward common letters):

```sql
SELECT label, COUNT(*) AS n
FROM datasets.emnist_balanced_test
GROUP BY label
ORDER BY label;
```

Run a classifier and measure accuracy against the ground-truth label:

```sql
SELECT
  AVG(CASE WHEN models.your_emnist_classifier(img) = label THEN 1.0 ELSE 0.0 END) AS accuracy
FROM datasets.emnist_balanced_test;
```

## Output schema

Every variant produces the same `images` table:

```
idx:   Int64    -- 0..N-1, dense per split
img:   Image    -- PNG-encoded 28×28 grayscale, upright, sidecar-backed
label: UInt8    -- integer class; vocabulary depends on the split (see above)
```

`idx` is the original IDX file index, so a `JOIN` against external
metadata keyed on the dataset's row position is well-defined.

## Tips

- **Harder than MNIST** — strong CNNs report ~88–91% test accuracy on
  the balanced split vs. ~99.7% on MNIST. Letters share far more
  visual ambiguity than digits (1/I/l, 0/O, 5/S), and the merged
  classes encode that on purpose.
- **Sidecar-backed images** — `img` is a handle into a `.datum-blob`
  companion file, not inline pixel data. Filter / count queries skip
  the blob entirely.
- **Pixel intensities are 0 (black) — 255 (white)** — ink-on-paper
  white-background convention, preserved from the source. Many
  published recipes invert the polarity at training time.

## License & attribution

CC-BY-4.0 (closest match in the registry; like MNIST, the release
predates SPDX and the underlying NIST Special Database 19 is a
public-domain US-government work distributed as "freely usable").
Commercial use is broadly accepted in practice — preserve attribution
to Cohen / Afshar / Tapson / van Schaik and the NIST source database.

- Paper: [EMNIST: an extension of MNIST to handwritten letters](https://arxiv.org/abs/1702.05373)
- Upstream: [The EMNIST Dataset — NIST](https://www.nist.gov/itl/products-and-services/emnist-dataset)
- Source corpus: [NIST Special Database 19](https://www.nist.gov/srd/nist-special-database-19)
