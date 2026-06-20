---
title: Person crops with YOLOX
---

![Search for people](../figures/search_person.jpg)

YOLOX is a fast object detector. Its output for an image is an array of structs — one struct per detection, each carrying the bounding box and the predicted label. Treating that as a column means you can filter and crop on what the model returned, with no glue code in between.

```sql
SELECT
    LET classes = models.yolox_s(a.file),
    image_crop(file, c.value.bbox)
FROM datasets.coco_val2017 a
CROSS JOIN unnest(classes) c
WHERE c.value.label = 'person'
LIMIT 100
```

The pieces:

- `models.yolox_s(a.file)` runs YOLOX-Small on each image and returns the detection array.
- `LET classes = ...` binds the array so the same call doesn't have to be repeated downstream.
- `CROSS JOIN unnest(classes) c` expands one image row into one row per detection.
- `WHERE c.value.label = 'person'` filters to person detections.
- `image_crop(file, c.value.bbox)` returns the cropped region as an `Image`.

YOLOX-Small is the smallest model in the family and runs comfortably on CPU. Larger variants (`yolox_m`, `yolox_l`, `yolox_x`) trade speed for accuracy. Open the **Model Catalog** tab for the full set of variants and their licenses; see [Models](../models.md) for the conceptual surface around `models.X(...)` dispatch and `Array<Struct>` outputs.
