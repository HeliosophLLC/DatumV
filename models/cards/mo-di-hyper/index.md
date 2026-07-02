# Mo-Di + Hyper-SD (4-step)

Nitrosocke's Mo-Di (Modern Disney) Diffusion — a Stable Diffusion 1.5
fine-tune that renders in a **modern Disney / Pixar 3D-animation style**
— distilled to **4 sampling steps** with ByteDance's Hyper-SD LoRA.
Reach for it for stylized character art and animated-movie looks.

One SQL-visible model ships: `mo_di_hyper`. It takes a text `prompt`
(and an optional `steps` count) and returns a 512×512 `Image`. It's a
true text-to-image model — no input image, no dataset; you describe the
scene and it renders it.

> **Activator phrase required.** This fine-tune was trained with the
> trigger **`modern disney style`** — include it in your prompt or the
> style won't reliably show up. It is *not* added for you.

This is a GPU model: it wants ~10 GB of VRAM and CUDA for usable speed.

> **What to expect from this model.** This is a **speed-first** text-to-image
> model built for fast, batched generation *inside SQL* — not a replacement
> for a dedicated image-generation app like Midjourney or a full Stable
> Diffusion UI. It also renders in a fixed stylized (Modern Disney) look, not
> photorealism. Two deliberate trade-offs buy that speed at the cost of
> per-image polish:
>
> - **4-step distillation.** ByteDance's Hyper-SD LoRA compresses the usual
>   ~30 denoising steps into 4. A big speed win, but it reduces fine detail
>   and output variety — you'll often get a similar face or composition
>   across different seeds.
> - **No classifier-free guidance (CFG) and no negative prompt.** The
>   pipeline runs the model once per step on your prompt alone. CFG (the
>   "guidance scale" ≈ 7 knob in other tools) and a negative prompt are what
>   give polished renders their strong prompt adherence, contrast, and clean
>   tone. Without them, output is softer and follows the prompt more loosely.
>
> The eye-catching examples you'll find online for the *same base model* are
> almost always the **full, non-distilled** model run with CFG, a negative
> prompt, and 20–50 steps — a slower, higher-fidelity path. These Hyper
> variants intentionally optimize for throughput and quick iteration across
> many rows instead. Match that setup before comparing output side by side.

## Example SQL

Generate a single image — note the activator phrase in the prompt:

```sql
SELECT models.mo_di_hyper(
    'a brave young knight holding a sword, modern disney style'
) AS image;
```

Output:

![a brave young knight holding a sword, modern disney style](query.jpg)

Generate several prompts in one query (each carries the activator):

```sql
WITH prompts AS (
  SELECT 'a cheerful baker mouse in an apron, modern disney style' AS prompt
  UNION ALL SELECT 'an old wizard with a long beard and twinkling eyes, modern disney style'
  UNION ALL SELECT 'a curious robot exploring a jungle, modern disney style'
)
SELECT prompt, models.mo_di_hyper(prompt) AS image
FROM prompts;
```

Output:

![Generate several prompts in one query (each carries the activator)](query2.jpg)

Trade quality for speed with the `steps` argument (1 is fastest, 4 is
the recommended minimum for detail quality):

```sql
SELECT models.mo_di_hyper(
    'a friendly dragon hatchling, modern disney style', 2
) AS preview;
```

## Output shape

Returns a single 512×512 `Image`. There is no batch dimension — one call
produces one picture.

## Tips

- **Always include `modern disney style`.** It's the trained trigger;
  without it you get a generic SD 1.5 render. Put it near the end of the
  prompt after the subject description.
- **4 steps is the sweet spot.** Hyper-SD was distilled for 1–4 steps;
  `steps` is capped at 8 and anything past 4 returns diminishing gains.
  Drop to 1–2 for fast previews, back to 4 for final renders.
- **Prompts are CLIP-limited to 77 tokens.** Roughly 50–60 words — and
  the activator phrase counts against that budget, so keep descriptions
  tight.
- **Reproducible with a seed; random without one.** Leave `seed` unset and
  each call samples fresh noise, so the same prompt yields a different image
  every time. Pass an integer `seed` to lock the initial noise and get the
  same image back for a given prompt and `steps` — handy once you land on a
  composition you like. The seed fixes this engine's noise only: results
  won't match other diffusion tools bit-for-bit, and GPU runs can still
  drift slightly.
- **No negative prompt in v1.** Steer entirely through the positive
  prompt; the classic `negative_prompt` channel isn't wired yet.

## License & attribution

CreativeML OpenRAIL-M — usable commercially, with use-based restrictions
(see the license). Fine-tune by Nitrosocke; 4-step distillation via
ByteDance's Hyper-SD LoRA; built on CompVis / Stability AI's Stable
Diffusion 1.5.

- Base fine-tune: [nitrosocke/mo-di-diffusion](https://huggingface.co/nitrosocke/mo-di-diffusion)
- Distillation: [ByteDance/Hyper-SD](https://huggingface.co/ByteDance/Hyper-SD) — [paper](https://arxiv.org/abs/2404.13686)
- ONNX export: [Heliosoph/mo-di-hyper-onnx](https://huggingface.co/Heliosoph/mo-di-hyper-onnx)
