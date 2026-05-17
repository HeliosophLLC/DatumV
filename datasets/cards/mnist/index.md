# MNIST

The 1998 LeCun-Cortes-Burges corpus of handwritten digits: 70,000
28×28 grayscale samples drawn from NIST Special Database 19,
preprocessed to a fixed size, and paired with their integer class
labels (0–9). Every introductory ML tutorial and many recipe
smoke-tests reach for it first.

Both variants share the same image distribution, the same label
vocabulary, and the same IDX file layout — they differ only in the
size of the split. Pick a split:
`datasets.mnist_test` for hold-out evaluation, `datasets.mnist_train`
for fine-tuning a small recipe. Swapping the table name in a query is
the only difference.

## When to use which split

| Variant | Images | Best for |
| ------- | ------:| -------- |
| **mnist_test** | **10 000** | **Default** for examples. The standard held-out evaluation slice — every accuracy / error-rate number in the literature reports against this split. |
| mnist_train | 60 000 | Fine-tuning a recipe end-to-end on a laptop. Same image shape, just six times more rows. |

Start with **mnist_test** for new tinkering — fits in memory, ingests
in seconds, and saves the train split for when you actually need to
update a model.

## Example SQL

Sanity-check that the install landed and look at a single row:

```sql
SELECT idx, label, img
FROM datasets.mnist_test
LIMIT 1;
```

Class distribution across the split (should be roughly balanced):

```sql
SELECT label, COUNT(*) AS n
FROM datasets.mnist_test
GROUP BY label
ORDER BY label;
```

Run a classifier and measure accuracy against the ground-truth label:

```sql
SELECT
  AVG(CASE WHEN models.your_mnist_classifier(img) = label THEN 1.0 ELSE 0.0 END) AS accuracy
FROM datasets.mnist_test;
```

Confusion-row for a single class (which digits get misclassified as
"3"?):

```sql
SELECT label, COUNT(*) AS misses
FROM datasets.mnist_test
WHERE models.your_mnist_classifier(img) = 3
  AND label <> 3
GROUP BY label
ORDER BY misses DESC;
```

## Output schema

Both variants produce the same `images` table:

```
idx:   Int64    -- 0..N-1, dense per split
img:   Image    -- PNG-encoded 28×28 grayscale, sidecar-backed
label: UInt8    -- integer class 0..9
```

`idx` is the original IDX file index, so a `JOIN` against external
metadata keyed on the dataset's row position is well-defined.

## Tips

- **Trivially easy by modern standards** — strong CNNs report ~99.7 %
  test accuracy. If your headline number is below ~98 %, the bug is
  probably in the training loop, not the model. Use KMNIST or
  Fashion-MNIST when you want a harder smoke test with the same
  shape.
- **Sidecar-backed images** — `img` is a handle into a `.datum-blob`
  companion file, not inline pixel data. Filter / count queries skip
  the blob entirely.
- **Pixel intensities are 0 (black) — 255 (white)** — the original
  files use ink-on-paper white-background images, so most pixels are
  near 0. Many published recipes invert the polarity at training
  time; the ingested PNGs preserve the original convention.

## License & attribution

CC-BY-4.0 (closest match in the registry; the original release predates
SPDX and was distributed as "freely usable for research"). Commercial
use is broadly accepted in practice — preserve attribution to LeCun /
Cortes / Burges and the NIST source database.

- Paper: [Gradient-Based Learning Applied to Document Recognition](http://yann.lecun.com/exdb/publis/pdf/lecun-98.pdf)
- Source mirror: [pytorch/data MNIST](https://github.com/pytorch/data) (`ossci-datasets.s3.amazonaws.com/mnist/`)
- Upstream NIST corpus: [Special Database 19](https://www.nist.gov/srd/nist-special-database-19)
