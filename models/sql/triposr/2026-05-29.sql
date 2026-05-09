-- ============================================================================
-- TripoSR — single-image to 3D mesh in one feedforward pass. MIT.
-- ============================================================================
--
-- Catalog id:  triposr            (models/catalog.json)
-- License:     MIT
-- Upstream:    https://huggingface.co/stabilityai/TripoSR
--              https://github.com/VAST-AI-Research/TripoSR
-- Paper:       Tochilkin et al. 2024 — "TripoSR: Fast 3D Object Reconstruction
--              from a Single Image"
--
-- TripoSR hallucinates the full 3D geometry of an object from a single
-- front-facing photo. The complement to the DepthEstimator + mesh_from_depth_*
-- pipeline (which captures only what the camera sees): here the model fills
-- in the back / occluded surfaces using its training prior. Best on a single
-- object centered in frame against a clean background (compose with
-- models.u2netp for background removal when the source photo has clutter).
--
-- Two ONNX files because the architecture splits into a one-shot triplane
-- generator + a per-query NeRF MLP:
--
--   triplane.onnx  image[1,3,512,512] -> triplane[1,3,40,64,64]
--                  DINOv1 ViT encoder + cross-attention triplane decoder.
--                  Runs once per image. Weights live in triplane.onnx_data
--                  (sidecar via ONNX external_data_format; ORT loads it
--                  automatically when colocated with the .onnx graph).
--
--   nerf.onnx      (triplane, xyz[K,3]) -> (density[K], color[K,3])
--                  Triplane bilinear sampler + small MLP. Runs many times
--                  per image — chunked across a resolution³ voxel grid by
--                  mesh_from_triplane.
--
-- Pipeline:
--   0. image_resize_foreground — crop to the alpha bbox, centre in a square
--                                with `fg_ratio` of side occupied by subject.
--                                Mirrors TripoSR's `resize_foreground(_, 0.85)`
--                                reference preprocessing. Without this step,
--                                a photo where the subject covers a third of
--                                the frame produces garbled output because
--                                the model sees mostly empty input.
--   1. image_composite_over   — flatten the input against mid-gray (0.5).
--                               TripoSR's training data was rembg-cleaned
--                               then composited over 0.5 gray; feeding a
--                               raw cutout (alpha=0 outside subject) or a
--                               black-background image produces silhouette
--                               ghosts on the unit cube's faces. This step
--                               is a no-op for already-flattened RGB inputs.
--   2. image_to_tensor_chw    — stretch-resize to 512×512, RGB CHW,
--                               divide-by-255 only (mean=0, std=1). The
--                               DINO encoder inside triplane.onnx applies
--                               its own ImageNet mean/std normalisation;
--                               applying it again here would double-normalise.
--   3. infer('triplane', ...) — single dispatch; output is the [1,3,40,64,64]
--                               triplane feature volume.
--   4. mesh_from_triplane     — chunked NeRF dispatch over a resolution³
--                               xyz grid spanning [-1, +1]³, Marching Cubes
--                               at the supplied isolevel, second pass for
--                               per-vertex colors.
--   5. mesh_swap_axes         — rotate from TripoSR's training frame
--                               (+X back, +Y right, +Z up; per tsr/utils.py
--                               line 357) to Three.js / glTF (+X right,
--                               +Y up, +Z toward viewer). Cyclic permutation
--                               [2, 3, 1]: out.X = in.Y, out.Y = in.Z,
--                               out.Z = in.X. Determinant +1 so winding is
--                               preserved.
-- ============================================================================

