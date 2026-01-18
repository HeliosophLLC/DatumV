// Pure data binding: (schema, rows, roles) → SceneData. No three.js
// imports — viz-three.ts converts the scene data into THREE.Points
// or THREE.Line instances. Keeping these separate means the role
// logic can be unit-tested without instantiating WebGL contexts.
//
// Cell text parsing: rows arrive as Cell[][] from the server, where
// each cell carries a `text` field. For numeric columns we coerce via
// parseFloat (cell.text === 'NaN' / 'Infinity' / '' → NaN, dropped).
// For Point3D / Point2D positionMode we accept '(x, y, z)' formatted
// text; if parsing fails the row is skipped with a warning.

import type {
  Cell,
  ResultSet,
  SchemaColumn,
} from './result-types.js';
import type {
  RoleAssignments,
  SceneBounds,
  SceneData,
  SceneGroup,
} from './viz-types.js';

// ===== Auto-detect default roles =====
//
// First pass when the user opens the viz panel for a tab without saved
// vizConfig: pick reasonable defaults from the schema. Keeps the panel
// useful immediately for common shapes (X/Y/Z + numerics) without the
// user having to set anything.

const X_NAME_HINTS = ['x'];
const Y_NAME_HINTS = ['y'];
const Z_NAME_HINTS = ['z'];
const COLOR_NAME_HINTS = ['color', 'hue', 'value'];
const ORDER_NAME_HINTS = ['order', 'idx', 'index', 't', 'time', 'md'];
const POINT3D_KIND_HINTS = ['point3d', 'vector3', 'vec3'];
const POINT2D_KIND_HINTS = ['point2d', 'vector2', 'vec2'];

function lcKind(col: SchemaColumn): string {
  return (col.kind || '').toLowerCase();
}

function isNumericKind(col: SchemaColumn): boolean {
  if (col.isArray) return false;
  const k = lcKind(col);
  // DataKind names from the C# side: Float32/Float64/Int8/Int16/Int32/
  // Int64/UInt8/UInt16/UInt32/UInt64/Float16. The exact case may vary.
  return /^(float|int|uint)/.test(k) || k === 'decimal';
}

function isPoint3DKind(col: SchemaColumn): boolean {
  if (col.isArray) return false;
  return POINT3D_KIND_HINTS.includes(lcKind(col));
}

function isPoint2DKind(col: SchemaColumn): boolean {
  if (col.isArray) return false;
  return POINT2D_KIND_HINTS.includes(lcKind(col));
}

function findColumn(
  schema: readonly SchemaColumn[],
  predicate: (col: SchemaColumn) => boolean,
): string | null {
  const hit = schema.find(predicate);
  return hit ? hit.name : null;
}

function findColumnByNameHints(
  schema: readonly SchemaColumn[],
  hints: readonly string[],
  filter: (col: SchemaColumn) => boolean,
): string | null {
  for (const hint of hints) {
    const hit = schema.find(
      (c) => filter(c) && c.name.toLowerCase() === hint,
    );
    if (hit) return hit.name;
  }
  return null;
}

export function autoDetectRoles(
  schema: readonly SchemaColumn[],
): RoleAssignments {
  const roles: RoleAssignments = {
    positionMode: 'xyz',
    xColumn: null,
    yColumn: null,
    zColumn: null,
    positionColumn: null,
    colorColumn: null,
    sizeColumn: null,
    orderColumn: null,
    groupColumn: null,
  };

  // Position: prefer a Point3D column if present, else fall back to
  // x/y/z numeric columns.
  const point3d = findColumn(schema, isPoint3DKind);
  if (point3d) {
    roles.positionMode = 'point3d';
    roles.positionColumn = point3d;
  } else {
    roles.xColumn = findColumnByNameHints(schema, X_NAME_HINTS, isNumericKind);
    roles.yColumn = findColumnByNameHints(schema, Y_NAME_HINTS, isNumericKind);
    roles.zColumn = findColumnByNameHints(schema, Z_NAME_HINTS, isNumericKind);
  }

  // Color: explicit hint name first; otherwise leave null (uniform).
  roles.colorColumn = findColumnByNameHints(
    schema,
    COLOR_NAME_HINTS,
    isNumericKind,
  );

  // Order: hint name; users with MD-keyed wellbore data get auto-line.
  roles.orderColumn = findColumnByNameHints(
    schema,
    ORDER_NAME_HINTS,
    isNumericKind,
  );

  return roles;
}

// ===== Cell value extraction =====

