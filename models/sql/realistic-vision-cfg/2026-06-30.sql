-- ============================================================================
-- Realistic Vision V6 + CFG — text-to-image, CreativeML OpenRAIL-M.
-- ============================================================================
--
-- Catalog id:  realistic-vision-cfg            (models/catalog.json)
-- ONNX files:  text_encoder/model.onnx, unet/model.onnx, vae_decoder/model.onnx
-- License:     CreativeML OpenRAIL-M
-- Upstream:    https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE
--              + stabilityai/sd-vae-ft-mse (paired VAE)
--
-- The QUALITY counterpart to realistic-vision-hyper. Same SD 1.5 base, but
-- this body exports the model **without** the Hyper-SD distillation LoRA and
-- drives it with classifier-free guidance (CFG) over a standard ~25-step
-- schedule. That recovers the prompt adherence, contrast, and clean skin /
-- tone that the 4-step Hyper variant trades away for speed — at the cost of
-- two UNet evaluations per step and roughly an order of magnitude more steps.
--
-- Differs from the Hyper bodies (see
-- models/sql/realistic-vision-hyper/2026-05-29.sql) in three places:
--   1. A negative_prompt is tokenized and encoded into a second embedding.
--   2. The UNet runs TWICE per step (conditional + unconditional); the two
--      noise predictions are combined as
--        noise_pred = uncond + guidance * (cond - uncond).
--   3. steps defaults to 25 (a normal, non-distilled denoising budget) and
--      guidance defaults to 7.5. The Hyper variant runs 4 steps at CFG=1.
--
-- **Pipeline (one image per row):**
--   1. tokenizer.encode_clip x2     — positive + negative prompt -> [1, 77].
--   2. infer('text_encoder') x2     — CLIP-L embeddings [1, 77, 768] for each.
--   3. sd_turbo_schedule(steps)     — standard SD scaled-linear beta schedule
--                                     with trailing timestep spacing. Valid for
--                                     non-distilled DDIM / Euler at any step
--                                     count (Karras spacing is a future option).
--   4. sample_normal + scale        — initial noisy latent [1, 4, 64, 64].
--   5. CFG Euler loop               — WHILE steps:
--                                       scaled  = latents / sqrt(sigma^2+1)
--                                       cond    = unet(scaled, t, pos_embeds)
--                                       uncond  = unet(scaled, t, neg_embeds)
--                                       pred    = uncond + g*(cond - uncond)
--                                       latents += (sigma_next - sigma) * pred
--   6. array_scale                  — latents /= 0.18215  (VAE scale).
--   7. infer('vae_decoder')         — [1, 4, 64, 64] -> [1, 3, 512, 512] in [-1, 1].
--   8. tensor_to_image_chw          — (value * 0.5 + 0.5) * 255 -> [0, 255] RGB.
-- ============================================================================

