-- ============================================================================
-- MobileSAM (everything mode) — segmentation, Apache-2.0.
-- ============================================================================
--
-- Catalog id:  mobile-sam                       (models/catalog.json)
-- License:     Apache-2.0
-- Upstream:    https://github.com/ChaoningZhang/MobileSAM
--              ONNX export: https://github.com/vietanhdev/samexporter
--
-- "Everything" segmentation: sweep a `grid_size × grid_size` grid of
-- foreground prompts across the image, run the prompt-conditioned mask
-- decoder for each, drop weak / fuzzy / empty candidates, NMS the
-- survivors, and return them as an Array<Image> of binary masks at the
-- input image's original resolution.
--
-- **Pipeline:**
--   1. sam_preprocess              — Aspect-preserving longest-side=1024
--                                    resize + raw HWC Float32 pack. Returns
--                                    {tensor, scale, height, width} — the
--                                    encoder's input is [resized_h, resized_w, 3]
--                                    (the encoder graph pads to 1024×1024
--                                    internally); the scale factor maps
--                                    original-image prompt coords into the
--                                    encoder's 1024-space.
--   2. infer('encoder')            — TinyViT ONNX → [1, 256, 64, 64] embedding.
--                                    One pass per image, reused by every prompt.
--   3. Grid sweep (nested WHILE)   — For each cell center (gx, gy):
--                                      a. scale (gx+0.5, gy+0.5)/grid into
--                                         (px*scale, py*scale) prompt with a
--                                         (0, 0) label=-1 padding sentinel
--                                         (samexporter convention).
--                                      b. infer_outputs('decoder', ...) — six
--                                         named inputs incl. zeroed mask_input
--                                         + has_mask_input=0; returns
--                                         {masks: [1, 4, H, W] Float32 logits,
--                                          iou_predictions: [1, 4]}.
--                                      c. Per-candidate filter: drop if
--                                         iou < 0.88 (sure-of-itself) or
--                                         stability_score < 0.95 (crisp boundary).
--                                         SAM canonical thresholds.
--                                      d. Accumulate surviving planes + scores
--                                         into List<Float32> accumulators.
--   4. mask_nms_planes             — Threshold planes at logit > 0, sort by
--                                    score desc, NMS at mask-IoU 0.7, materialize
--                                    survivors as Array<Image> (binary RGBA,
--                                    same shape as u2net masks).
--
-- **Cost.** Encoder runs once; decoder runs grid_size² times. At the default
-- grid_size=16 that's 256 decoder dispatches per row (~0.5-1s on CPU); SAM
-- canonical 32 yields 1024 (~3-5s). Survivor planes accumulate into a
-- List<Float32> that grows off-arena with amortised O(1) APPEND, so the
-- O(N^2) array_concat churn that used to dominate is gone. The remaining
-- arena ceiling is the per-cell decoder output: line ~131 flattens each
-- cell's [1, M, H, W] mask tensor into the per-row arena and nothing is
-- reclaimed until the row completes, so arena use grows ~grid_size^2 *
-- mask_bytes and overflows the 8GB cap above ~grid_size=20 at full image
-- resolution. Lifting grid_size back to the canonical 32 needs per-iteration
-- arena reclamation (a filed follow-up); the off-arena accumulator makes that
-- reset clean.
-- ============================================================================

CREATE OR REPLACE MODEL mobilesam(
  img       Image,
  grid_size Int32 = 16
    CHECK (grid_size BETWEEN 4 AND 128)
    COMMENT 'Side length of the foreground-prompt grid (the model runs grid_size * grid_size decoder dispatches per row). Default 16 (SAM canonical is 32). The cap is the per-cell decoder output: each cell flattens an [1, M, H, W] mask tensor into the body arena and nothing is reclaimed until the row finishes, so the arena grows ~grid_size^2 * mask_bytes. At full image resolution that overflows the 8GB arena above ~grid_size=20. Raising the default to 32 waits on per-iteration arena reclamation (the List<Float32> accumulator already grows off-arena, so that reset is clean). Range 4-128.'
) RETURNS Array<Image>
USING 'mobile-sam/2026-05-29/mobile_sam_image_encoder.onnx' AS encoder,
      'mobile-sam/2026-05-29/sam_mask_decoder_multi.onnx'   AS decoder
