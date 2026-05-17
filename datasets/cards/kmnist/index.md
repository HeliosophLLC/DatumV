# KMNIST

The cursive-Japanese sibling of MNIST: 70,000 28×28 grayscale samples
of ten Hiragana characters drawn from pre-modern Japanese
woodblock-printed books, released in 2018 by the ROIS Center for
Open Data in the Humanities (Tokyo).

Same IDX file layout as MNIST, same image shape, same ten-class
softmax — but a meaningfully harder task. Cursive Kuzushiji
characters share more visual structure than Latin digits, so the
accuracy ceiling sits around ~95–97 % for strong CNNs (vs ~99.7 % on
MNIST). Useful for benchmarking whether a classifier is actually
learning shape priors rather than memorising digit silhouettes.

## When to use which split

| Variant | Images | Best for |
| ------- | ------:| -------- |
| **kmnist_test** | **10 000** | **Default** for examples. Held-out evaluation slice. |
| kmnist_train | 60 000 | Training a recipe end-to-end. |

## Label vocabulary

Each integer label 0–9 corresponds to one of ten Hiragana characters
selected from the most commonly observed forms in classical Japanese
manuscripts:

```
0: お (o)    5: は (ha)
1: き (ki)   6: ま (ma)
2: す (su)   7: や (ya)
3: つ (tsu)  8: れ (re)
4: な (na)   9: を (wo)
```

The characters were picked for visual distinctiveness when written by
many hands and across centuries — not for any phonetic or
semantic property.

## Example SQL

Sanity-check the install:

```sql
SELECT idx, label, img
FROM datasets.kmnist_test
LIMIT 1;
```

Class distribution — should be exactly 1,000 per class on the test
split:

```sql
SELECT label, COUNT(*) AS n
FROM datasets.kmnist_test
GROUP BY label
ORDER BY label;
```

Accuracy:

```sql
SELECT
  AVG(CASE WHEN models.your_kmnist_classifier(img) = label THEN 1.0 ELSE 0.0 END) AS accuracy
FROM datasets.kmnist_test;
```

## Output schema

Both variants produce the same `images` table:

```
idx:   Int64    -- 0..N-1, dense per split
img:   Image    -- PNG-encoded 28×28 grayscale, sidecar-backed
label: UInt8    -- integer character class 0..9
```

## Tips

- **MNIST-compatible** — every recipe / model / training loop that
  works on MNIST runs on KMNIST unchanged. Just point the SQL at a
  different table.
- **Harder than MNIST without being a different shape** — the visual
  ambiguity between cursive characters is the source of the
  ~3-percentage-point accuracy gap. If your model exceeds ~97 % test
  accuracy you're either doing something clever or overfitting.
- **Sister datasets exist** — *Kuzushiji-49* (49 characters) and
  *Kuzushiji-Kanji* (3,832 characters) are larger / harder variants
  from the same authors. Neither is in the catalog yet; this entry
  ships only the MNIST-shape KMNIST cut.

## License & attribution

CC-BY-SA-4.0. Commercial use OK with attribution; the ShareAlike
clause applies if you redistribute or modify the dataset itself
(models trained on it are generally not considered derivative works).

- Paper: [Deep Learning for Classical Japanese Literature](https://arxiv.org/abs/1812.01718)
- Repo: [rois-codh/kmnist](https://github.com/rois-codh/kmnist)
- Hosting: [codh.rois.ac.jp/kmnist](http://codh.rois.ac.jp/kmnist/)