CREATE OR REPLACE MODEL realistic_vision_cfg(
  prompt String,
  negative_prompt String = ''
    COMMENT 'Things to steer away from — e.g. ''blotchy skin, deformed, lowres, oversaturated, extra fingers''. Realized through the classifier-free guidance term, so it only has effect when guidance > 1. Leave empty for a plain unconditional baseline.',
  steps  Int32 = 25
    CHECK (steps BETWEEN 1 AND 50)
    COMMENT 'Number of denoising steps. This is the non-distilled model, so it wants a normal budget: 20-30 is the quality sweet spot, fewer than ~15 starts to look unfinished. Cost is roughly linear in steps (each step runs the UNet twice for CFG).',
  guidance Float32 = 7.5::Float32
    CHECK (guidance BETWEEN 1.0::Float32 AND 20.0::Float32)
    COMMENT 'Classifier-free guidance scale. 1.0 disables CFG (output follows the prompt only weakly); 6-9 is the usual range for sharp, prompt-faithful results; above ~12 tends to oversaturate and harden edges. This is the knob the Hyper variant lacks.',
  seed   Int64 = NULL
    COMMENT 'Optional RNG seed for the initial latent noise. Leave unset (NULL) for a fresh random image on every call; pass a fixed integer to reproduce the same image for a given prompt, negative_prompt, steps, and guidance. Seeds the initial noise only, so output is not bit-identical to other diffusion tools and GPU runs may still vary slightly.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'realistic-vision-cfg/2026-06-30/text_encoder/model.onnx' AS text_encoder,
      'realistic-vision-cfg/2026-06-30/unet/model.onnx'         AS unet,
      'realistic-vision-cfg/2026-06-30/vae_decoder/model.onnx'  AS vae_decoder
AS BEGIN
  -- 1. Positive and negative conditioning, each CLIP-framed to length 77.
  DECLARE cond_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');
  DECLARE uncond_ids Int64[] = tokenizer.encode_clip(
    negative_prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  -- 2. Text encoder. [1, 77] Int64 -> [1, 77, 768] flat Float32, once each.
  DECLARE cond_embeds Float32[] = infer(
    'text_encoder', cond_ids, [1::Int32, 77::Int32]);
  DECLARE uncond_embeds Float32[] = infer(
    'text_encoder', uncond_ids, [1::Int32, 77::Int32]);

  -- 3. Standard SD schedule. sigmas length steps+1 (last = 0), timesteps
  -- length steps in [0, 999].
  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];

  -- 4. Initial noisy latent (4 channels x 64 x 64) scaled to sigma_max.
  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * 64 * 64, seed), sigmas[1]);

  -- 5. Classifier-free-guided Euler denoising loop.
  DECLARE i Int32 = 1;
  WHILE i <= steps
  BEGIN
    DECLARE sigma Float32 = sigmas[i];
    DECLARE sigma_next Float32 = sigmas[i + 1];
    DECLARE c_in Float32 = CAST(1.0 AS Float32)
      / sqrt(sigma * sigma + CAST(1.0 AS Float32));
    DECLARE scaled Float32[] = array_scale(latents, c_in);

    -- Conditional pass — the UNet sees the positive prompt.
    DECLARE noise_cond Float32[] = infer(
      'unet',
      { sample: scaled,
        timestep: timesteps[i],
        encoder_hidden_states: cond_embeds },
      { sample: [1::Int32, 4::Int32, 64::Int32, 64::Int32],
        encoder_hidden_states: [1::Int32, 77::Int32, 768::Int32] });

    -- Unconditional pass — the UNet sees the negative (or empty) prompt.
    DECLARE noise_uncond Float32[] = infer(
      'unet',
      { sample: scaled,
        timestep: timesteps[i],
        encoder_hidden_states: uncond_embeds },
      { sample: [1::Int32, 4::Int32, 64::Int32, 64::Int32],
        encoder_hidden_states: [1::Int32, 77::Int32, 768::Int32] });

    -- CFG combine: noise_pred = uncond + guidance * (cond - uncond).
    -- array_axpy(y, a, x) = y + a*x, so:
    --   diff = cond - uncond              = axpy(cond, -1, uncond)
    --   pred = uncond + guidance * diff   = axpy(uncond, guidance, diff)
    DECLARE diff Float32[] = array_axpy(
      noise_cond, CAST(-1.0 AS Float32), noise_uncond);
    DECLARE noise_pred Float32[] = array_axpy(
      noise_uncond, guidance, diff);

    -- Euler update: latents += (sigma_next - sigma) * noise_pred.
    SET latents = array_axpy(latents, sigma_next - sigma, noise_pred);
    SET i = i + 1
  END;

  -- 6. Scale latents for VAE decoding.
  SET latents = array_scale(latents, CAST(1.0 / 0.18215 AS Float32));

  -- 7. VAE decode: [1, 4, 64, 64] -> [1, 3, 512, 512] flat Float32 in [-1, 1].
  DECLARE rgb Float32[] = infer(
    'vae_decoder', latents,
    [1::Int32, 4::Int32, 64::Int32, 64::Int32]);

  -- 8. (value * 0.5 + 0.5) * 255 maps [-1, 1] -> [0, 255].
  RETURN tensor_to_image_chw(
    rgb, 512, 512,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)])
END