AS BEGIN
  -- 1. Encode the image once. sam_preprocess returns the resized HWC tensor
  -- plus the scale / resized dims needed for the encoder shape + prompt
  -- coordinate transform.
  DECLARE prep Struct = sam_preprocess(img);
  DECLARE tensor     Float32[] = prep['tensor'];
  DECLARE scale      Float32   = prep['scale'];
  DECLARE resized_h  Int32     = prep['height'];
  DECLARE resized_w  Int32     = prep['width'];
  DECLARE embeddings Float32[] = infer(
    'encoder', tensor, [resized_h, resized_w, 3::Int32]);

  DECLARE orig_h Int32 = image_height(img);
  DECLARE orig_w Int32 = image_width(img);
  DECLARE plane_size Int32 = orig_h * orig_w;

  -- 2. Static decoder side-inputs reused across every grid prompt. mask_input
  -- is zeros (has_mask_input=0 disables the conditioning path); orig_im_size
  -- in PyTorch (rows, cols) order drives the decoder's internal mask
  -- resize-back to the original image dims.
  DECLARE mask_input Float32[] = array_repeat(CAST(0.0 AS Float32), 256 * 256);
  DECLARE has_mask Float32[] = [CAST(0.0 AS Float32)];
  DECLARE orig_im_size Float32[] = [
    CAST(orig_h AS Float32), CAST(orig_w AS Float32)];

  -- Accumulators for surviving candidates pre-NMS. List<Float32> grows in
  -- place with amortised O(1) APPEND (no copy-on-modify), then freezes to a
  -- flat Float32[] when handed to mask_nms_planes at RETURN: N*plane_size
  -- mask floats + a parallel Float32[] of N scores. mask_nms_planes derives N
  -- from cardinality(scores) and slices.
  DECLARE acc_planes List<Float32>;
  DECLARE acc_scores List<Float32>;

  -- 3. Grid sweep with inline iou + stability filtering. The IF guards keep
  -- the accumulator small — without them, gridSize²×4 candidates at full
  -- plane_size would blow memory.
  DECLARE gy Int32 = 0;
  WHILE gy < grid_size
  BEGIN
    DECLARE gx Int32 = 0;
    WHILE gx < grid_size
    BEGIN
      DECLARE px Float32 = (CAST(gx AS Float32) + CAST(0.5 AS Float32))
                           * CAST(orig_w AS Float32) / CAST(grid_size AS Float32);
      DECLARE py Float32 = (CAST(gy AS Float32) + CAST(0.5 AS Float32))
                           * CAST(orig_h AS Float32) / CAST(grid_size AS Float32);
      DECLARE point_coords Float32[] = [
        px * scale, py * scale, CAST(0.0 AS Float32), CAST(0.0 AS Float32)];
      DECLARE point_labels Float32[] = [
        CAST(1.0 AS Float32), CAST(-1.0 AS Float32)];
      DECLARE dec Struct = infer_outputs(
        'decoder',
        { image_embeddings: embeddings,
          point_coords:     point_coords,
          point_labels:     point_labels,
          mask_input:       mask_input,
          has_mask_input:   has_mask,
          orig_im_size:     orig_im_size },
        { image_embeddings: [1::Int32, 256::Int32, 64::Int32, 64::Int32],
          point_coords:     [1::Int32, 2::Int32, 2::Int32],
          point_labels:     [1::Int32, 2::Int32],
          mask_input:       [1::Int32, 1::Int32, 256::Int32, 256::Int32],
          has_mask_input:   [1::Int32],
          orig_im_size:     [2::Int32] });
      -- masks comes back rank-4 [1, M, H, W]; iou_predictions rank-2 [1, M].
      -- Flatten both so 1-based bracket access (iou[c]) + array_slice work.
      DECLARE masks Float32[] = array_flatten(dec['masks']);
      DECLARE iou   Float32[] = array_flatten(dec['iou_predictions']);
      DECLARE c Int32 = 1;
      WHILE c <= 4
      BEGIN
        DECLARE iou_c Float32 = iou[c];
        -- Tightened from SAM canonical (0.88, 0.95) to keep survivor count down.
        -- The List<Float32> accumulator already removed the O(N^2) array_concat
        -- churn, but the dominant arena cost is the per-cell mask flatten above,
        -- not the accumulator — relax these (and raise grid_size) once
        -- per-iteration arena reclamation lands.
        IF iou_c >= CAST(0.92 AS Float32)
        BEGIN
          -- 1-based array_slice: candidate c lives at [(c-1)*plane_size + 1, plane_size].
          DECLARE plane_start Int32 = (c - 1) * plane_size + 1;
          DECLARE plane Float32[] = array_slice(masks, plane_start, plane_size);
          DECLARE stability Float32 = sam_stability_score(plane, orig_h, orig_w, CAST(1.0 AS Float32));
          IF stability >= CAST(0.97 AS Float32)
          BEGIN
            APPEND plane TO acc_planes;
            APPEND iou_c TO acc_scores
          END
        END
        SET c = c + 1
      END
      SET gx = gx + 1
    END
    SET gy = gy + 1
  END
  RETURN mask_nms_planes(acc_planes, acc_scores, orig_h, orig_w, CAST(0.7 AS Float32))
