---
title: Multi-dim arrays and bracket indexing
---

Columns declared with multiple dimensions (`Array<T>(N, M, …)`) carry an
explicit shape — elements are addressed by per-dim indices via bracket
syntax `m[y, x]`. The shape also survives `infer()` outputs whose ONNX
tensor rank is ≥ 2, so depth maps and feature tensors can be poked at
directly without manually flattening.

```sql
CREATE TABLE grids (m Array<Float32>(2, 3));
INSERT INTO grids VALUES ([1.0, 2.0, 3.0, 4.0, 5.0, 6.0]);

-- Per-element access (zero-based, row-major). Out-of-range returns NULL.
SELECT m[0, 0]       AS top_left,    -- 1.0
       m[1, 2]       AS bottom_right -- 6.0
FROM grids;

-- Introspection: shape, ndim, total element count.
SELECT array_shape(m)         AS shape,       -- [2, 3]
       array_ndims(m)         AS ndim,        -- 2
       cardinality(m)         AS total,       -- 6
       array_length(m, 1)     AS rows,        -- 2
       array_length(m, 2)     AS cols         -- 3
FROM grids;
```

The same syntax works against multi-dim function outputs — useful for
plucking a single pixel out of a depth map without materialising the
intermediate tensor:

```sql
-- Pixel-at-center of a 384×384 depth output (after squeezing the leading
-- batch dim). Useful for spot-checking inference results.
SELECT models.midas_small(file_bytes)[0, 192, 192] AS center_depth
FROM photos
LIMIT 1;
```

See [Array Functions](../functions/array.md#multi-dim-arrays) for the
per-function multi-dim behavior table — which functions are shape-aware
vs. which silently flatten.
