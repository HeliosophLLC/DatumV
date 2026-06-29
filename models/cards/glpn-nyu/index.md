# GLPN-NYU (Lightweight Depth)

KAIST's Global-Local Path Network — an EfficientFormer encoder + custom
hierarchical decoder (~52M params) for monocular depth, trained on the NYU
Depth V2 **indoor** dataset. Useful as a lightweight baseline and, because
its backbone family is distinct from the DPT / DINOv2 lineage every other
depth model here uses, as an architectural-diversity row in a comparison.

GLPN regresses **absolute (metric) depth** in real metres against the NYU
ground truth — so two models ship: a relative-style visualization and a
raw-metres variant.

| Model              | Returns          | Use                                                  |
| ------------------ | ---------------- | --------------------------------------------------- |
| `glpn_nyu`         | `Image`          | A grayscale depth map for viewing / comparison.     |
| `glpn_nyu_meters`  | `Array<Float32>` | Raw per-pixel depth in metres, aligned to the image. |

Because GLPN was trained on indoor scenes, depth quality is best on rooms,
hallways, and tabletop scenes; outdoor / far-field images fall outside its
training distribution.

## Example SQL

COCO 2017 val is images-only — `file` is the decoded JPEG, `file_name`
its path.

Depth-map visualization over each image:

```sql
SELECT
    LET depth = models.glpn_nyu(file) AS depth,
    file AS baseline,
    file_name
FROM datasets.coco_val2017
LIMIT 32;
```

Raw metres — a per-pixel `Float32` array aligned to the image, for
analysis rather than viewing:

```sql
SELECT
    file_name,
    models.glpn_nyu_meters(file) AS depth_meters
FROM datasets.coco_val2017
LIMIT 8;
```

## Output shape

- `glpn_nyu` → an `Image`: grayscale depth map, **brighter = closer**,
  resized to the source dimensions. (GLPN emits metres where bigger =
  *farther*, so the visualization is inverted to match the rest of the
  depth zoo's near-is-bright convention.)
- `glpn_nyu_meters` → an `Array<Float32>`, a per-pixel depth map in metres,
  bilinear-resized so it aligns 1:1 with the input image's H×W.

## Tips

- **Indoor model.** Trained on NYU Depth V2 — strongest on interior
  scenes; treat metres on outdoor images with suspicion.
- **Two outputs, one network.** `glpn_nyu` is for looking at;
  `glpn_nyu_meters` is for computing with. Pick by whether you need a
  picture or numbers.
- **480×480 input**, ImageNet mean/std, handled inside the body — pass the
  raw `Image` column straight in.
- **For metric depth on general scenes**, prefer `zoedepth_nyu_kitti`
  (indoor + outdoor) or `depth_anything_v3_large`; GLPN's metric range is
  NYU-indoor-bound.

## License & attribution

Apache-2.0. Original model by KAIST (GLPN — Kim, Ga, Ahn, Joo, Chun, Kim);
ONNX export (Xenova) of `vinvino02/glpn-nyu`.

- Upstream: [vinvino02/GLPDepth](https://github.com/vinvino02/GLPDepth)
- Paper: [Global-Local Path Networks for Monocular Depth Estimation](https://arxiv.org/abs/2201.07436)
