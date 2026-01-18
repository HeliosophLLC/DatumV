// Persisted viz role assignments (per tab) + transient scene data
// (rebuilt every render). Pure data — no three.js imports.
//
// Roles are how the viz panel decides "what does this column mean":
//
//   position  — where to put the point in 3D space. Either a single
//               Point3D column, or three numeric columns (x/y/z).
//   color     — numeric column mapped through a continuous colormap.
//               null → all points one color.
//   size      — numeric column mapped to point radius. null → uniform.
//   order     — numeric column. If set, rows are sorted by this and
//               connected as a polyline; otherwise rendered as scatter.
//   group     — string column. Each unique value renders as its own
//               cloud / line in distinct colors. null → one group.

export type PositionMode = 'xyz' | 'point3d';

export interface RoleAssignments {
  positionMode: PositionMode;
  // For positionMode === 'xyz' — three numeric column names.
  xColumn: string | null;
  yColumn: string | null;
  zColumn: string | null;
  // For positionMode === 'point3d' — one Point3D-typed column name.
  positionColumn: string | null;
  colorColumn: string | null;
  sizeColumn: string | null;
  orderColumn: string | null;
  groupColumn: string | null;
}

export interface VizConfig {
  // Which result set this config applies to. Multi-statement queries
  // produce N sets; the panel renders one set at a time.
  resultSetIndex: number;
  roles: RoleAssignments;
}

// ===== Transient scene shape =====
//
// Output of viz-bind.ts. `viz-three.ts` converts this into THREE.Points
// or THREE.Line instances. Float32Array buffers transfer cleanly into
// three.js BufferGeometry without copies.

export interface SceneGroup {
  name: string;
  // n*3 floats: x0,y0,z0,x1,y1,z1,...
  positions: Float32Array;
  // n*3 RGB floats in [0,1], or null for uniform color (defaultColor).
  colors: Float32Array | null;
  // n floats giving point radius, or null for uniform size.
  sizes: Float32Array | null;
  // Map vertex i back to source row index — used for selection sync.
  rowIndices: Uint32Array;
  // true → render as connected polyline; false → scatter points.
  isLine: boolean;
  // Default color when colors === null. RGB in [0,1].
  defaultColor: [number, number, number];
}

export interface SceneBounds {
  min: [number, number, number];
  max: [number, number, number];
  center: [number, number, number];
  radius: number;
}

export interface SceneData {
  groups: SceneGroup[];
  bounds: SceneBounds;
  // Non-fatal warnings the panel surfaces in its header — e.g.,
  // "Order column 'MD' is not numeric, ignored".
  warnings: string[];
  // Empty when groups is empty AND nothing went wrong (e.g., no rows).
  // Set when configuration is invalid (e.g., missing position columns).
  fatalError: string | null;
}
