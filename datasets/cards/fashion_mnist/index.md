# Fashion-MNIST

Zalando Research's drop-in replacement for MNIST: 70,000 28×28
grayscale clothing thumbnails labelled with one of ten garment
categories. Same file layout, same image shape, same ten-class
softmax — but a meaningfully harder task that doesn't saturate at
~99.7 % the way MNIST does.

Reach for it when MNIST is too easy to discriminate between recipes,
when you want a smoke-test that exercises a real visual prior, or as a
sanity check that a "works on MNIST" pipeline transfers to a corpus
with actual structure.

## When to use which split

| Variant | Images | Best for |
| ------- | ------:| -------- |
| **fashion_mnist_test** | **10 000** | **Default** for examples. Held-out evaluation slice paired with the 60k train cut. |
| fashion_mnist_train | 60 000 | Training a recipe end-to-end. |

## Label vocabulary

```
0: T-shirt/top   5: Sandal
1: Trouser       6: Shirt
2: Pullover      7: Sneaker
3: Dress         8: Bag
4: Coat          9: Ankle boot
```

A small caveat: `T-shirt/top` and `Shirt` overlap visually, and
`Pullover` / `Coat` are similarly close in pixel space. The ~95 %
accuracy ceiling that strong CNNs report is dominated by confusions
between these neighbouring categories — checking the confusion matrix
against this list is a good way to tell a real win from noise.

## Example SQL

Look at one row to confirm the install:

```sql
SELECT idx, label, img
FROM datasets.fashion_mnist_test
LIMIT 1;
```

Class distribution — should be exactly 1,000 per class on the test
split (perfectly balanced):

```sql
SELECT label, COUNT(*) AS n
FROM datasets.fashion_mnist_test
GROUP BY label
ORDER BY label;
```

Accuracy against ground truth:

```sql
SELECT
  AVG(CASE WHEN models.your_fashion_classifier(img) = label THEN 1.0 ELSE 0.0 END) AS accuracy
FROM datasets.fashion_mnist_test;
```

Mapping the integer labels to human-readable category names inline
(useful for spot-checking screenshots):

```sql
SELECT
  CASE label
    WHEN 0 THEN 'T-shirt/top' WHEN 1 THEN 'Trouser'
    WHEN 2 THEN 'Pullover'    WHEN 3 THEN 'Dress'
    WHEN 4 THEN 'Coat'        WHEN 5 THEN 'Sandal'
    WHEN 6 THEN 'Shirt'       WHEN 7 THEN 'Sneaker'
    WHEN 8 THEN 'Bag'         WHEN 9 THEN 'Ankle boot'
  END AS category,
  COUNT(*) AS n
FROM datasets.fashion_mnist_test
GROUP BY label
ORDER BY label;
```

## Output schema

Both variants produce the same `images` table:

```
idx:   Int64    -- 0..N-1, dense per split
img:   Image    -- PNG-encoded 28×28 grayscale, sidecar-backed
label: UInt8    -- integer category 0..9
```

## Tips

- **MNIST-compatible — almost everything that works on MNIST runs
  here unchanged.** The recipe / model / training-loop swap is
  literally `s/mnist/fashion_mnist/`.
- **Harder than MNIST without being qualitatively different** —
  expect ~95 % from a tuned CNN vs MNIST's ~99.7 %. The headroom
  comes from the inherent visual ambiguity of clothing categories,
  not from a richer label space.
- **Inverted polarity** — like MNIST, dark fabric on a light
  background. Recipes that normalise pixel values to mean=0 should
  apply the same shift here.

## License & attribution

MIT-licensed by Zalando Research — fully commercial-friendly with
attribution.

- Paper: [Fashion-MNIST: a Novel Image Dataset for Benchmarking Machine Learning Algorithms](https://arxiv.org/abs/1708.07747)
- Repo: [zalandoresearch/fashion-mnist](https://github.com/zalandoresearch/fashion-mnist)
