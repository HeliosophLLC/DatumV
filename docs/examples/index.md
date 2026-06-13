---
title: Examples
---

A growing collection of interesting things you can do in DatumV. Each example is a single self-contained query — paste it into a new SQL tab and it runs.

The examples here lean toward showing how the engine composes: model invocation inside `SELECT`, structured outputs queried directly, multiple models in one query, typed media as a first-class column. They're not benchmarks and they're not exhaustive tutorials — they're the shortest queries that get a particular point across.

## Vision and model comparisons

- [Person crops with YOLOX](yolox-person-crops.md) — object detection inside `SELECT`, cropping by detection bounding box.
- [Same input, four depth models](depth-comparison.md) — Depth Anything v2 and v3, MiDaS, and DPT side by side.
- [Depth maps and 3D point clouds](depth-maps-and-point-clouds.md) — a full pipeline from photos to renderable meshes.

## Media

- [Video frames as a queryable column](video-frames.md) — lazy frame extraction, per-frame inference.
- [Transcribing audio with Whisper](transcription.md) — speech-to-text over an audio dataset in one column.

## Language details

- [Multi-dim arrays and bracket indexing](multi-dim-arrays.md) — addressing into multi-dim tensors directly from SQL.

## Procedural graphics

- [Animated torch](animated-torch.md) — a particle system rendered to GIF.
- [Audio waveforms as procedural drawings](audio-waveform.md) — render a gradient waveform per clip with a per-sample drawing lambda.
