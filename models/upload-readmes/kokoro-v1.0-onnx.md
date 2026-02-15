---
license: apache-2.0
library_name: onnx
tags:
  - text-to-speech
  - tts
  - kokoro
  - onnx
  - english
base_model: hexgrad/Kokoro-82M
pipeline_tag: text-to-speech
---

# Kokoro v1.0 — ONNX + Voice Bundle

[Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) is an 82M-parameter text-to-speech model that punches well above its weight class. This repo bundles the ONNX checkpoint with the **11 official voice embeddings** so users can download once and have the full vocal range ready.

Not converted locally — Kokoro publishes ONNX as its primary distribution format. This is a curated re-host with the voices co-located for convenience.

Credit: hexgrad (Kokoro).

## What this repo contains

```
kokoro-v1.0.onnx              # 311 MB — the TTS model
kokoro-voices/
  af.bin                      # default American Female (alias for af_bella)
  af_bella.bin
  af_nicole.bin
  af_sarah.bin
  af_sky.bin
  am_adam.bin                 # American Male
  am_michael.bin
  bf_emma.bin                 # British Female
  bf_isabella.bin
  bm_george.bin                # British Male
  bm_lewis.bin
```

Total: ~320 MB.

Voice naming convention: `{a,b}{f,m}_<name>.bin` where `a` = American, `b` = British, `f` = female, `m` = male.

## How to use

Kokoro takes a tokenized phoneme sequence as input. You'll typically pair it with a phonemizer library (the upstream Python uses `phonemizer` + `espeak-ng`).

```python
import onnxruntime as ort
import numpy as np

sess = ort.InferenceSession("kokoro-v1.0.onnx")
voice = np.fromfile("kokoro-voices/af_bella.bin", dtype=np.float32).reshape(-1, 1, 256)

# tokens: int64 array of phoneme IDs from your phonemizer
# speed: float in [0.5, 2.0], 1.0 = natural
audio = sess.run(None, {
    "tokens":  tokens,
    "style":   voice[len(tokens)],   # voice embedding indexed by token length
    "speed":   np.array([1.0], dtype=np.float32),
})[0]   # → float32 waveform at 24 kHz
```

See [hexgrad/Kokoro-82M](https://huggingface.co/hexgrad/Kokoro-82M) for the canonical reference implementation.

## License

**Apache-2.0** — same as upstream. `LICENSE` file included.
