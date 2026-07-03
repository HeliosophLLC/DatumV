# DatumV

[![CI](https://github.com/HeliosophLLC/DatumV/actions/workflows/ci.yml/badge.svg)]
[![Release](https://img.shields.io/github/v/release/HeliosophLLC/DatumV)]
[![License](https://img.shields.io/github/license/HeliosophLLC/DatumV)]

DatumV runs ML models on your data — locally, batched, no Python. You describe what you want in SQL, and the engine handles inference, batching, calibration, and I/O across dozens of vision, audio, and text models from a built-in catalog.

![Search for people](docs/figures/search_person.jpg)

```sql
SELECT
    LET classes = models.yolox_s(a.file),
    image_crop(file, c.value.bbox)
FROM datasets.coco_val2017 a
CROSS JOIN unnest(classes) c
WHERE c.value.label = 'person'
LIMIT 100
```

See [Examples — Person crops with YOLOX](docs/examples/yolox-person-crops.md) for the full walkthrough.

## Same input, four depth models

![Depth model comparison](docs/figures/depth_comparison.jpg)

```sql
SELECT
    LET depth_anything_v2 = models.depth_anything_v2_base(file) AS DAv2,
    LET depth_anything_v3 = models.depth_anything_v3_large(file) AS DAv3,
    LET midas = models.midas_small(file) AS midas,
    LET dpt = models.dpt_large(file) AS dpt,
    file AS baseline,
    file_name
FROM datasets.coco_val2017
LIMIT 32
```

See [Examples — Depth model comparison](docs/examples/depth-comparison.md) for the full walkthrough.

## Compare text-to-image models

![Compare text-to-image models](docs/figures/compare_images.jpg)

```sql
DECLARE prompt String = 'A grand stone castle, blue skys, natural light, realistic'

SELECT
    g.value
    ,models.sd_turbo(prompt) "sd"
    ,models.sdxl_turbo(prompt) "sdxl"
    ,models.epicrealism_hyper(prompt) "epicrealism"
    ,models.absolute_reality_hyper(prompt) "absolute_reality"
    ,models.dreamshaper_hyper(prompt) "dreamshaper"
FROM generate_series(1, 10) g
```

See [Examples — Five text-to-image models, one prompt](docs/examples/compare-images.md) for the full walkthrough.

## Installing

Download the [latest release](https://github.com/HeliosophLLC/DatumV/releases/latest) and pick the build that matches your hardware:

| Hardware | Windows | Linux (x86_64) |
|---|---|---|
| NVIDIA GPU | `DatumV-X.Y.Z-cuda-setup.exe` | `DatumV-X.Y.Z-cuda.AppImage` |
| AMD / Intel GPU, or no discrete GPU | `DatumV-X.Y.Z-setup.exe` | `DatumV-X.Y.Z.AppImage` |

The **cuda** variants enable NVIDIA CUDA acceleration. On first launch (when an NVIDIA GPU is detected), the app prompts to download ~1.5 GB of NVIDIA runtime libraries to your app-data folder; subsequent launches use the cached runtime. Requires NVIDIA driver ≥ 525.60 and a GPU with compute capability 7.0 or newer (Volta or later, see the compatibility matrix below).

The **standard** variants use DirectML on Windows and Vulkan on Linux for cross-vendor hardware acceleration. They work on virtually any GPU made in the last decade, including integrated graphics.

Windows users who don't want an installer can grab `DatumV-X.Y.Z-portable.exe` (self-extracting single file) or `DatumV-X.Y.Z.zip` (raw archive) from the same Release page. These don't create Start Menu entries and can be deleted to remove.

### System requirements

- 8 GB RAM minimum, 16 GB recommended (more for larger LLMs)
- ~5 GB free disk for the app; significantly more for downloaded models and datasets
- Catalog content is stored separately from the app itself, under `%LOCALAPPDATA%\Heliosoph.DatumV\` on Windows and `~/.local/share/Heliosoph.DatumV/` on Linux
- Windows 10/11 (x64) or a recent x86_64 Linux distribution

### GPU acceleration

DatumV uses different acceleration paths depending on your hardware and OS. The picture isn't symmetric across platforms — Linux has fewer GPU options than Windows for non-NVIDIA hardware.

**ONNX models** (vision, audio, embeddings, classifiers — everything dispatched via `models.X(...)`):

| Hardware | Windows (standard) | Windows (cuda) | Linux (standard) | Linux (cuda) |
|---|---|---|---|---|
| NVIDIA Turing+ (RTX 20-series and newer, GTX 16-series) | DirectML | CUDA | CPU | CUDA |
| NVIDIA Pascal / Maxwell (GTX 10-series, 9-series) | DirectML | not recommended* | CPU | not recommended* |
| NVIDIA Kepler and older (GTX 7-series, 800M-series) | CPU† | not supported | CPU | not supported |
| AMD Radeon (GCN 1.2+) | DirectML | — | CPU | — |
| Intel Arc / iGPU (Skylake or newer) | DirectML | — | CPU | — |

*The cuda variant detects Pascal/Maxwell and surfaces a "download Standard installer instead" warning. cuDNN 9's precompiled kernels for these architectures are incomplete, so Conv operations fail at runtime.
†Some Kepler-era GPUs are rejected by DirectML's D3D12 device-interface requirements and fall back to CPU; DatumV's Settings → GPU panel will say so explicitly.

**LLM models** (chat, generative text via LLamaSharp):

| Hardware | Windows & Linux |
|---|---|
| NVIDIA Turing+ | CUDA (cuda variant) or Vulkan (standard variant) |
| Any other Vulkan-capable GPU (most GPUs since ~2014, including iGPUs) | Vulkan |
| No usable GPU | CPU |

In short: LLM inference accelerates on a much broader range of hardware than ONNX inference, because the Vulkan path doesn't depend on platform-specific runtime libraries the way DirectML / CUDA do.

**Hybrid laptops (NVIDIA Optimus / AMD Switchable Graphics)**: on laptops with both integrated and discrete GPUs, DatumV uses whichever GPU your OS exposes by default — typically the integrated one. To force the discrete GPU:

- **Windows**: Settings → System → Display → Graphics → Browse → pick the DatumV executable → set to **High performance**, then relaunch.
- **Linux**: prefix the launch with `DRI_PRIME=1` (open-source drivers) or `__NV_PRIME_RENDER_OFFLOAD=1 __GLX_VENDOR_LIBRARY_NAME=nvidia` (NVIDIA proprietary driver).

For small models, the integrated GPU is usually fine. For 7B+ LLMs, the discrete GPU's dedicated VRAM tends to win.

### First-launch warnings (unsigned installers)

DatumV v0.1 installers are **unsigned**. We plan to add code signing in a future release; until then:

- **Windows**: SmartScreen warns *"Windows protected your PC"* on first run. Click **More info** → **Run anyway**.
- **Linux**: no warning — `chmod +x DatumV-*.AppImage` and double-click or run from the terminal.

## Why DatumV?

- **Local-first.** Your data and models live on your machine. No data leaves the host.
- **Offline.** No internet connection required for queries, inference, or workflow execution after install.
- **No Python.** No `pip install`, no virtualenv, no CUDA-version mismatches.
- **Batched by default.** Inference is a column operation across thousands of rows. No manual `DataLoader` wiring.
- **Queryable.** Filter, join, group, and rank on model outputs without writing glue code.
- **Inspectable.** Every intermediate is a row in a table you can `SELECT` from.

## The catalog

DatumV ships with a built-in catalog spanning object detection, segmentation, classification, depth estimation, OCR, captioning, embeddings, text-to-speech, image generation, and LLMs — including MobileSAM, the YOLOX family, Stable Diffusion variants, all-MiniLM-L6-v2, Florence-2, PaddleOCR, MiDaS, DPT, U²-Net, and Bark.

You can add your own ONNX models with `CREATE MODEL`. See [docs/models.md](docs/models.md) for the conceptual surface (dispatch, output shapes, lifecycle) and [docs/sql/create-model.md](docs/sql/create-model.md) for adding new ones.

## Documentation

| | |
|--|--|
| [Getting Started](docs/getting-started.md) | Install, your first query, the load → transform → export pipeline |
| [Examples](docs/examples/index.md) | Things you can do |
| [SQL Reference](docs/sql/select.md) | SELECT, JOIN, WHERE, GROUP BY, window functions, type system, DDL/DML |
| [Functions](docs/functions/string.md) | 200+ built-in functions across math, string, image, vector, temporal, JSON |
| [Models](docs/models.md) | How `models.X(...)` works — dispatch, output shapes, lifecycle, adding your own |
| [Engine internals](docs/technical/architecture.md) | Architecture, file format, indexes, execution plans, C# API |

## Building from source

```bash
git clone https://github.com/Heliosoph/DatumV.git
cd DatumV
dotnet build
dotnet test
```

## Built with

**DatumV is built with Llama.** Use of Llama models is subject to Meta's [Llama Community License](https://llama.meta.com/llama3_1/license/) and [Acceptable Use Policy](https://llama.meta.com/llama3/use-policy/).

DatumV's catalog references third-party models — Llama, Stable Diffusion, MiDaS, YOLOX, the Florence family, MobileSAM, U²-Net, Bark, and others — that are downloaded from their publishers at install time. Each model retains its upstream license; you can review the license for any model before downloading via the install dialog, or browse the bundled set in [licenses/](licenses/).

DatumV is built on:

- [Llama](https://llama.meta.com/) and [LLamaSharp](https://github.com/SciSharp/LLamaSharp) — LLM inference
- [ONNX Runtime](https://onnxruntime.ai/) — vision, audio, and embedding model inference
- [FFmpeg](https://ffmpeg.org/) — media decoding
- [Apache Arrow](https://arrow.apache.org/) — columnar in-memory format
- [Electron](https://www.electronjs.org/) — desktop shell
- [.NET 10](https://dotnet.microsoft.com/) — engine runtime

## License

DatumV's source code, the Electron shell, and the catalog manifests are MIT-licensed. Third-party models referenced from the catalog are subject to their own upstream licenses (see [Built with](#built-with) above) and are not redistributed by this project.

---

DatumV™ is a trademark of Heliosoph LLC.

*Datum: a fact known or assumed as the basis for reasoning or calculation. A premise. A given — the atomic unit of information from which inferences are drawn.*