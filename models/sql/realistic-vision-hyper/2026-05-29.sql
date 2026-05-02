-- ============================================================================
-- Realistic Vision V6 + Hyper-SD — text-to-image, CreativeML OpenRAIL-M.
-- ============================================================================
--
-- Catalog id:  realistic-vision-hyper          (models/catalog.json)
-- ONNX files:  text_encoder/model.onnx, unet/model.onnx, vae_decoder/model.onnx
-- License:     CreativeML OpenRAIL-M
-- Upstream:    https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE
--              + ByteDance/Hyper-SD (4-step distillation LoRA, fused in)
--
-- SD 1.5 fine-tune distilled for 1-4 Euler steps. Sharper portraits / skin
-- texture than the other Hyper variants; more NSFW-permissive base.
-- Pipeline shape is identical to every other SD 1.x variant — the body
-- below is the canonical SD-1.x diffusion implementation; sibling
-- variants (dreamshaper-hyper, epicrealism-hyper, mo-di-hyper,
-- openjourney-hyper, absolute-reality-hyper) differ only in the
-- USING paths.
--
-- **Pipeline (one image per row, ~1-2s on a consumer GPU):**
--   1. tokenizer.encode_clip      — lowercase + BPE + [BOS, ids, EOS, ..., EOS]
--                                   padded to 77 tokens (CLIP convention).
--   2. infer('text_encoder')      — [1, 77] Int64 -> [1, 77, 768] Float32.
--                                   CLIP-L embeddings.
--   3. sd_turbo_schedule          — Trailing-spaced training schedule.
--                                   Returns Struct{sigmas, timesteps}.
--                                   NOT Karras spacing — distilled models
--                                   require the training noise levels.
--   4. sample_normal + scale      — Initial noisy latent [1, 4, 64, 64]
--                                   scaled by sigma_max.
--   5. Euler denoising loop       — WHILE steps:
--                                     scaled  = latents / sqrt(sigma^2+1)
--                                     noise_pred = unet(scaled, t, embeds)
--                                     latents += (sigma_next - sigma) * noise_pred
--   6. array_scale                — latents /= 0.18215  (SD 2.x VAE scale).
--   7. infer('vae_decoder')       — [1, 4, 64, 64] -> [1, 3, 512, 512] in [-1, 1].
--   8. tensor_to_image_chw        — (value * 0.5 + 0.5) * 255 maps [-1, 1]
--                                   -> [0, 255] RGB.
-- ============================================================================

CREATE OR REPLACE MODEL realistic_vision_hyper(
  prompt String,
  steps  Int32 = 4
    CHECK (steps BETWEEN 1 AND 8)
    COMMENT 'Number of Euler denoising steps. Hyper-SD was distilled for 1-4 steps; 1 is fastest, 4 is the recommended minimum for face / detail quality, beyond 4 returns diminishing gains.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'realistic-vision-hyper/2026-05-29/text_encoder/model.onnx' AS text_encoder,
      'realistic-vision-hyper/unet/model.onnx'         AS unet,
      'realistic-vision-hyper/vae_decoder/model.onnx'  AS vae_decoder
AS BEGIN
  -- 1. CLIP-frame [BOS, ...ids, EOS, EOS, ..., EOS] of length 77.
  DECLARE input_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  -- 2. Text encoder. [1, 77] Int64 -> [1, 77, 768] flat Float32.
  DECLARE text_embeds Float32[] = infer(
    'text_encoder', input_ids, [1::Int32, 77::Int32]);

  -- 3. Distillation-faithful schedule. sigmas has length steps+1
  -- (sigmas[steps+1] = 0), timesteps has length steps in [0, 999].
  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];

  -- 4. Initial noisy latent (4 channels x 64 x 64) scaled to sigma_max.
  -- SQL arrays are 1-indexed so sigmas[1] is the first (largest) sigma.
  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * 64 * 64), sigmas[1]);

  -- 5. Euler denoising loop.
  DECLARE i Int32 = 1;
  WHILE i <= steps
  BEGIN
    DECLARE sigma Float32 = sigmas[i];
    DECLARE sigma_next Float32 = sigmas[i + 1];
    DECLARE c_in Float32 = CAST(1.0 AS Float32)
      / sqrt(sigma * sigma + CAST(1.0 AS Float32));
    DECLARE scaled Float32[] = array_scale(latents, c_in);

    DECLARE noise_pred Float32[] = infer(
      'unet',
      { sample: scaled,
        timestep: timesteps[i],
        encoder_hidden_states: text_embeds },
      { sample: [1::Int32, 4::Int32, 64::Int32, 64::Int32],
        encoder_hidden_states: [1::Int32, 77::Int32, 768::Int32] });

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
