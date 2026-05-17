# CIFAR-100

The harder sibling of CIFAR-10: 60,000 32×32 colour photographs from
the same Tiny Images source pool, but labelled with one of **100
fine-grained classes** grouped under **20 coarse superclasses**. Same
image format, same record layout — just a much richer label space.

Reach for it when CIFAR-10 saturates and you need either (a) a
small-image benchmark with real classification headroom, or (b) a
testbed for hierarchical / multi-task classification — the
coarse/fine taxonomy is genuinely structured rather than synthetic.

## When to use which split

| Variant | Images | Best for |
| ------- | ------:| -------- |
| **cifar100_test** | **10 000** | **Default** for examples. Held-out evaluation slice. |
| cifar100_train | 50 000 | Training a recipe end-to-end. |

## Label hierarchy

The 100 fine classes group cleanly into 20 coarse superclasses, five
fine per coarse. Examples of the grouping:

```
coarse: 'flowers'        → fine: 'rose', 'tulip', 'sunflower', 'orchid', 'poppy'
coarse: 'vehicles_1'     → fine: 'bicycle', 'bus', 'motorcycle', 'pickup_truck', 'train'
coarse: 'large_carnivores' → fine: 'bear', 'leopard', 'lion', 'tiger', 'wolf'
coarse: 'household_furniture' → fine: 'bed', 'chair', 'couch', 'table', 'wardrobe'
```

The integer label spaces are independent — coarse 5 isn't related to
fine 5. The recipe joins both `coarse_label_names.txt` and
`fine_label_names.txt` from inside the upstream tar.gz at install
time, so each row carries both integer labels alongside their
human-readable names.

## Example SQL

Confirm the install:

```sql
SELECT idx, coarse_label_name, fine_label_name, img
FROM datasets.cifar100_test
LIMIT 1;
```

Class distribution at the fine level (should be exactly 100 per fine
class on test):

```sql
SELECT fine_label_name, COUNT(*) AS n
FROM datasets.cifar100_test
GROUP BY fine_label_name
ORDER BY fine_label_name;
```

Fine classes inside one coarse superclass:

```sql
SELECT DISTINCT fine_label_name
FROM datasets.cifar100_test
WHERE coarse_label_name = 'flowers'
ORDER BY fine_label_name;
```

Fine-label accuracy:

```sql
SELECT
  AVG(CASE WHEN models.your_cifar100_classifier(img) = fine_label THEN 1.0 ELSE 0.0 END) AS fine_accuracy
FROM datasets.cifar100_test;
```

Coarse vs fine accuracy from the same fine classifier (the coarse
prediction is implied by the fine prediction's superclass membership):

```sql
WITH preds AS (
  SELECT
    coarse_label, fine_label,
    models.your_cifar100_classifier(img) AS pred_fine
  FROM datasets.cifar100_test
),
mapping AS (
  -- Build a fine→coarse lookup from the dataset itself.
  SELECT DISTINCT fine_label, coarse_label
  FROM datasets.cifar100_test
)
SELECT
  AVG(CASE WHEN pred_fine = preds.fine_label THEN 1.0 ELSE 0.0 END) AS fine_accuracy,
  AVG(CASE WHEN m.coarse_label = preds.coarse_label THEN 1.0 ELSE 0.0 END) AS coarse_accuracy
FROM preds
JOIN mapping m ON m.fine_label = preds.pred_fine;
```

## Output schema

Both variants produce the same `images` table:

```
idx:                Int64    -- 0..N-1, dense per split
img:                Image    -- PNG-encoded 32×32 RGB, sidecar-backed
coarse_label:       UInt8    -- integer superclass 0..19
fine_label:         UInt8    -- integer class 0..99
coarse_label_name:  String   -- e.g. 'flowers'
fine_label_name:    String   -- e.g. 'rose'
```

## Tips

- **Same image distribution as CIFAR-10** — pretraining on CIFAR-10
  then fine-tuning on CIFAR-100 is a common (and effective) recipe.
  The pixel statistics are identical.
- **Sparse classes** — with 100 fine classes and only 500 train
  images each (50k / 100), per-class training signal is thin. Strong
  recipes report ~80–85 % fine-label accuracy; anything above 90 %
  usually indicates leakage or evaluation on the wrong split.
- **Coarse is much easier than fine** — typical fine→coarse accuracy
  ratios hover around 60 / 80 %. Use the coarse axis when you want a
  smoke-test that still shows separation between models.
- **The 100 fine classes overlap visually inside a superclass** —
  `rose`/`tulip` are both red flower petals at 32×32; `bear`/`wolf`
  have similar body shapes. Confusion-matrix inspection within a
  coarse class is more revealing than overall accuracy.

## License & attribution

Released by Alex Krizhevsky without a formal SPDX licence; treated as
freely usable. We catalogue it as MIT — closest registry match for
"permissive, commercial-OK with attribution."

- Paper: [Learning Multiple Layers of Features from Tiny Images](https://www.cs.toronto.edu/~kriz/learning-features-2009-TR.pdf)
- Site: [cs.toronto.edu/~kriz/cifar.html](https://www.cs.toronto.edu/~kriz/cifar.html)
