-- ============================================================================
-- epiCRealism + Hyper-SD — text-to-image, CreativeML OpenRAIL-M.
-- ============================================================================
--
-- Catalog id:  epicrealism-hyper               (models/catalog.json)
-- License:     CreativeML OpenRAIL-M
-- Upstream:    https://huggingface.co/emilianJR/epiCRealism + ByteDance/Hyper-SD
--
-- Photoreal environments / natural lighting SD 1.5 fine-tune distilled to
-- 4 steps. Pipeline shape is identical to every other SD 1.x Hyper variant;
-- see models/sql/realistic-vision-hyper/2026-05-29.sql for the canonical
-- annotated reference.
-- ============================================================================

CREATE OR REPLACE MODEL epicrealism_hyper(
  prompt String,
  steps  Int32 = 4
    CHECK (steps BETWEEN 1 AND 8)
    COMMENT 'Number of Euler denoising steps. Hyper-SD was distilled for 1-4 steps; 1 is fastest, 4 is the recommended minimum for face / detail quality, beyond 4 returns diminishing gains.'
) RETURNS Image
IMPLEMENTS TextToImage
USING 'epicrealism-hyper/2026-05-29/text_encoder/model.onnx' AS text_encoder,
      'epicrealism-hyper/2026-05-29/unet/model.onnx'         AS unet,
      'epicrealism-hyper/2026-05-29/vae_decoder/model.onnx'  AS vae_decoder
AS BEGIN
  DECLARE input_ids Int64[] = tokenizer.encode_clip(
    prompt, '../tokenizer/vocab.json', '../tokenizer/merges.txt');

  DECLARE text_embeds Float32[] = infer(
    'text_encoder', input_ids, [1::Int32, 77::Int32]);

  DECLARE schedule Struct = sd_turbo_schedule(steps);
  DECLARE sigmas Float32[] = schedule['sigmas'];
  DECLARE timesteps Float32[] = schedule['timesteps'];

  DECLARE latents Float32[] = array_scale(
    sample_normal(4 * 64 * 64), sigmas[1]);

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

    SET latents = array_axpy(latents, sigma_next - sigma, noise_pred);
    SET i = i + 1
  END;

  SET latents = array_scale(latents, CAST(1.0 / 0.18215 AS Float32));

  DECLARE rgb Float32[] = infer(
    'vae_decoder', latents,
    [1::Int32, 4::Int32, 64::Int32, 64::Int32]);

  RETURN tensor_to_image_chw(
    rgb, 512, 512,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)],
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)])
END