function cellNumber(cell: Cell | undefined): number {
  if (!cell || cell.kind === 'null') return NaN;
  const text = (cell as { text?: string }).text;
  if (text === undefined || text === '') return NaN;
  const n = parseFloat(text);
  return Number.isFinite(n) ? n : NaN;
}

function cellString(cell: Cell | undefined): string {
  if (!cell || cell.kind === 'null') return '';
  return (cell as { text?: string }).text ?? '';
}

// Parse a Point3D / Point2D cell. The exact wire format isn't fixed
// yet; we try a few common shapes and return null on failure. Once
// the cell renderer for Point3D lands, this can be tightened.
//
// Accepts:
//   "(x, y, z)" / "(x, y)"
//   "x, y, z" / "x, y"
//   "[x, y, z]" / "[x, y]"
//   '{"x":...,"y":...,"z":...}' (JSON struct)
function parsePointText(
  text: string,
  expectedDims: 2 | 3,
): [number, number, number] | null {
  const t = text.trim();
  if (!t) return null;
  // JSON struct
  if (t.startsWith('{')) {
    try {
      const obj = JSON.parse(t) as Record<string, unknown>;
      const x = Number(obj.x);
      const y = Number(obj.y);
      const z = expectedDims === 3 ? Number(obj.z) : 0;
      if (Number.isFinite(x) && Number.isFinite(y) && Number.isFinite(z)) {
        return [x, y, z];
      }
    } catch {
      /* fall through */
    }
    return null;
  }
  // Strip parens / brackets, split on comma.
  const stripped = t.replace(/^[(\[]\s*|\s*[)\]]$/g, '');
  const parts = stripped.split(',').map((p) => parseFloat(p.trim()));
  if (parts.length < expectedDims) return null;
  const x = parts[0];
  const y = parts[1];
  const z = expectedDims === 3 ? parts[2] : 0;
  if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(z)) {
    return null;
  }
  return [x, y, z];
}

// ===== Colormap =====
//
// Continuous numeric → RGB. Five-stop linear: blue → cyan → green →
// yellow → red. Cheap, no library dependency. Maps t ∈ [0,1].

const COLORMAP_STOPS: ReadonlyArray<[number, [number, number, number]]> = [
  [0.0, [0.20, 0.30, 0.85]], // blue
  [0.25, [0.20, 0.75, 0.85]], // cyan
  [0.5, [0.30, 0.80, 0.30]], // green
  [0.75, [0.95, 0.85, 0.20]], // yellow
  [1.0, [0.90, 0.30, 0.20]], // red
];

function colormap(t: number): [number, number, number] {
  if (!Number.isFinite(t)) return [0.5, 0.5, 0.5];
  const clamped = Math.max(0, Math.min(1, t));
  for (let i = 0; i < COLORMAP_STOPS.length - 1; i++) {
    const [t0, c0] = COLORMAP_STOPS[i];
    const [t1, c1] = COLORMAP_STOPS[i + 1];
    if (clamped <= t1) {
      const u = (clamped - t0) / (t1 - t0 || 1);
      return [
        c0[0] + (c1[0] - c0[0]) * u,
        c0[1] + (c1[1] - c0[1]) * u,
        c0[2] + (c1[2] - c0[2]) * u,
      ];
    }
  }
  return COLORMAP_STOPS[COLORMAP_STOPS.length - 1][1];
}

// Distinct group colors for categorical group splits — 12-stop palette.
const GROUP_PALETTE: ReadonlyArray<[number, number, number]> = [
  [0.31, 0.60, 0.95], // azure
  [0.95, 0.45, 0.30], // coral
  [0.40, 0.80, 0.50], // mint
  [0.85, 0.55, 0.95], // lavender
  [0.95, 0.75, 0.30], // amber
  [0.50, 0.85, 0.85], // aqua
  [0.95, 0.40, 0.55], // pink
  [0.45, 0.75, 0.30], // lime
  [0.65, 0.50, 0.85], // violet
  [0.85, 0.85, 0.40], // chartreuse
  [0.50, 0.65, 0.85], // periwinkle
  [0.95, 0.65, 0.50], // peach
];

function groupColor(idx: number): [number, number, number] {
  return GROUP_PALETTE[idx % GROUP_PALETTE.length];
}

// ===== Main bind =====

interface RowExtraction {
  rowIndex: number;
  position: [number, number, number];
  colorValue: number; // NaN when no color column
  sizeValue: number;
  orderValue: number;
  groupValue: string;
}