CREATE OR REPLACE MODEL triposr(
  img Image,
  resolution Int32 = CAST(256 AS Int32)
    CHECK (resolution BETWEEN 64 AND 512) STEP 32
    COMMENT 'Voxel grid resolution per axis (resolution³ total query points). 256 is TripoSR''s reference setting; drop to 128 for ~8× faster previews, push to 384 for higher-quality but ~3× slower extraction.',
  isolevel Float32 = CAST(25.0 AS Float32)
    COMMENT 'Marching Cubes iso-surface threshold on density_act. Higher = tighter (closer to the object''s skin); lower = puffier. 25.0 is the TripoSR reference; tune per subject.',
  chunk_size Int32 = CAST(65536 AS Int32)
    CHECK (chunk_size BETWEEN 4096 AND 1048576) STEP 4096
    COMMENT 'Query-points per nerf.onnx dispatch. Bigger = fewer dispatches but more VRAM per call; 65536 fits comfortably on a 12 GB GPU.',
  fg_ratio Float32 = CAST(0.85 AS Float32)
    CHECK (fg_ratio BETWEEN 0.5 AND 1.0) STEP 0.05
    COMMENT 'Fraction of the 512×512 input that the subject should occupy after centring (the rest is transparent margin filled by mid-gray). 0.85 is TripoSR''s reference; lower (0.7–0.8) leaves more breathing room and helps when the subject has extremities (legs, antennas, etc.) that would otherwise hit the edge.'
) RETURNS Mesh
IMPLEMENTS MeshFromImage
USING 'triposr/2026-05-29/triplane.onnx' AS triplane,
      'triposr/2026-05-29/nerf.onnx'     AS nerf
AS BEGIN
  -- Centre the subject in a canonical-margin square. Without this, photos
  -- where the subject occupies <50% of the frame get stretched into 512×512
  -- with the subject still tiny — TripoSR has nothing to anchor to and
  -- produces garbled geometry. Subject's tight alpha bbox lands in a square
  -- where it occupies `fg_ratio` of each side; the rest is transparent and
  -- gets filled by the composite-over-gray step below.
  DECLARE framed Image = image_resize_foreground(img, fg_ratio);

  -- Flatten against mid-gray. Matches the training-data background that
  -- TripoSR's image_processor produces (`rgb · alpha + 0.5 · (1 - alpha)`).
  -- No-op for inputs that are already opaque RGB; rescues alpha-bearing
  -- cutouts and black-background flatteners from the silhouette-ghost
  -- artifacts the model emits when it sees scene-y pixels around the subject.
  DECLARE flat Image = image_composite_over(
    framed,
    [CAST(0.5 AS Float32), CAST(0.5 AS Float32), CAST(0.5 AS Float32)]);

  -- Preprocess: resize to 512×512, pack as CHW Float32 in [0, 1]. The DINO
  -- encoder inside the triplane export applies its own ImageNet normalisation,
  -- so we pass un-normalised RGB here (mean=0, std=1).
  DECLARE tensor Float32[] = image_to_tensor_chw(
    flat,
    [CAST(512 AS Int32), CAST(512 AS Int32)],
    [CAST(0.0 AS Float32), CAST(0.0 AS Float32), CAST(0.0 AS Float32)],
    [CAST(1.0 AS Float32), CAST(1.0 AS Float32), CAST(1.0 AS Float32)]);

  -- Pass 1 (one shot per image): image → triplane feature volume.
  DECLARE triplane_features Float32[] = infer(
    'triplane', tensor,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(512 AS Int32), CAST(512 AS Int32)]);

  -- Pass 2 (chunked density loop + Marching Cubes + chunked color loop):
  -- the orchestrator owns the xyz grid construction and dispatch chunking
  -- so the SQL surface stays one call.
  --
  -- radius = 0.87 matches `renderer.radius` in TripoSR's config.yaml
  -- (the comment there reads "slightly larger than 0.5 * sqrt(3)" — i.e.
  -- the sphere that just contains the unit cube). Sampling outside that
  -- range queries the triplane at clamped grid_sample boundaries; the
  -- decoder MLP then emits a near-constant value that crosses the iso
  -- threshold and produces phantom "slices" on each of the six cube
  -- faces. Always use the model's trained radius, not 1.0.
  DECLARE raw Mesh = mesh_from_triplane(
    'nerf',
    triplane_features,
    [CAST(1 AS Int32), CAST(3 AS Int32), CAST(40 AS Int32), CAST(64 AS Int32), CAST(64 AS Int32)],
    resolution,
    isolevel,
    CAST(0.87 AS Float32),
    chunk_size);

  -- Rotate from TripoSR's training frame (X-back, Y-right, Z-up) to the
  -- glTF / Three.js convention (X-right, Y-up, Z-toward-viewer). Cyclic
  -- axis permutation with determinant +1 — winding stays correct, no
  -- triangle index reversal needed.
  RETURN mesh_swap_axes(raw,
    [CAST(2 AS Int32), CAST(3 AS Int32), CAST(1 AS Int32)])
END
