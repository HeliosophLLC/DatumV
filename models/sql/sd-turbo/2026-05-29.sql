-- ============================================================================
-- SD Turbo — text-to-image, Stability AI Community License.
-- ============================================================================
--
-- Catalog id:  sd-turbo                          (models/catalog.json)
-- ONNX files:  text_encoder/model.onnx, unet/model.onnx, vae_decoder/model.onnx
-- License:     Stability AI Community License
-- Upstream:    https://huggingface.co/stabilityai/sd-turbo
--
-- Stability AI's Adversarial Diffusion Distillation (ADD) of Stable
-- Diffusion 2.1, distilled for 1-4 step inference. Differs from the SD 1.x
-- Hyper bodies (see models/sql/absolute-reality-hyper/2026-05-29.sql for
-- the SD 1.5 reference) in two load-bearing places:
--   1. Single text encoder is CLIP-H (OpenCLIP ViT-H/14), embedding dim
--      **1024**, not the 768 of SD 1.5's CLIP-L.
--   2. UNet's encoder_hidden_states shape is [1, 77, 1024] correspondingly.
-- Latent shape (4 x 64 x 64 -> 512 x 512 output) and VAE scale (0.18215,
-- shared with SD 2.x) are otherwise the same as the SD 1.5 family.
--
-- The denoising schedule, CLIP tokenization, sample_normal noise, Euler
-- update, and tensor->image conversion are shared with the SD 1.x bodies.
-- ============================================================================

CREATE OR REPLACE MODEL sd_turbo(
  prompt String,
  steps  Int32 = 4
    CHECK (steps BETWEEN 1 AND 8)
    COMMENT 'Number of Euler denoising steps. SD Turbo was distilled via ADD for 1-4 steps; 1 is the design point for fastest output, 4 is the quality sweet spot, beyond 4 returns diminishing gains.',
  size   Int32 = 512
    CHECK (size BETWEEN 256 AND 1024 AND size % 8 = 0)
    STEP 8
    UNIT 'pixels'
    COMMENT 'Output image side length in pixels. Must be a multiple of 8 (the VAE downsample factor; latent side = size / 8). 512 is the distillation target — fastest and highest fidelity to the ADD training. The underlying SD 2.1 UNet supports 64-aligned alternates (576, 640, 768, ...) with roughly quadratic VRAM cost and quality drift away from the 512 sweet spot.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'sd-turbo/text_encoder/model.onnx' AS text_encoder,
      'sd-turbo/unet/model.onnx'         AS unet,
      'sd-turbo/vae_decoder/model.onnx'  AS vae_decoder
AS BEGIN
  DECLARE latent_dim Int32 = size / 8;

  DECLARE input_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  DECLARE text_embeds Float32[] = infer(
    'text_encoder', input_ids, [1::Int32, 77::Int32]);

  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];

  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * latent_dim * latent_dim), sigmas[1]);

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
      { sample: [1::Int32, 4::Int32, latent_dim, latent_dim],
        encoder_hidden_states: [1::Int32, 77::Int32, 1024::Int32] });

    SET latents = array_axpy(latents, sigma_next - sigma, noise_pred);
    SET i = i + 1
  END;

  SET latents = array_scale(latents, CAST(1.0 / 0.18215 AS Float32));

  DECLARE rgb Float32[] = infer(
    'vae_decoder', latents,
    [1::Int32, 4::Int32, latent_dim, latent_dim]);

  RETURN tensor_to_image_chw(
    rgb, size, size,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)])
END
