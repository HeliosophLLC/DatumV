-- ============================================================================
-- Juggernaut XL Lightning — text-to-image, CreativeML OpenRAIL-PP-M.
-- ============================================================================
--
-- Catalog id:  juggernaut-xl-lightning         (models/catalog.json)
-- License:     CreativeML OpenRAIL-PP-M
-- Upstream:    https://huggingface.co/RunDiffusion/Juggernaut-XL-Lightning
--              (SDXL base + RunDiffusion Juggernaut fine-tune + ByteDance Lightning distillation)
--
-- 1024×1024 SDXL-class text-to-image distilled to 4 Euler steps. Differs
-- from the SD 1.x Hyper variants in several load-bearing ways:
--   1. Two text encoders (CLIP-L 768 + OpenCLIP-G 1280); UNet
--      cross-attention takes them concatenated along the hidden dim
--      ([1, 77, 768] + [1, 77, 1280] -> [1, 77, 2048]).
--   2. Encoder 2 also emits a pooled embedding [1, 1280] consumed via
--      the UNet's `text_embeds` added_cond_kwarg.
--   3. UNet takes a `time_ids` [1, 6] added_cond_kwarg encoding
--      [orig_h, orig_w, crop_top, crop_left, target_h, target_w].
--   4. fp16 UNet: text-encoder outputs are Float32 from the SQL side; we
--      clamp to ±65504 before feeding so they don't become ±Inf on cast
--      and NaN out attention softmax.
--   5. 128×128 latent (vs SD 1.x's 64×64); 1024×1024 RGB output.
--   6. VAE scale = 0.13025 (vs SD 2.x's 0.18215 — getting it wrong washes
--      the colours).
-- The denoising schedule, CLIP framing, sample_normal noise, Euler update,
-- and tensor->image conversion are shared with the SD 1.x bodies.
--
-- Classifier-free guidance: Lightning's reference guidance scale is ~1.8, so
-- whenever cfg > 1 the loop runs an unconditional ("") UNet pass alongside the
-- conditional one and extrapolates: pred = uncond + cfg * (cond - uncond).
-- At cfg <= 1 it short-circuits to a single conditional pass (matching
-- diffusers' do_classifier_free_guidance gate). This is the opposite of the
-- ADD/Turbo bodies, which must stay at cfg = 0 (no guidance at all).
-- ============================================================================

CREATE OR REPLACE MODEL juggernaut_xl_lightning(
  prompt String,
  steps  Int32 = 4
    CHECK (steps BETWEEN 1 AND 8)
    COMMENT 'Number of Euler denoising steps. Lightning was distilled for 1-8 steps; 1 is fastest, 4 is the recommended minimum for face / fine-detail quality, 8 for hero outputs.',
  seed   Int64 = NULL
    COMMENT 'Optional RNG seed for the initial latent noise. Leave unset (NULL) for a fresh random image on every call; pass a fixed integer to reproduce the same image for a given prompt and steps. Seeds the initial noise only, so output is not bit-identical to other diffusion tools and GPU runs may still vary slightly.',
  cfg    Float32 = 1.8
    CHECK (cfg BETWEEN 0.0 AND 15.0)
    COMMENT 'Classifier-free guidance scale. The Juggernaut XL Lightning reference is ~1.5-2.0; higher tightens prompt adherence and contrast but over-burns past ~3 on a Lightning distillation. cfg > 1 runs a second (unconditional) UNet pass per step and extrapolates, roughly doubling per-step cost; cfg <= 1 short-circuits to a single conditional pass — fastest, with no guidance.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'juggernaut-xl-lightning/2026-05-29/text_encoder/model.onnx'   AS text_encoder_1,
      'juggernaut-xl-lightning/2026-05-29/text_encoder_2/model.onnx' AS text_encoder_2,
      'juggernaut-xl-lightning/2026-05-29/unet/model.onnx'           AS unet,
      'juggernaut-xl-lightning/2026-05-29/vae_decoder/model.onnx'    AS vae_decoder
AS BEGIN
  -- 1. CLIP-frame input ids — both encoders consume the same 77-token sequence.
  DECLARE input_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  -- 2. Text encoder 1 (CLIP-L 768) -> [1, 77, 768] hidden states (flat Float32).
  DECLARE hidden_1 Float32[] = infer(
    'text_encoder_1', input_ids, [1::Int32, 77::Int32]);

  -- 3. Text encoder 2 (OpenCLIP-G 1280) -> {last_hidden_state: [1, 77, 1280],
  --    text_embeds: [1, 1280]}. Both outputs are needed downstream.
  DECLARE outputs_2 Struct = infer_outputs(
    'text_encoder_2', input_ids, [1::Int32, 77::Int32]);
  DECLARE hidden_2 Float32[] = outputs_2['last_hidden_state'];
  DECLARE pooled   Float32[] = outputs_2['text_embeds'];

  -- 4. Concat along the hidden dim per token: [1, 77, 768+1280=2048].
  DECLARE combined_embeds Float32[] = array_concat_last_dim(
    hidden_1, 768::Int32, hidden_2, 1280::Int32);

  -- 5. Clamp to fp16 representable range so the cast inside the UNet's input
  --    binding doesn't produce ±Inf (which would cascade to NaN through
  --    attention softmax + group norm).
  SET combined_embeds = array_clamp(
    combined_embeds, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));
  SET pooled = array_clamp(
    pooled, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));

  -- 5b. Unconditional ("") embeddings for classifier-free guidance, encoded
  --     once up front by the same two-encoder path. The loop consumes them only
  --     when cfg > 1; we compute them unconditionally because DECLARE scoping
  --     would otherwise hide them from the loop, and the two text-encoder passes
  --     are cheap next to the per-step UNet.
  DECLARE uncond_ids Int64[] = tokenizer.encode_clip(
    '', '../tokenizer/vocab.json', '../tokenizer/merges.txt');
  DECLARE uncond_hidden_1 Float32[] = infer(
    'text_encoder_1', uncond_ids, [1::Int32, 77::Int32]);
  DECLARE uncond_outputs_2 Struct = infer_outputs(
    'text_encoder_2', uncond_ids, [1::Int32, 77::Int32]);
  DECLARE uncond_hidden_2 Float32[] = uncond_outputs_2['last_hidden_state'];
  DECLARE uncond_pooled   Float32[] = uncond_outputs_2['text_embeds'];
  DECLARE uncond_embeds   Float32[] = array_concat_last_dim(
    uncond_hidden_1, 768::Int32, uncond_hidden_2, 1280::Int32);
  SET uncond_embeds = array_clamp(
    uncond_embeds, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));
  SET uncond_pooled = array_clamp(
    uncond_pooled, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));

  -- 6. SDXL's added_cond_kwargs.time_ids — micro-conditioning declaring
  --    the original + target resolution and any crop offsets.
  DECLARE time_ids Float32[] = [
    CAST(1024.0 AS Float32), CAST(1024.0 AS Float32),
    CAST(0.0    AS Float32), CAST(0.0    AS Float32),
    CAST(1024.0 AS Float32), CAST(1024.0 AS Float32)];

  -- 7. Distillation-faithful schedule + initial noisy latent (4 channels x 128 x 128).
  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];
  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * 128 * 128, seed), sigmas[1]);

  -- 8. Euler denoising loop. UNet has 5 named inputs.
  DECLARE i Int32 = 1;
  WHILE i <= steps
  BEGIN
    DECLARE sigma Float32 = sigmas[i];
    DECLARE sigma_next Float32 = sigmas[i + 1];
    DECLARE c_in Float32 = CAST(1.0 AS Float32)
      / sqrt(sigma * sigma + CAST(1.0 AS Float32));
    DECLARE scaled Float32[] = array_scale(latents, c_in);

    -- Conditional UNet pass — always needed.
    DECLARE pred_cond Float32[] = infer(
      'unet',
      { sample: scaled,
        timestep: timesteps[i],
        encoder_hidden_states: combined_embeds,
        text_embeds: pooled,
        time_ids: time_ids },
      { sample: [1::Int32, 4::Int32, 128::Int32, 128::Int32],
        encoder_hidden_states: [1::Int32, 77::Int32, 2048::Int32],
        text_embeds: [1::Int32, 1280::Int32],
        time_ids: [1::Int32, 6::Int32] });

    -- Classifier-free guidance only when cfg > 1 (a single conditional pass is
    -- exactly cfg = 1). Second pass on the unconditional embeddings, then
    -- extrapolate: pred = uncond + cfg * (cond - uncond).
    IF cfg > CAST(1.0 AS Float32)
    BEGIN
      DECLARE pred_uncond Float32[] = infer(
        'unet',
        { sample: scaled,
          timestep: timesteps[i],
          encoder_hidden_states: uncond_embeds,
          text_embeds: uncond_pooled,
          time_ids: time_ids },
        { sample: [1::Int32, 4::Int32, 128::Int32, 128::Int32],
          encoder_hidden_states: [1::Int32, 77::Int32, 2048::Int32],
          text_embeds: [1::Int32, 1280::Int32],
          time_ids: [1::Int32, 6::Int32] });
      DECLARE diff Float32[] = array_axpy(
        pred_cond, CAST(-1.0 AS Float32), pred_uncond);   -- cond - uncond
      DECLARE guided Float32[] = array_axpy(pred_uncond, cfg, diff);
      SET latents = array_axpy(latents, sigma_next - sigma, guided);
    END
    ELSE
    BEGIN
      SET latents = array_axpy(latents, sigma_next - sigma, pred_cond);
    END;
    SET i = i + 1
  END;

  -- 9. Scale latents for SDXL VAE (0.13025, not SD 2.x's 0.18215).
  SET latents = array_scale(latents, CAST(1.0 / 0.13025 AS Float32));

  -- 10. VAE decode: [1, 4, 128, 128] -> [1, 3, 1024, 1024] in [-1, 1].
  DECLARE rgb Float32[] = infer(
    'vae_decoder', latents,
    [1::Int32, 4::Int32, 128::Int32, 128::Int32]);

  -- 11. (value * 0.5 + 0.5) * 255 maps [-1, 1] -> [0, 255].
  RETURN tensor_to_image_chw(
    rgb, 1024, 1024,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)])
END
