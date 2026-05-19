---
license: mit
library_name: onnx
tags:
  - ocr
  - text-recognition
  - trocr
  - documents
  - transformer
  - onnx
base_model: microsoft/trocr-base-printed
pipeline_tag: image-to-text
---

# TrOCR Base Printed (ONNX, fp32 + fp16 bundle)

ONNX exports of [microsoft/trocr-base-printed](https://huggingface.co/microsoft/trocr-base-printed) — Microsoft's Transformer-based OCR for printed text. ViT image encoder + GPT-style autoregressive text decoder, trained on SROIE printed-text crops. Recognizes the text in a cropped image of a single line/word.

This repo bundles **both fp32 and fp16 precisions** in one download — distribution symmetry, shared tokenizer + config files. Pick a precision via the `.onnx` filename in the `onnx/` subdir.

Re-exported from upstream PyTorch weights via a two-step pipeline. Provenance trail: Li et al. → microsoft/trocr-base-printed → `optimum-cli export onnx --task image-to-text-with-past` (fp32 stage) → `onnxconverter_common.float16.convert_float_to_float16(..., keep_io_types=True)` (fp16 cast on the fp32 graph) → these files.

Toolchain: `torch 2.4.x` (CUDA 12.4), `transformers 4.45.2`, `optimum[onnxruntime] 1.24.0`, `onnxconverter-common>=1.14`. Full conversion script: [`scripts/export-trocr-base-printed-fp16.ps1`](https://github.com/HeliosophLLC/Heliosoph/blob/main/scripts/export-trocr-base-printed-fp16.ps1) in the Heliosoph repo (despite the `-fp16` suffix on the script name, it produces both precisions in one run).

Why the two-step pipeline instead of `optimum-cli ... --dtype fp16` directly: the CUDA path requires a CUDA-enabled torch build in the venv (the other export scripts don't install one), and optimum-cli's fp16 merged-decoder export ships with an If-subgraph wiring bug on this architecture. The onnxconverter-common pass operates on the already-traced fp32 graph in place and sidesteps both issues. `keep_io_types=True` means inputs and outputs stay fp32 at the wire boundary — only internal weights + activations run in half precision — so runtime code feeds the same input tensors regardless of which `.onnx` file it loads.

Credit: Minghao Li, Tengchao Lv, Lei Cui, Yijuan Lu, Dinei Florencio, Cha Zhang, Zhoujun Li, Furu Wei (Microsoft Research). Paper: *"TrOCR: Transformer-based Optical Character Recognition with Pre-trained Models"*, 2021.

## What this repo contains

TrOCR is encoder-decoder, so the export splits into multiple files. **All shared (root) files must be present** along with the precision-specific `.onnx` files you choose to load.

### `onnx/` subdir — precision-specific files

| File | Variant | Size | Role |
|---|---|---|---|
| `encoder_model.onnx` | fp32 | ~700 MB | ViT image encoder |
| `encoder_model_fp16.onnx` | fp16 | ~350 MB | Half-precision ViT image encoder |
| `decoder_model_merged.onnx` | fp32 | ~700 MB | Text decoder with KV cache merged into one graph |
| `decoder_model_merged_fp16.onnx` | fp16 | ~350 MB | Half-precision text decoder |

The non-merged `decoder_model.onnx` is deliberately omitted — the merged form supersedes it for runtime use; keeping both would double the repo size for no benefit.

### Root — shared tokenizer + config files

| File | Role |
|---|---|
| `config.json` | Model architecture config |
| `generation_config.json` | Decoder generation defaults (max_length, EOS token, etc.) |
| `preprocessor_config.json` | Image preprocessing — `TrOCRProcessor` settings (resize, normalize) |
| `tokenizer.json` + `vocab.json` + `merges.txt` | BPE tokenizer files |
| `tokenizer_config.json` + `special_tokens_map.json` | Tokenizer metadata |

## Input / output (both variants)

| Stage | Input | Output |
|---|---|---|
| Encoder | `pixel_values` — NCHW **float32** (yes, even for the fp16 variant — IO types are kept fp32), preprocessed RGB image (typically 384×384) | `last_hidden_state` — encoder features (fp32 at the boundary; fp16 internally for the half-precision variant) |
| Decoder | `input_ids` (token sequence so far) + `encoder_hidden_states` + KV cache from prior step | next-token logits + updated KV cache |

The fp16 variant's `keep_io_types=True` setting means runtime code is **identical** between fp32 and fp16 — you don't have to cast inputs to `np.float16`. Only the on-disk weights and the internal compute differ.

## How to use

Greedy decoding orchestrated outside the ONNX graph — same encoder-decoder shape as Whisper, T5, BART, and friends:

```python
import onnxruntime as ort
import numpy as np
from PIL import Image
from transformers import TrOCRProcessor

# Pick a precision — same runtime code either way thanks to keep_io_types.
PRECISION_SUFFIX = ""          # "" for fp32, "_fp16" for fp16

proc      = TrOCRProcessor.from_pretrained(".")
encoder   = ort.InferenceSession(f"onnx/encoder_model{PRECISION_SUFFIX}.onnx")
decoder   = ort.InferenceSession(f"onnx/decoder_model_merged{PRECISION_SUFFIX}.onnx")

img = Image.open("cropped_text_line.jpg").convert("RGB")
pixel_values = proc(images=img, return_tensors="np").pixel_values  # float32 in both cases
encoder_hidden = encoder.run(None, {"pixel_values": pixel_values})[0]

BOS = proc.tokenizer.cls_token_id
EOS = proc.tokenizer.eos_token_id
input_ids = np.array([[BOS]], dtype=np.int64)
generated, past_kv = [], None

for _ in range(64):
    decoder_inputs = {"input_ids": input_ids, "encoder_hidden_states": encoder_hidden}
    if past_kv is not None:
        decoder_inputs.update(past_kv_to_inputs(past_kv))
    outputs = decoder.run(None, decoder_inputs)
    next_token = outputs[0][:, -1, :].argmax(-1)
    if next_token.item() == EOS: break
    generated.append(next_token.item())
    input_ids = next_token.reshape(1, 1)
    past_kv = outputs_to_past_kv(outputs[1:])

text = proc.tokenizer.decode(generated, skip_special_tokens=True)
```

The exact past-KV input/output names are Optimum-version-specific; inspect with Netron once after export to pin them down.

## Which precision should I use?

- **fp32** — full precision, identical numerics to upstream PyTorch reference. Default for accuracy-sensitive scientific work, OCR-accuracy benchmarks.
- **fp16** — ~half the disk footprint (~700 MB vs ~1.4 GB total) and half the model-load memory. On GPU / NPU with native fp16: modest speedup (typically 1.5-2× over fp32 on consumer GPUs). On CPU runtimes that upcast fp16 → fp32 internally, runtime speed is identical to fp32 but you save the memory.

The `keep_io_types=True` setting means switching between them is a single file-path change — no code changes needed.

## Related variants (not in this repo)

Microsoft publishes a small variant family of TrOCR — same architecture, different size / training corpus:

- `microsoft/trocr-small-printed` — smaller (~5× less), less accurate but faster.
- `microsoft/trocr-large-printed` — bigger, better quality.
- `microsoft/trocr-base-handwritten` — same size as this, trained on IAM handwritten dataset instead of SROIE.

All MIT-licensed, all re-exportable via the same script with a swapped `--model` arg.

## License

**MIT** — same as upstream [microsoft/trocr-base-printed](https://huggingface.co/microsoft/trocr-base-printed). `LICENSE` file included. Optimum ONNX export + fp16 conversion are numerical transformations only — no relicensing implication.
