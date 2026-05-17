# CIFAR-10

60,000 32×32 colour photographs labelled with one of ten everyday
categories. Released in 2009 by Alex Krizhevsky as part of the Tiny
Images project at the University of Toronto — the reach-for
small-colour benchmark in the slot between MNIST and ImageNet.

Both variants share the same image distribution, the same ten-class
label vocabulary, and the same record format. Pick a split, not a
dataset: swap `datasets.cifar10_test` for `datasets.cifar10_train` in
a query and the only thing that changes is the row count.

## When to use which split

| Variant | Images | Best for |
| ------- | ------:| -------- |
| **cifar10_test** | **10 000** | **Default** for examples. Held-out evaluation slice; every accuracy number in the literature reports against this split. |
| cifar10_train | 50 000 | Fine-tuning a recipe end-to-end. Same image shape as test, five times more rows. |

Start with **cifar10_test** for new tinkering — it's the canonical
eval slice and 5× faster to ingest than the train split.

## Label vocabulary

The ten labels are mutually exclusive and roughly evenly distributed:

```
0: airplane     5: dog
1: automobile   6: frog
2: bird         7: horse
3: cat          8: ship
4: deer         9: truck
```

The recipe joins `batches.meta.txt` from inside the upstream tar.gz at
install time, so each row carries both the integer `label` and the
matching `label_name` string. Most queries are easier to read against
`label_name`.

## Example SQL

Confirm the install and look at a single row:

```sql
SELECT idx, label, label_name, img
FROM datasets.cifar10_test
LIMIT 1;
```

Class distribution (should be exactly 1,000 per class on test):

```sql
SELECT label_name, COUNT(*) AS n
FROM datasets.cifar10_test
GROUP BY label_name
ORDER BY label_name;
```

Run a classifier and measure accuracy:

```sql
SELECT
  AVG(CASE WHEN models.your_cifar10_classifier(img) = label THEN 1.0 ELSE 0.0 END) AS accuracy
FROM datasets.cifar10_test;
```

Per-class accuracy (which categories does the classifier struggle
with?):

```sql
WITH preds AS (
  SELECT label_name AS truth, models.your_cifar10_classifier(img) AS pred_id
  FROM datasets.cifar10_test
)
SELECT truth,
       AVG(CASE WHEN truth = (
         CASE pred_id
           WHEN 0 THEN 'airplane' WHEN 1 THEN 'automobile'
           WHEN 2 THEN 'bird'     WHEN 3 THEN 'cat'
           WHEN 4 THEN 'deer'     WHEN 5 THEN 'dog'
           WHEN 6 THEN 'frog'     WHEN 7 THEN 'horse'
           WHEN 8 THEN 'ship'     WHEN 9 THEN 'truck'
         END) THEN 1.0 ELSE 0.0 END) AS accuracy,
       COUNT(*) AS n
FROM preds
GROUP BY truth
ORDER BY accuracy ASC;
```

Spot-check 12 random images from one class:

```sql
SELECT idx, img
FROM datasets.cifar10_test
WHERE label_name = 'frog'
ORDER BY RANDOM()
LIMIT 12;
```

## Output schema

Both variants produce the same `images` table:

```
idx:        Int64    -- 0..N-1, dense per split (assigned via row_number)
img:        Image    -- PNG-encoded 32×32 RGB, sidecar-backed
label:      UInt8    -- integer class 0..9
label_name: String   -- canonical class name ('airplane'..'truck')
```

`idx` on the train split is a global row number across the five
underlying binary batches (data_batch_1..5.bin); the recipe assigns
it with `row_number() OVER ()` so callers see a unified 50,000-row
stream regardless of the upstream packaging.

## Tips

- **Tiny but harder than MNIST** — at 32×32 with three colour
  channels, the input is small enough to train quickly but
  meaningful enough that headline accuracy (~94–97 % for a strong
  CNN) has real headroom. ImageNet-pretrained features transfer
  reasonably well.
- **Strong inter-class similarity** — `cat`/`dog`, `automobile`/
  `truck`, and `deer`/`horse` are the canonical confusion pairs.
  Per-class accuracy is the headline metric worth checking.
- **Train split costs ~5× more disk** — five 10k-record batches vs
  the test split's single batch. The upstream tar.gz is downloaded
  twice (once per variant) because variant raw caches don't share.
- **Sidecar-backed images** — `img` is a handle into a `.datum-blob`
  companion file. `SELECT COUNT(*)` skips the blobs entirely.

## License & attribution

Released by Alex Krizhevsky without a formal SPDX licence; the
upstream page (`cs.toronto.edu/~kriz/cifar.html`) treats it as freely
usable. We catalogue it as MIT — the closest registry match for
"permissive, commercial-OK with attribution."

- Paper: [Learning Multiple Layers of Features from Tiny Images](https://www.cs.toronto.edu/~kriz/learning-features-2009-TR.pdf)
- Site: [cs.toronto.edu/~kriz/cifar.html](https://www.cs.toronto.edu/~kriz/cifar.html)
