---
license: mit
library_name: onnx
tags:
  - 3d-generation
  - mesh-generation
  - image-to-3d
  - triposr
  - onnx
base_model: stabilityai/TripoSR
pipeline_tag: image-to-3d
---

# TripoSR — Single-Image to 3D Mesh (ONNX)

ONNX export of [stabilityai/TripoSR](https://huggingface.co/stabilityai/TripoSR) — Stability AI + Tripo AI's single-image-to-mesh model. ViT image encoder (DINOv2) + triplane decoder + implicit field, all trained jointly so the model can **hallucinate the back and occluded sides** of an object from a single front-facing photo. Object-scale, not scene-scale: works best on a single subject centered in frame, ideally with a clean background.

This is the **complement** to depth-based 3D pipelines (depth → point cloud → Poisson mesh), which can only capture what the camera actually sees. TripoSR fills in plausible-but-not-truthful geometry for the unseen sides — good for content creation, wrong for scientific measurement.

Re-exported from upstream PyTorch weights via `torch.onnx.export`. Provenance trail: Tochilkin et al. → cloned VAST-AI-Research/TripoSR (for the `tsr/` architecture module) + stabilityai/TripoSR (for `config.yaml` + `model.ckpt` weights from the Hub) → two separately-traced ONNX graphs (image→triplane and (triplane,xyz)→(density,color)) → these files.

Toolchain: `torch 2.4.x` (CUDA 12.4), `torchvision 0.19`, `transformers 4.45.2`, `einops>=0.7`, `omegaconf>=2.3`, `jaxtyping>=0.2.20`, `onnx>=1.16`, `onnxconverter-common>=1.14`, opset 17, `do_constant_folding=True`. Full conversion script: [`scripts/export-triposr.ps1`](https://github.com/HeliosophLLC/DatumV/blob/main/scripts/export-triposr.ps1) in the Heliosoph repo. The script also writes a `requirements.txt` / `requirements-torch.txt` / `requirements-freeze.txt` / `README.txt` quartet into the output directory so the exact venv state is recoverable from just the uploaded files.

Why two graphs instead of one: TripoSR is a feedforward image-to-triplane model whose downstream "render" step samples a NeRF MLP at many query points on a 3D grid (chunked over ~16M points at 256³ resolution). Tracing the entire thing as one graph would either (a) bake in a specific grid resolution as a constant, or (b) require dynamic-shape grid construction inside the ONNX graph — both fragile. Splitting it makes the per-image cost (`triplane.onnx`) and the per-chunk cost (`nerf.onnx`) independently schedulable on the host. Marching cubes runs entirely outside the ONNX graph in the host engine.

Credit: Dmitry Tochilkin, David Pankratz, Zexiang Liu, Zixuan Huang, Adam Letts, Yangguang Li, Ding Liang, Christian Laforte, Varun Jampani, Yan-Pei Cao (Stability AI + Tripo AI / VAST-AI-Research). Paper: *["TripoSR: Fast 3D Object Reconstruction from a Single Image"](https://arxiv.org/abs/2403.02151)*, arXiv:2403.02151, 2024.

## What this repo contains

TripoSR ships as a **two-graph pipeline**, with the TripoSR runtime config + a reproducibility manifest alongside. Total bundle is ~3.4 GB; the `.onnx_data` sidecar must travel with its `.onnx` (ORT loads external-data sidecars implicitly by filename match in the same directory).

| File | Role | Size |
|---|---|---|
| `triplane.onnx` | Image (RGB 512×512) → triplane features `[B, 3, C, 64, 64]`. Run **once per image**. | ~920 KB graph |
| `triplane.onnx_data` | External weights for `triplane.onnx`. Spilled to a sidecar via `save_as_external_data` so the .onnx stays under the 2 GB protobuf limit. | ~3.1 GB |
| `nerf.onnx` | (triplane, xyz points `[K, 3]`) → (density `[K]`, color features `[K, *]`). Run **many times** per image, chunked over a 3D query grid. Uses `grid_sample` internally — requires opset ≥ 17. | ~180 KB (graph + weights) |
| `config.yaml` | TripoSR runtime config (DINOv1 spec, triplane channel count, NeRF MLP dims, render radius). The architecture module reads this — keep alongside the .onnx files. | <1 KB |
| `requirements.txt` | PyPI pin set used at export time. Recreates the working venv. | <1 KB |
| `requirements-torch.txt` | torch + torchvision pins (PyTorch cu124 index). | <1 KB |
| `requirements-freeze.txt` | Full `pip freeze` capturing transitive closure for byte-identical recreation. | ~2 KB |
| `README.txt` | Provenance manifest written by the export script: HF model id, the TripoSR architecture-repo commit sha that was traced, file inventory, recreation steps. | ~2 KB |

If you ran the export with `-Fp16`, you'll also see `triplane_fp16.onnx` + `triplane_fp16.onnx_data` + `nerf_fp16.onnx` siblings (~half the disk footprint, IO types kept fp32 so consumer code is identical to the fp32 path).

**What's NOT in the ONNX graphs**: the marching-cubes step that extracts a triangle mesh from the density grid. That's a classical algorithm; it runs as a downstream pipeline step in whatever consumer renders the mesh (the Heliosoph engine has it as part of the `mesh_compute_*` SQL function family). Same convention used by InstantMesh, CRM, Shap-E, and other implicit-field mesh-gen models.

## Input / output

| Graph | Input(s) | Output(s) |
|---|---|---|
| `triplane.onnx` | `image` — `[batch, 3, 512, 512]` float32, RGB, **pre-resized to 512×512** and normalized to `[0, 1]` on the host (the upstream PIL-side image_processor is bypassed because it doesn't trace cleanly) | `triplane` — `[batch, 3, C, 64, 64]` float32 (C depends on TripoSR config; ~32-96 channels typically) |
| `nerf.onnx` | `triplane` — `[1, 3, C, 64, 64]`; `xyz` — `[K, 3]` query points in radius-normalized coords `[-R, R]` where R≈0.87 | `density` — `[K]` activated density (the values marching-cubes runs on); `color` — `[K, *]` activated RGB features (sample at MC vertex positions for per-vertex color) |

Dynamic axes: `triplane.onnx` is dynamic in batch only (image dims are fixed at 512×512 — TripoSR is trained at that resolution). `nerf.onnx` is dynamic in batch + points, so chunk size can vary at runtime.

## How to use

The runtime pattern matches what the script's generated `README.txt` documents:

```python
import onnxruntime as ort
import numpy as np
from PIL import Image

triplane_sess = ort.InferenceSession("triplane.onnx")
nerf_sess     = ort.InferenceSession("nerf.onnx")

# 1. Pre-resize + normalize the image on the host (the ONNX bypasses PIL).
img = Image.open("subject.png").convert("RGB").resize((512, 512))
arr = np.asarray(img, dtype=np.float32) / 255.0
arr = arr.transpose(2, 0, 1)[None, ...]                              # 1x3x512x512

# 2. Encode image to triplane features. One ORT.Run.
triplane = triplane_sess.run(None, {"image": arr.astype(np.float32)})[0]

# 3. Build a 3D query grid + chunk over it. 256³ is the standard
#    resolution; ~16M points total, chunked at ~256K per nerf.onnx call.
RESOLUTION = 256
RADIUS     = 0.87
CHUNK      = 262_144

coords = np.linspace(-RADIUS, RADIUS, RESOLUTION, dtype=np.float32)
xx, yy, zz = np.meshgrid(coords, coords, coords, indexing="ij")
xyz = np.stack([xx, yy, zz], axis=-1).reshape(-1, 3)                 # [16.7M, 3]

densities = np.empty(xyz.shape[0], dtype=np.float32)
for i in range(0, xyz.shape[0], CHUNK):
    chunk = xyz[i : i + CHUNK]
    d, _ = nerf_sess.run(None, {"triplane": triplane, "xyz": chunk})
    densities[i : i + chunk.shape[0]] = d
density_grid = densities.reshape(RESOLUTION, RESOLUTION, RESOLUTION)

# 4. Marching cubes on the density grid (host-side).
import mcubes
vertices, triangles = mcubes.marching_cubes(density_grid, 0.0)

# 5. Optional: per-vertex color via a second nerf.onnx pass at vertex positions.
#    vertices are in voxel coords; rescale back to radius-normalized [-R, R] first.
verts_radius = ((vertices / (RESOLUTION - 1)) * 2.0 - 1.0) * RADIUS
_, color = nerf_sess.run(None, {
    "triplane": triplane,
    "xyz":      verts_radius.astype(np.float32),
})

# vertices: Nx3 float; triangles: Mx3 int — feed to Three.js / trimesh / GLB writer.
```

The exact input/output names per ONNX file are stable across exports of this script (`image` / `triplane` for graph 1; `triplane` / `xyz` / `density` / `color` for graph 2), but inspect with [Netron](https://netron.app/) if you're integrating with a different runtime that's picky about names.

## When to pick TripoSR vs depth-based 3D

The two pipelines are complementary, not competing:

| Pipeline | Strength | Weakness |
|---|---|---|
| **TripoSR (this)** | Complete 3D object (front + back + interior), generates geometry the camera couldn't see, single image input | Hallucinated for unseen parts (plausible but not truthful), object-scale only, no metric units |
| **Depth model + Poisson reconstruction** | Metrically faithful (with ZoeDepth), scientifically usable, scene-friendly, multi-image composable | Only captures the visible surface — no back, no occlusions filled |

For "give me a complete model of this object I photographed," pick TripoSR.
For "give me an accurate reconstruction of this scene," pick depth + Poisson.

## License

**MIT** — same as the upstream `stabilityai/TripoSR` model card declaration (`license: mit` in YAML frontmatter) and the underlying VAST-AI-Research/TripoSR code repo. `LICENSE` file included in this Heliosoph mirror with copyright attributed to Stability AI + Tripo AI / VAST-AI-Research.

A note on the upstream gating: `stabilityai/TripoSR` on HuggingFace requires a click-through `extra_gated_fields` form (name / email / organization, plus an opt-in to Stability marketing) before download. **That gate is an access-control mechanism, not a license** — the binding license remains MIT regardless of what you fill in on the form. This Heliosoph mirror has no such gate; download is anonymous and the MIT terms apply directly.

## Related models worth knowing

If you want to go further than TripoSR's quality (at higher engineering cost):

- **TRELLIS** (Microsoft Research, 2024) — MIT, often higher quality output, multi-view conditioned generation. PyTorch only — no clean ONNX export yet.
- **Hunyuan3D-2** (Tencent, 2024) — Tencent Hunyuan license, very high quality. PyTorch only.
- **CRM (Convolutional Reconstruction Model)** (Tsinghua, 2024) — Apache-2.0, similar architecture to TripoSR. PyTorch only.

TripoSR remains the easiest single-image-to-mesh model to actually ship as ONNX, which is why it's the catalog entry point.
