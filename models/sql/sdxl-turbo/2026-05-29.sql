-- ============================================================================
-- SDXL Turbo — text-to-image, Stability AI Community License.
-- ============================================================================
--
-- Catalog id:  sdxl-turbo                        (models/catalog.json)
-- License:     Stability AI Community License
-- Upstream:    https://huggingface.co/stabilityai/sdxl-turbo
--
-- Stability AI's Adversarial Diffusion Distillation (ADD) of SDXL base,
-- distilled for 1-4 step inference. Designed for 512×512 (Stability's model
-- card recommendation), but the architecture is the full SDXL UNet so
-- larger sizes (768, 1024) work with roughly quadratic VRAM cost and some
-- quality drift away from the distillation target. See
-- models/sql/juggernaut-xl-lightning/2026-05-29.sql for the canonical
-- annotated SDXL reference — that one locks to 1024×1024 because the
-- Lightning distillation was trained at full SDXL size.
-- ============================================================================

CREATE OR REPLACE MODEL sdxl_turbo(
  prompt String,
  steps  Int32 = 4
    CHECK (steps BETWEEN 1 AND 8)
    COMMENT 'Number of Euler denoising steps. SDXL Turbo was distilled via ADD for 1-4 steps; 1 is the design point for fastest output, 4 is the quality sweet spot.',
  size   Int32 = 512
    CHECK (size BETWEEN 256 AND 1024 AND size % 8 = 0)
    STEP 8
    UNIT 'pixels'
    COMMENT 'Output image side length in pixels. Must be a multiple of 8 (the VAE downsample factor; latent side = size / 8). 512 is the distillation target — fastest and highest fidelity to the ADD training. 768 and 1024 work since the underlying UNet is full SDXL, but VRAM grows roughly with size² and quality drifts away from the 512 distillation point. Other multiples of 8 are accepted but untested.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'sdxl-turbo/text_encoder/model.onnx'   AS text_encoder_1,
      'sdxl-turbo/text_encoder_2/model.onnx' AS text_encoder_2,
      'sdxl-turbo/unet/model.onnx'           AS unet,
      'sdxl-turbo/vae_decoder/model.onnx'    AS vae_decoder
AS BEGIN
  -- 0. Derive latent side and a reusable Float32 of `size` for time_ids.
  --    The VAE downsamples by 8; if `size` isn't a multiple of 8 the UNet
  --    shapes won't line up and ORT will throw at first dispatch.
  DECLARE latent_dim Int32 = size / 8;
  DECLARE size_f Float32 = CAST(size AS Float32);

  -- 1. CLIP-frame input ids — both encoders consume the same 77-token sequence.
  DECLARE input_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  -- 2. Text encoder 1 (CLIP-L 768) -> [1, 77, 768] hidden states.
  DECLARE hidden_1 Float32[] = infer(
    'text_encoder_1', input_ids, [1::Int32, 77::Int32]);

  -- 3. Text encoder 2 (OpenCLIP-G 1280) -> {last_hidden_state: [1, 77, 1280],
  --    text_embeds: [1, 1280]}.
  DECLARE outputs_2 Struct = infer_outputs(
    'text_encoder_2', input_ids, [1::Int32, 77::Int32]);
  DECLARE hidden_2 Float32[] = outputs_2['last_hidden_state'];
  DECLARE pooled   Float32[] = outputs_2['text_embeds'];

  -- 4. Concat along the hidden dim per token: [1, 77, 768+1280=2048].
  DECLARE combined_embeds Float32[] = array_concat_last_dim(
    hidden_1, 768::Int32, hidden_2, 1280::Int32);

  -- 5. Clamp to fp16 representable range (defensive — matches the
  --    JuggernautXL Lightning body's pattern).
  SET combined_embeds = array_clamp(
    combined_embeds, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));
  SET pooled = array_clamp(
    pooled, CAST(-65504.0 AS Float32), CAST(65504.0 AS Float32));

  -- 6. SDXL added_cond_kwargs.time_ids — original size, crop offset, target size.
  DECLARE time_ids Float32[] = [
    size_f, size_f,
    CAST(0.0 AS Float32), CAST(0.0 AS Float32),
    size_f, size_f];

  -- 7. ADD-distilled schedule + initial noisy latent (4 channels x latent_dim^2).
  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];
  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * latent_dim * latent_dim), sigmas[1]);

  -- 8. Euler denoising loop.
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
        encoder_hidden_states: combined_embeds,
        text_embeds: pooled,
        time_ids: time_ids },
      { sample: [1::Int32, 4::Int32, latent_dim, latent_dim],
        encoder_hidden_states: [1::Int32, 77::Int32, 2048::Int32],
        text_embeds: [1::Int32, 1280::Int32],
        time_ids: [1::Int32, 6::Int32] });

    SET latents = array_axpy(latents, sigma_next - sigma, noise_pred);
    SET i = i + 1
  END;

  -- 9. Scale latents for SDXL VAE (0.13025).
  SET latents = array_scale(latents, CAST(1.0 / 0.13025 AS Float32));

  -- 10. VAE decode: [1, 4, latent_dim, latent_dim] -> [1, 3, size, size] in [-1, 1].
  DECLARE rgb Float32[] = infer(
    'vae_decoder', latents,
    [1::Int32, 4::Int32, latent_dim, latent_dim]);

  -- 11. (value * 0.5 + 0.5) * 255 maps [-1, 1] -> [0, 255].
  RETURN tensor_to_image_chw(
    rgb, size, size,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)])
END