function getColumnIndex(
  schema: readonly SchemaColumn[],
  name: string | null,
): number {
  if (!name) return -1;
  return schema.findIndex((c) => c.name === name);
}

export function bindSceneFromResultSet(
  set: ResultSet,
  roles: RoleAssignments,
): SceneData {
  const warnings: string[] = [];
  const schema = set.schema ?? [];
  const rows = set.rows;

  // ===== Resolve column indices and validate position role =====

  let posIdxX = -1;
  let posIdxY = -1;
  let posIdxZ = -1;
  let posIdxPoint = -1;
  let pointDims: 2 | 3 = 3;

  if (roles.positionMode === 'point3d') {
    posIdxPoint = getColumnIndex(schema, roles.positionColumn);
    if (posIdxPoint < 0) {
      return emptyScene(`Position column '${roles.positionColumn}' not found`);
    }
    const col = schema[posIdxPoint];
    if (isPoint2DKind(col)) pointDims = 2;
    else if (isPoint3DKind(col)) pointDims = 3;
    else {
      warnings.push(
        `Column '${col.name}' is kind '${col.kind}', not Point2D/Point3D — attempting to parse anyway`,
      );
    }
  } else {
    posIdxX = getColumnIndex(schema, roles.xColumn);
    posIdxY = getColumnIndex(schema, roles.yColumn);
    posIdxZ = getColumnIndex(schema, roles.zColumn);
    if (posIdxX < 0 || posIdxY < 0) {
      return emptyScene('Position requires X and Y columns (Z optional)');
    }
  }

  const colorIdx = getColumnIndex(schema, roles.colorColumn);
  const sizeIdx = getColumnIndex(schema, roles.sizeColumn);
  const orderIdx = getColumnIndex(schema, roles.orderColumn);
  const groupIdx = getColumnIndex(schema, roles.groupColumn);

  if (roles.colorColumn && colorIdx < 0) {
    warnings.push(`Color column '${roles.colorColumn}' not found — ignored`);
  }
  if (roles.sizeColumn && sizeIdx < 0) {
    warnings.push(`Size column '${roles.sizeColumn}' not found — ignored`);
  }
  if (roles.orderColumn && orderIdx < 0) {
    warnings.push(`Order column '${roles.orderColumn}' not found — ignored`);
  }
  if (roles.groupColumn && groupIdx < 0) {
    warnings.push(`Group column '${roles.groupColumn}' not found — ignored`);
  }

  // ===== Extract per-row =====

  const extractions: RowExtraction[] = [];
  let parseFailures = 0;

  for (let r = 0; r < rows.length; r++) {
    const row = rows[r];
    let position: [number, number, number] | null = null;

    if (roles.positionMode === 'point3d') {
      const text = cellString(row[posIdxPoint]);
      position = parsePointText(text, pointDims);
    } else {
      const x = cellNumber(row[posIdxX]);
      const y = cellNumber(row[posIdxY]);
      const z = posIdxZ >= 0 ? cellNumber(row[posIdxZ]) : 0;
      if (Number.isFinite(x) && Number.isFinite(y) && Number.isFinite(z)) {
        position = [x, y, z];
      }
    }

    if (!position) {
      parseFailures++;
      continue;
    }

    extractions.push({
      rowIndex: r,
      position,
      colorValue: colorIdx >= 0 ? cellNumber(row[colorIdx]) : NaN,
      sizeValue: sizeIdx >= 0 ? cellNumber(row[sizeIdx]) : NaN,
      orderValue: orderIdx >= 0 ? cellNumber(row[orderIdx]) : NaN,
      groupValue: groupIdx >= 0 ? cellString(row[groupIdx]) : '',
    });
  }

  if (parseFailures > 0) {
    warnings.push(
      `${parseFailures} ${parseFailures === 1 ? 'row' : 'rows'} skipped — position couldn't be parsed`,
    );
  }

  if (extractions.length === 0) {
    return {
      groups: [],
      bounds: emptyBounds(),
      warnings,
      fatalError: 'No plottable rows',
    };
  }

  // ===== Group split =====

  const groupBuckets = new Map<string, RowExtraction[]>();
  if (groupIdx >= 0) {
    for (const e of extractions) {
      let bucket = groupBuckets.get(e.groupValue);
      if (!bucket) {
        bucket = [];
        groupBuckets.set(e.groupValue, bucket);
      }
      bucket.push(e);
    }
  } else {
    groupBuckets.set('', extractions);
  }

  // ===== Color normalisation across all groups =====
  //
  // Map color values to [0, 1] using the global min/max. Picking the
  // global range (rather than per-group) keeps the visual scale
  // comparable across groups in the same scene.

  let colorMin = Infinity;
  let colorMax = -Infinity;
  if (colorIdx >= 0) {
    for (const e of extractions) {
      if (Number.isFinite(e.colorValue)) {
        if (e.colorValue < colorMin) colorMin = e.colorValue;
        if (e.colorValue > colorMax) colorMax = e.colorValue;
      }
    }
  }
  const colorSpan = colorMax - colorMin;

  // Same idea for size — normalize to [0.5x, 2.5x] of base radius.
  let sizeMin = Infinity;
  let sizeMax = -Infinity;
  if (sizeIdx >= 0) {
    for (const e of extractions) {
      if (Number.isFinite(e.sizeValue)) {
        if (e.sizeValue < sizeMin) sizeMin = e.sizeValue;
        if (e.sizeValue > sizeMax) sizeMax = e.sizeValue;
      }
    }
  }
  const sizeSpan = sizeMax - sizeMin;

  // ===== Build SceneGroups =====

  const groups: SceneGroup[] = [];
  let groupIdxCounter = 0;

  let bx0 = Infinity;
  let by0 = Infinity;
  let bz0 = Infinity;
  let bx1 = -Infinity;
  let by1 = -Infinity;
  let bz1 = -Infinity;

  for (const [groupValue, bucket] of groupBuckets) {
    // Sort by order column if set — produces a polyline rather than
    // an unordered scatter cloud.
    if (orderIdx >= 0) {
      bucket.sort((a, b) => {
        const ao = Number.isFinite(a.orderValue) ? a.orderValue : Infinity;
        const bo = Number.isFinite(b.orderValue) ? b.orderValue : Infinity;
        return ao - bo;
      });
    }

    const n = bucket.length;
    const positions = new Float32Array(n * 3);
    const rowIndices = new Uint32Array(n);
    const colors = colorIdx >= 0 ? new Float32Array(n * 3) : null;
    const sizes = sizeIdx >= 0 ? new Float32Array(n) : null;

    for (let i = 0; i < n; i++) {
      const e = bucket[i];
      positions[i * 3] = e.position[0];
      positions[i * 3 + 1] = e.position[1];
      positions[i * 3 + 2] = e.position[2];
      rowIndices[i] = e.rowIndex;

      if (e.position[0] < bx0) bx0 = e.position[0];
      if (e.position[1] < by0) by0 = e.position[1];
      if (e.position[2] < bz0) bz0 = e.position[2];
      if (e.position[0] > bx1) bx1 = e.position[0];
      if (e.position[1] > by1) by1 = e.position[1];
      if (e.position[2] > bz1) bz1 = e.position[2];

      if (colors) {
        const t = colorSpan > 0 ? (e.colorValue - colorMin) / colorSpan : 0.5;
        const rgb = colormap(t);
        colors[i * 3] = rgb[0];
        colors[i * 3 + 1] = rgb[1];
        colors[i * 3 + 2] = rgb[2];
      }

      if (sizes) {
        const t =
          sizeSpan > 0 ? (e.sizeValue - sizeMin) / sizeSpan : 0.5;
        // Map [0,1] → [0.5, 2.5] base-radius multiplier.
        sizes[i] = Number.isFinite(t) ? 0.5 + t * 2.0 : 1.0;
      }
    }

    groups.push({
      name: groupValue || 'all',
      positions,
      colors,
      sizes,
      rowIndices,
      isLine: orderIdx >= 0,
      defaultColor:
        groupBuckets.size > 1 ? groupColor(groupIdxCounter) : [0.55, 0.75, 0.95],
    });
    groupIdxCounter++;
  }

  const bounds: SceneBounds = {
    min: [bx0, by0, bz0],
    max: [bx1, by1, bz1],
    center: [(bx0 + bx1) / 2, (by0 + by1) / 2, (bz0 + bz1) / 2],
    radius: Math.max(
      0.001,
      Math.hypot(bx1 - bx0, by1 - by0, bz1 - bz0) / 2,
    ),
  };

  return { groups, bounds, warnings, fatalError: null };
}

// ===== Helpers =====

function emptyScene(msg: string): SceneData {
  return {
    groups: [],
    bounds: emptyBounds(),
    warnings: [],
    fatalError: msg,
  };
}

function emptyBounds(): SceneBounds {
  return {
    min: [0, 0, 0],
    max: [0, 0, 0],
    center: [0, 0, 0],
    radius: 1,
  };
}