END

-- ============================================================================
-- MobileSAM (prompted mode) — segment a single object at a user click.
-- ============================================================================
--
-- Catalog id:  mobile-sam (same bundle, second model in the installSql).
-- License:     Apache-2.0.
--
-- Same encoder + decoder as the everything-mode body above; differs only in
-- the prompt construction (one user-supplied (x, y) instead of a grid sweep)
-- and the output shape (a single Image instead of Array<Image>). No
-- accumulator → no array_concat → no arena-pressure concern; this body fits
-- under the 8 GB cap on arbitrarily large images.
--
-- **Pipeline:**
--   1. sam_preprocess              — Same as everything mode.
--   2. infer('encoder')            — One pass per image.
--   3. Build a single prompt at (x, y) scaled by sam_preprocess's scale,
--      plus the (0, 0) padding sentinel with label -1.
--   4. infer_outputs('decoder')    — Six named inputs, returns 4 candidate
--                                    masks + iou_predictions [1, 4].
--   5. argmax(iou)                 — Pick the most-confident candidate.
--   6. binary_mask_from_logits     — Threshold the chosen plane at logit > 0
--                                    and pack as a binary RGBA Image at
--                                    (orig_h, orig_w).
--
-- **Coordinate convention.** `x` and `y` are original-image pixel coords:
-- (0, 0) is the top-left, x grows right, y grows down. The encoder graph
-- handles the 1024-space conversion internally via orig_im_size.
-- ============================================================================

CREATE OR REPLACE MODEL mobilesam_point(
  img Image,
  x   Float64
    COMMENT 'Click x-coordinate in original-image pixel space (0 = left, grows right).',
  y   Float64
    COMMENT 'Click y-coordinate in original-image pixel space (0 = top, grows down).'
) RETURNS Image
USING 'mobile-sam/2026-05-29/mobile_sam_image_encoder.onnx' AS encoder,
      'mobile-sam/2026-05-29/sam_mask_decoder_multi.onnx'   AS decoder
AS BEGIN
  -- 1. Encode the image once.
  DECLARE prep Struct = sam_preprocess(img);
  DECLARE tensor     Float32[] = prep['tensor'];
  DECLARE scale      Float32   = prep['scale'];
  DECLARE resized_h  Int32     = prep['height'];
  DECLARE resized_w  Int32     = prep['width'];
  DECLARE embeddings Float32[] = infer(
    'encoder', tensor, [resized_h, resized_w, 3::Int32]);

  DECLARE orig_h Int32 = image_height(img);
  DECLARE orig_w Int32 = image_width(img);

  -- 2. Real prompt + (0, 0) padding sentinel with label -1 (samexporter
  -- convention; decoders that require >= 2 points need the sentinel).
  DECLARE point_coords Float32[] = [
    CAST(x AS Float32) * scale, CAST(y AS Float32) * scale,
    CAST(0.0 AS Float32), CAST(0.0 AS Float32)];
  DECLARE point_labels Float32[] = [
    CAST(1.0 AS Float32), CAST(-1.0 AS Float32)];

  DECLARE dec Struct = infer_outputs(
    'decoder',
    { image_embeddings: embeddings,
      point_coords:     point_coords,
      point_labels:     point_labels,
      mask_input:       array_repeat(CAST(0.0 AS Float32), 256 * 256),
      has_mask_input:   [CAST(0.0 AS Float32)],
      orig_im_size:     [CAST(orig_h AS Float32), CAST(orig_w AS Float32)] },
    { image_embeddings: [1::Int32, 256::Int32, 64::Int32, 64::Int32],
      point_coords:     [1::Int32, 2::Int32, 2::Int32],
      point_labels:     [1::Int32, 2::Int32],
      mask_input:       [1::Int32, 1::Int32, 256::Int32, 256::Int32],
      has_mask_input:   [1::Int32],
      orig_im_size:     [2::Int32] });

  -- masks [1, 4, H, W] -> flat; iou [1, 4] -> flat.
  DECLARE masks Float32[] = array_flatten(dec['masks']);
  DECLARE iou   Float32[] = array_flatten(dec['iou_predictions']);

  -- 3. Most-confident candidate, then threshold its plane at logit > 0.
  DECLARE best Int32 = argmax(iou);
  DECLARE plane_size Int32 = orig_h * orig_w;
  DECLARE best_plane Float32[] = array_slice(
    masks, (best - 1) * plane_size + 1, plane_size);
  RETURN binary_mask_from_logits(best_plane, orig_h, orig_w, CAST(0.0 AS Float32))
END
