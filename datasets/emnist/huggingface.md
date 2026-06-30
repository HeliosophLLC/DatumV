---
license: cc-by-4.0
pretty_name: EMNIST (Extended MNIST) — handwritten letters and digits
task_categories:
  - image-classification
language:
  - en
size_categories:
  - 100K<n<1M
tags:
  - emnist
  - mnist
  - handwriting
  - characters
  - letters
  - digits
  - ocr
  - nist
  - idx
  - image-classification
source_datasets:
  - original
---

# EMNIST — handwritten letters and digits, MNIST-style IDX

A verbatim mirror of the [EMNIST dataset](https://www.nist.gov/itl/products-and-services/emnist-dataset) (Cohen, Afshar, Tapson & van Schaik, 2017), repackaged so each split's IDX files are fetchable individually. No pixels or labels added, removed, or reordered relative to NIST's release — only the hosting moved: the per-split members were extracted out of NIST's single bundled `gzip.zip` and re-uploaded at the repo root, byte-for-byte.

Re-hosted under Heliosoph for ingestion-pipeline stability. NIST ships every split inside one ~540 MB `gzip.zip`; a pipeline that wants just the `balanced` split shouldn't have to download the whole bundle and dig a member out of an archive. Pinning a HuggingFace revision lets pipelines fetch the same bytes anonymously and reproducibly, one split at a time.

Credit: Gregory Cohen, Saeed Afshar, Jonathan Tapson, André van Schaik (Western Sydney University) — glyphs drawn from NIST Special Database 19.

## Why a mirror?

NIST distributes EMNIST as a single `gzip.zip` (~540 MB) whose members live under a `gzip/` prefix. Extracting one split means downloading the entire bundle — every other split included — and then pulling a specific `.gz` out of the zip. Neither step is hostile, but neither plays nicely with a reproducible ingestion pipeline that just wants one split's bytes. This mirror flattens the members to the repo root so each `.gz` is a direct, anonymous, revision-pinned download. The bytes inside each `.gz` are identical to NIST's.

## What this repo contains

Twelve gzip-compressed IDX files — three splits, each with a train + test cut, each cut as an images file + a labels file:

```
emnist-balanced-train-images-idx3-ubyte.gz   # 112,800 glyphs, 47 classes
emnist-balanced-train-labels-idx1-ubyte.gz
emnist-balanced-test-images-idx3-ubyte.gz    #  18,800 glyphs
emnist-balanced-test-labels-idx1-ubyte.gz
emnist-letters-train-images-idx3-ubyte.gz    # 124,800 glyphs, 26 classes
emnist-letters-train-labels-idx1-ubyte.gz
emnist-letters-test-images-idx3-ubyte.gz     #  20,800 glyphs
emnist-letters-test-labels-idx1-ubyte.gz
emnist-byclass-train-images-idx3-ubyte.gz    # 697,932 glyphs, 62 classes
emnist-byclass-train-labels-idx1-ubyte.gz
emnist-byclass-test-images-idx3-ubyte.gz     # 116,323 glyphs
emnist-byclass-test-labels-idx1-ubyte.gz
```

EMNIST upstream defines three more splits (`bymerge`, `digits`, `mnist`); this mirror carries the three that cover the useful range — class-balanced (`balanced`), pure-alphabet (`letters`), and the full unbalanced character set (`byclass`).

### File format — MNIST IDX

Each split is a pair of big-endian IDX files, identical in layout to the original MNIST distribution:

| File | Magic | Shape | Dtype | Meaning |
|---|---|---|---|---|
| `*-images-idx3-ubyte` | `0x00000803` | `[N, 28, 28]` | uint8 | 28×28 grayscale glyph, 0 (black) – 255 (white) |
| `*-labels-idx1-ubyte` | `0x00000801` | `[N]` | uint8 | integer class id; vocabulary depends on the split |

Row `i` of the images file pairs with row `i` of the labels file. Any MNIST loader reads these unchanged.

## ⚠️ Orientation — glyphs are stored transposed

EMNIST images are stored **transposed** relative to display orientation: the NIST → MNIST conversion baked in a 90° rotation plus a horizontal mirror. Read raw, every character looks rotated and flipped. The fix is a single matrix transpose (reflect across the main diagonal):

```python
img = raw.reshape(28, 28).T   # upright
```

This is a property of the upstream bytes, not the mirror — any faithful EMNIST consumer must transpose. (DatumV applies `image_transpose()` in its install recipe, so the ingested images are already upright.)

## Class labels

The label integer's meaning depends on the split:

- **balanced** (`0`–`46`): `0`–`9` are digits; `10`–`46` are letters, with the 15 upper/lowercase pairs that are visually identical (C/c, O/o, S/s, …) merged into a single class. Equal samples per class.
- **byclass** (`0`–`61`): `0`–`9` digits, `10`–`35` are `A`–`Z`, `36`–`61` are `a`–`z`. Frequencies follow natural handwriting distribution (heavily skewed).
- **letters** (`1`–`26`): `1`–`26` map to `A`–`Z` with case merged. Note the **1-based** indexing — subtract 1 before indexing a zero-based class array.

## How to use

Read an IDX pair directly with NumPy (no extra dependencies), remembering the transpose:

```python
import gzip, numpy as np

def load_images(path):
    with gzip.open(path, "rb") as f:
        magic, n, rows, cols = np.frombuffer(f.read(16), dtype=">u4")
        data = np.frombuffer(f.read(), dtype=np.uint8).reshape(n, rows, cols)
    return np.transpose(data, (0, 2, 1))      # upright: transpose each glyph

def load_labels(path):
    with gzip.open(path, "rb") as f:
        magic, n = np.frombuffer(f.read(8), dtype=">u4")
        return np.frombuffer(f.read(), dtype=np.uint8)

x = load_images("emnist-balanced-train-images-idx3-ubyte.gz")  # (112800, 28, 28)
y = load_labels("emnist-balanced-train-labels-idx1-ubyte.gz")  # (112800,)
print(x.shape, y.shape, y.min(), y.max())                       # ... 0 46
```

Or with `idx2numpy` if you prefer (`pip install idx2numpy`):

```python
import gzip, idx2numpy, numpy as np
with gzip.open("emnist-letters-test-images-idx3-ubyte.gz", "rb") as f:
    images = np.transpose(idx2numpy.convert_from_string(f.read()), (0, 2, 1))
```

## Dataset specs

| | Spec |
|---|---|
| Splits in this mirror | balanced (47 cls), letters (26 cls), byclass (62 cls) |
| Glyphs — balanced | 112,800 train / 18,800 test |
| Glyphs — letters | 124,800 train / 20,800 test |
| Glyphs — byclass | 697,932 train / 116,323 test |
| Image shape | 28×28, grayscale, uint8 (0–255) |
| Orientation | stored transposed — consumer must transpose to upright |
| Format | MNIST-style IDX (`idx3-ubyte` images, `idx1-ubyte` labels), gzip-compressed |
| Compressed size | ~180 MB total across the 12 files |
| Language | English (Latin alphanumerics) |
| Source corpus | NIST Special Database 19 |

## When to pick EMNIST

- **Drop-in harder MNIST**: identical 28×28 IDX shape, so any MNIST recipe runs unchanged — but strong CNNs report ~88–91% test accuracy on `balanced` vs. ~99.7% on MNIST. A better smoke test for whether a classifier is actually learning.
- **Character / handwriting recognition**: the `letters` split is a clean 26-class A–Z problem; `byclass` exercises the full 62-class digit + upper + lower vocabulary.
- **Imbalanced-classification practice**: `byclass` follows natural letter-frequency distribution — report macro-F1, not just accuracy, and reach for resampling / threshold calibration.

For plain digit classification reach for **MNIST**; for cursive-Japanese characters in the same IDX shape, **KMNIST**.

## License

**CC BY 4.0** (closest match; the release predates SPDX). The underlying NIST Special Database 19 is a public-domain US-government work distributed as "freely usable", and EMNIST is broadly used commercially in practice. Preserve attribution to Cohen / Afshar / Tapson / van Schaik and to NIST.

- Paper: [EMNIST: an extension of MNIST to handwritten letters](https://arxiv.org/abs/1702.05373)
- Upstream: [The EMNIST Dataset — NIST](https://www.nist.gov/itl/products-and-services/emnist-dataset)
- Source corpus: [NIST Special Database 19](https://www.nist.gov/srd/nist-special-database-19)
