# MobileNetV2 (ImageNet Classification)

A tiny (~14 MB, 3.5M-param) whole-image classifier from Google AI
Research. One ONNX dispatch over a 224×224 image produces a 1000-way
softmax over the standard ImageNet-1K vocabulary; the model body returns
the single top class and its probability. The CPU-friendly baseline for
"what is this a picture of."

This is *whole-image* classification, not detection: it emits one label
for the entire frame, not a box per object. For boxes use a detector
([YOLOX](../yolox/index.md) / [RT-DETR-R18](../rtdetr-r18/index.md)); for open-vocabulary
labels use a captioner or VLM.

One SQL-visible model ships: `mobilenetv2`. It takes an `Image` and
returns a `ScoredLabel` struct. There's a single variant — no GPU
needed, runs in ~128 MB of RAM.

## Example SQL

The COCO 2017 validation split is images-only — a `file` column carries
the decoded JPEG and `file_name` carries its path inside the source zip.

Classify each image — the result is a `{label, score}` struct:

```sql
SELECT
    file_name,
    file AS image,
    models.mobilenetv2(file) AS prediction
FROM datasets.coco_val2017
LIMIT 10;
```

Output:

![Classify each image — the result is a `{label, score}` struct](query.jpg)

Split the struct into separate label / score columns (compute once in a
subquery, then read the fields):

```sql
SELECT
    file_name,
    file AS image,
    p.label,
    p.score
FROM (
    SELECT file_name, file, models.mobilenetv2(file) AS p
    FROM datasets.coco_val2017
    LIMIT 50
) t;
```

Most common ImageNet labels across the split:

```sql
SELECT p.label AS label, COUNT(*) AS n
FROM (
    SELECT models.mobilenetv2(file) AS p
    FROM datasets.coco_val2017
    LIMIT 500
) t
GROUP BY p.label
ORDER BY n DESC;
```

Output:

![Classify each image — the result is a `{label, score}` struct](query2.jpg)

Keep only confident predictions:

```sql
SELECT file_name, p.label, p.score
FROM (
    SELECT file_name, models.mobilenetv2(file) AS p
    FROM datasets.coco_val2017
    LIMIT 200
) t
WHERE p.score > 0.5
ORDER BY p.score DESC;
```

## Output shape

`mobilenetv2` returns a single `ScoredLabel` struct:

```
label: String   -- ImageNet-1K class name (e.g. "tabby cat", "sports car")
score: Float32  -- 0.0–1.0 softmax probability of the top class
```

It returns only the top-1 class. There's no top-k variant in v1 — if you
need the runner-up classes, that's a follow-up.

## Tips

- **ImageNet labels, not COCO.** The 1000 classes are the fine-grained
  ImageNet-1K vocabulary (hundreds of dog/bird breeds, specific
  objects), *not* COCO's 80 categories. On COCO's cluttered
  multi-object scenes the single whole-image guess is often approximate
  — the classifier picks one dominant thing. Treat low scores as "unsure,"
  not "wrong label."
- **ImageNet normalization** at 224×224 — the model body applies
  ImageNet mean/std internally via `image_to_tensor_chw`; pass the raw
  `Image` column straight in.
- **It's a baseline.** 3.5M params, top-1 ImageNet accuracy in the low
  70s. Fast and cheap, not state-of-the-art — reach for a larger
  classifier or a VLM when accuracy matters.
- **Classify once, aggregate many.** The model call is the cost.
  Materialize predictions into a `ScoredLabel` column and group / filter
  over that rather than re-running per query.

## License & attribution

Apache-2.0. Original model by Google AI Research (MobileNetV2 — Sandler,
Howard, Zhu, Zhmoginov, Chen); ONNX export from the ONNX Model Zoo,
re-hosted on HuggingFace under `Heliosoph`.

- Paper: [MobileNetV2: Inverted Residuals and Linear Bottlenecks](https://arxiv.org/abs/1801.04381)
- ONNX source: [onnx/models — MobileNetV2](https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet)
- ONNX export: [Heliosoph/mobilenetv2-onnx](https://huggingface.co/Heliosoph/mobilenetv2-onnx)
