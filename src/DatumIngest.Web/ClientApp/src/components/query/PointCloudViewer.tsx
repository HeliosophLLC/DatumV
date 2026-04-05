import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Canvas, useFrame, useThree } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import type { OrbitControls as OrbitControlsImpl } from 'three-stdlib';
import { MediaPreview } from './MediaPreview';
import { decodeCellBytes } from './decodeCellBytes';
import type { JsonCell } from '@/state/execution';
import { cn } from '@/lib/utils';

interface DecodedCloud {
  positions: Float32Array;
  colors: Uint8Array | null;
  bbox: { min: [number, number, number]; max: [number, number, number] };
  pointCount: number;
  width: number;
  height: number;
  hasColor: boolean;
}

interface DecodeError {
  message: string;
}

/**
 * Modal wrapper — PointCloud cell click handler opens this. Renders
 * <PointCloudViewerBody> inside a MediaPreview portal at 80vw × 80vh.
 */
export function PointCloudViewer({
  cell,
  open,
  onClose,
  title,
}: {
  cell: JsonCell;
  open: boolean;
  onClose: () => void;
  title: string;
}) {
  return (
    <MediaPreview open={open} onClose={onClose} title={title}>
      <div className="bg-background h-[80vh] w-[80vw]">
        <PointCloudViewerBody cell={cell} active={open} />
      </div>
    </MediaPreview>
  );
}

/**
 * The actual viewer — decodes the cell's gzip-base64 payload on mount
 * (gated on `active`) and renders a Three.js scene via react-three-fiber.
 * Rendered both inside <PointCloudViewer> (modal) and inline as the
 * full-pane body when the query result is a single PointCloud value.
 *
 * Three render modes: "points" (always available) splats every vertex as a
 * dot; "voxels" (always available) renders each point as a Minecraft-style
 * cube via InstancedMesh — ideal for voxel-downsampled clouds; "mesh" (only
 * when the cloud is organized) derives implicit triangle topology from the
 * (u, v) grid.
 */
export function PointCloudViewerBody({
  cell,
  active,
}: {
  cell: JsonCell;
  active: boolean;
}) {
  const { t } = useTranslation('query');
  const [decoded, setDecoded] = useState<DecodedCloud | null>(null);
  const [error, setError] = useState<DecodeError | null>(null);
  const [mode, setMode] = useState<'points' | 'voxels' | 'mesh'>('points');
  const [voxelSize, setVoxelSize] = useState<number | null>(null);

  // Decode once when the viewer becomes active. The work is async
  // (DecompressionStream is stream-based) so we hold a cancellation
  // flag to drop the result if the consumer unmounts mid-decode.
  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    setDecoded(null);
    setError(null);
    decodePointCloud(cell)
      .then((d) => {
        if (cancelled) return;
        setDecoded(d);
      })
      .catch((e: Error) => {
        if (cancelled) return;
        setError({ message: e.message });
      });
    return () => {
      cancelled = true;
    };
  }, [active, cell]);

  const organized = decoded !== null && decoded.width > 0 && decoded.height > 0
    && decoded.width * decoded.height === decoded.pointCount;

  // Default voxel cube size: heuristic from bbox + point density. Picks
  // something visible-but-not-overwhelming for typical voxel-downsampled
  // clouds (a 1cm voxel grid produces clouds that look right at cube edge
  // ≈ 0.01). User can override via the slider once a cloud is loaded.
  const defaultVoxelSize = useMemo(() => {
    if (decoded === null) return 0.02;
    const extent = Math.max(
      decoded.bbox.max[0] - decoded.bbox.min[0],
      decoded.bbox.max[1] - decoded.bbox.min[1],
      decoded.bbox.max[2] - decoded.bbox.min[2],
      0.1,
    );
    // Assume points are roughly uniformly spread → cell edge ≈ cube-root of
    // (volume / pointCount). Underestimates for sparse clouds, overestimates
    // for dense ones; close enough as a starting value.
    const volumePerPoint = (extent * extent * extent) / Math.max(decoded.pointCount, 1);
    return Math.cbrt(volumePerPoint);
  }, [decoded]);

  // Reset the slider value whenever a new cloud is decoded.
  useEffect(() => {
    setVoxelSize(defaultVoxelSize);
  }, [defaultVoxelSize]);

  const effectiveVoxelSize = voxelSize ?? defaultVoxelSize;

  // Center the camera on the cloud's bbox, sized so the whole thing fits
  // in view at the chosen FOV. drei's OrbitControls then lets the user
  // orbit around the bbox center; the +2.5× factor leaves breathing room.
  const camera = useMemo(() => {
    if (decoded === null) return null;
    const { min, max } = decoded.bbox;
    const center: [number, number, number] = [
      (min[0] + max[0]) / 2,
      (min[1] + max[1]) / 2,
      (min[2] + max[2]) / 2,
    ];
    const extent = Math.max(
      max[0] - min[0],
      max[1] - min[1],
      max[2] - min[2],
      0.1,
    );
    return {
      center,
      // Pull back along +Z so the cloud (which sits in [-1, 0] Z in the
      // OpenGL frame) is in front of the camera looking down -Z.
      position: [center[0], center[1], center[2] + extent * 2.5] as [number, number, number],
    };
  }, [decoded]);

  return (
    <div className="flex h-full w-full flex-col gap-2">
      <div className="text-muted-foreground flex items-center gap-3 px-2 text-xs">
        {decoded && (
          <>
            <span>
              {decoded.pointCount.toLocaleString()} {t('pointCloudPointsLabel')}
            </span>
            {decoded.hasColor && <span>· {t('pointCloudColored')}</span>}
            {organized && <span>· {decoded.width}×{decoded.height} {t('pointCloudOrganized')}</span>}
          </>
        )}
        {mode === 'voxels' && decoded && (
          <label className="flex items-center gap-2">
            <span>{t('pointCloudVoxelSize')}</span>
            <input
              type="range"
              min={defaultVoxelSize * 0.25}
              max={defaultVoxelSize * 4}
              step={defaultVoxelSize * 0.05}
              value={effectiveVoxelSize}
              onChange={(e) => setVoxelSize(Number(e.target.value))}
              className="w-24"
            />
            <span className="tabular-nums">{effectiveVoxelSize.toFixed(3)}</span>
          </label>
        )}
        <div className="ml-auto flex items-center gap-1">
          <button
            type="button"
            onClick={() => setMode('points')}
            className={cn(
              'rounded-md border px-2 py-1 transition-colors',
              mode === 'points'
                ? 'bg-primary text-primary-foreground border-primary'
                : 'border-border bg-background hover:bg-muted',
            )}
          >
            {t('pointCloudModePoints')}
          </button>
          <button
            type="button"
            onClick={() => setMode('voxels')}
            className={cn(
              'rounded-md border px-2 py-1 transition-colors',
              mode === 'voxels'
                ? 'bg-primary text-primary-foreground border-primary'
                : 'border-border bg-background hover:bg-muted',
            )}
          >
            {t('pointCloudModeVoxels')}
          </button>
          <button
            type="button"
            onClick={() => setMode('mesh')}
            disabled={!organized}
            title={organized ? undefined : t('pointCloudMeshUnavailable')}
            className={cn(
              'rounded-md border px-2 py-1 transition-colors',
              mode === 'mesh'
                ? 'bg-primary text-primary-foreground border-primary'
                : 'border-border bg-background hover:bg-muted disabled:opacity-40 disabled:cursor-not-allowed',
            )}
          >
            {t('pointCloudModeMesh')}
          </button>
        </div>
      </div>
      <div className="bg-[#1a1a1a] relative flex-1 overflow-hidden rounded-md">
        {error && (
          <div className="text-destructive absolute inset-0 flex items-center justify-center text-sm">
            {error.message}
          </div>
        )}
        {!error && !decoded && (
          <div className="text-muted-foreground absolute inset-0 flex items-center justify-center text-sm">
            {t('pointCloudDecoding')}
          </div>
        )}
        {decoded && camera && (
          <Canvas
            camera={{ position: camera.position, fov: 60, near: 0.001, far: 1000 }}
            gl={{ antialias: true, alpha: false }}
          >
            <color attach="background" args={['#1a1a1a']} />
            <ambientLight intensity={0.6} />
            <directionalLight position={[1, 1, 1]} intensity={0.5} />
            {mode === 'points' && <PointsRenderer decoded={decoded} />}
            {mode === 'voxels' && (
              <VoxelsRenderer decoded={decoded} cubeSize={effectiveVoxelSize} />
            )}
            {mode === 'mesh' && <MeshRenderer decoded={decoded} />}
            <axesHelper args={[Math.max(...decoded.bbox.max.map(Math.abs), ...decoded.bbox.min.map(Math.abs)) * 0.5]} />
            <OrbitControlsWithKeyboard target={camera.center} sceneExtent={cameraExtent(decoded)} />
          </Canvas>
        )}
        <div className="text-muted-foreground pointer-events-none absolute bottom-2 right-2 text-[10px] opacity-70">
          {t('pointCloudKeyboardHint')}
        </div>
      </div>
    </div>
  );
}

// ────────── Controls ──────────

/**
 * OrbitControls + WASD/QE first-person translation. Keys translate the
 * camera AND the orbit target together so dragging-to-orbit always pivots
 * around whatever you're looking at, even after walking around. Speed
 * scales with scene extent so the same keypress moves you ~10% of the
 * scene per second regardless of cloud size.
 *
 * Bindings:
 *   W / S — forward / back along camera view direction
 *   A / D — strafe left / right (perpendicular to view, in camera-up plane)
 *   Q / E — drop / rise vertically (world Y axis)
 *   Shift — 3× speed boost
 *
 * Listens at the window level rather than the canvas (canvas keyboard
 * focus is awkward; this lets users start typing immediately after the
 * canvas mounts). Skips when an input/textarea/contenteditable is active
 * so we don't hijack typing.
 */
function OrbitControlsWithKeyboard({
  target,
  sceneExtent,
}: {
  target: [number, number, number];
  sceneExtent: number;
}) {
  const controlsRef = useRef<OrbitControlsImpl>(null!);
  const { camera } = useThree();
  const keys = useRef<Set<string>>(new Set());

  // Set the initial OrbitControls target imperatively — the prop form
  // overrides the target ref every render, and we want WASD updates to
  // stick across frames.
  useEffect(() => {
    if (!controlsRef.current) return;
    controlsRef.current.target.set(target[0], target[1], target[2]);
    controlsRef.current.update();
  }, [target]);

  useEffect(() => {
    const isTypingTarget = (el: Element | null): boolean => {
      if (!el) return false;
      const tag = el.tagName;
      if (tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT') return true;
      if ((el as HTMLElement).isContentEditable) return true;
      return false;
    };

    const down = (e: KeyboardEvent) => {
      if (isTypingTarget(document.activeElement)) return;
      const k = e.key.toLowerCase();
      if ('wasdqe'.includes(k) || k === 'shift') {
        keys.current.add(k);
        // Don't preventDefault — let normal page shortcuts still fire when
        // we're not actively moving (some keys overlap with browser
        // shortcuts but only with Ctrl/Cmd modifiers).
      }
    };
    const up = (e: KeyboardEvent) => { keys.current.delete(e.key.toLowerCase()); };
    const blur = () => { keys.current.clear(); };   // dropped focus = release all keys

    window.addEventListener('keydown', down);
    window.addEventListener('keyup', up);
    window.addEventListener('blur', blur);
    return () => {
      window.removeEventListener('keydown', down);
      window.removeEventListener('keyup', up);
      window.removeEventListener('blur', blur);
    };
  }, []);

  useFrame((_, delta) => {
    if (keys.current.size === 0) return;

    // Speed scales with scene extent so movement feels right whether the
    // scene is a 10cm figurine or a 50m room. ~50% of extent per second at
    // 1x, 150% with shift.
    const baseSpeed = sceneExtent * 0.5 * delta;
    const speed = keys.current.has('shift') ? baseSpeed * 3 : baseSpeed;

    const forward = new THREE.Vector3();
    camera.getWorldDirection(forward);
    forward.normalize();

    // Strafe = forward × world-up, normalized. Using camera.up here would
    // tilt strafe when looking up/down (often undesired); world-up keeps
    // strafe in the ground plane.
    const worldUp = new THREE.Vector3(0, 1, 0);
    const right = new THREE.Vector3().crossVectors(forward, worldUp).normalize();

    const delta3 = new THREE.Vector3();
    if (keys.current.has('w')) delta3.addScaledVector(forward, speed);
    if (keys.current.has('s')) delta3.addScaledVector(forward, -speed);
    if (keys.current.has('d')) delta3.addScaledVector(right, speed);
    if (keys.current.has('a')) delta3.addScaledVector(right, -speed);
    if (keys.current.has('e')) delta3.addScaledVector(worldUp, speed);
    if (keys.current.has('q')) delta3.addScaledVector(worldUp, -speed);

    if (delta3.lengthSq() === 0) return;

    camera.position.add(delta3);
    // Move the orbit target by the same vector so subsequent drag-to-orbit
    // pivots around whatever's currently in front of the camera.
    if (controlsRef.current) {
      controlsRef.current.target.add(delta3);
      controlsRef.current.update();
    }
  });

  return <OrbitControls ref={controlsRef} makeDefault />;
}

/**
 * Largest axis-aligned extent of the cloud's bbox. Used to scale keyboard
 * movement speed so the same keypress moves you ~the same fraction of the
 * scene regardless of cloud size.
 */
function cameraExtent(decoded: DecodedCloud): number {
  return Math.max(
    decoded.bbox.max[0] - decoded.bbox.min[0],
    decoded.bbox.max[1] - decoded.bbox.min[1],
    decoded.bbox.max[2] - decoded.bbox.min[2],
    0.1,
  );
}

// ────────── Renderers ──────────

function PointsRenderer({ decoded }: { decoded: DecodedCloud }) {
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(decoded.positions, 3));
    if (decoded.colors) {
      // normalized=true ⇒ uint8 [0,255] divided by 255 in the shader to
      // produce [0,1] float colors, the standard "vertex color" path.
      g.setAttribute('color', new THREE.BufferAttribute(decoded.colors, 3, true));
    }
    return g;
  }, [decoded]);

  return (
    <points geometry={geometry}>
      <pointsMaterial
        size={pointSize(decoded)}
        sizeAttenuation
        vertexColors={decoded.colors !== null}
        color={decoded.colors !== null ? undefined : '#cccccc'}
      />
    </points>
  );
}

/**
 * Renders each point as a Minecraft-style cube via Three.js InstancedMesh
 * — one BoxGeometry, one draw call, N per-instance matrices + colors.
 * Designed for voxel-downsampled clouds where the data is already a
 * grid-aligned set of cells; cube size should match the cell size used
 * upstream. For raw (non-downsampled) clouds the cubes will overlap
 * heavily, which can still look fine but is computationally wasteful.
 *
 * Performance: ~60 fps for 500K cubes on a 3090. The matrix-and-color
 * setup pass dominates the cost; for clouds beyond ~2M points consider
 * downsampling on the SQL side first.
 */
function VoxelsRenderer({
  decoded,
  cubeSize,
}: {
  decoded: DecodedCloud;
  cubeSize: number;
}) {
  const meshRef = useRef<THREE.InstancedMesh>(null!);
  const geometry = useMemo(() => new THREE.BoxGeometry(1, 1, 1), []);

  // Push per-instance transform + color into the InstancedMesh whenever the
  // cloud or the cube size changes. Three.js needs both `instanceMatrix` and
  // `instanceColor` flagged for upload after we write into them; without
  // that, the GPU keeps showing the previous frame's values.
  useEffect(() => {
    if (!meshRef.current) return;
    const matrix = new THREE.Matrix4();
    const position = new THREE.Vector3();
    const quat = new THREE.Quaternion();   // identity — no per-cube rotation
    const scale = new THREE.Vector3(cubeSize, cubeSize, cubeSize);
    const color = new THREE.Color();

    for (let i = 0; i < decoded.pointCount; i++) {
      position.set(
        decoded.positions[i * 3 + 0],
        decoded.positions[i * 3 + 1],
        decoded.positions[i * 3 + 2],
      );
      matrix.compose(position, quat, scale);
      meshRef.current.setMatrixAt(i, matrix);
      if (decoded.colors) {
        color.setRGB(
          decoded.colors[i * 3 + 0] / 255,
          decoded.colors[i * 3 + 1] / 255,
          decoded.colors[i * 3 + 2] / 255,
        );
        meshRef.current.setColorAt(i, color);
      }
    }
    meshRef.current.instanceMatrix.needsUpdate = true;
    if (meshRef.current.instanceColor) {
      meshRef.current.instanceColor.needsUpdate = true;
    }
  }, [decoded, cubeSize]);

  return (
    <instancedMesh
      ref={meshRef}
      args={[geometry, undefined, decoded.pointCount]}
    >
      <meshStandardMaterial
        color={decoded.colors !== null ? '#ffffff' : '#cccccc'}
        flatShading
      />
    </instancedMesh>
  );
}

function MeshRenderer({ decoded }: { decoded: DecodedCloud }) {
  // Implicit triangle topology from the (u, v) grid: each cell (u, v) →
  // (u+1, v+1) becomes two triangles. Skip cells where any of the four
  // corner depths span more than `discontinuityThreshold` of the bbox Z
  // range — keeps depth-edges from producing rubber-sheet skirts.
  const geometry = useMemo(() => {
    const { positions, colors, width, height, bbox } = decoded;
    const zRange = Math.max(bbox.max[2] - bbox.min[2], 1e-6);
    const discontinuityThreshold = zRange * 0.05; // 5% of Z range

    const indices: number[] = [];
    for (let v = 0; v < height - 1; v++) {
      for (let u = 0; u < width - 1; u++) {
        const a = v * width + u;
        const b = v * width + u + 1;
        const c = (v + 1) * width + u;
        const d = (v + 1) * width + u + 1;
        const za = positions[a * 3 + 2];
        const zb = positions[b * 3 + 2];
        const zc = positions[c * 3 + 2];
        const zd = positions[d * 3 + 2];
        const localRange = Math.max(za, zb, zc, zd) - Math.min(za, zb, zc, zd);
        if (localRange > discontinuityThreshold) continue;
        indices.push(a, c, b, b, c, d);
      }
    }

    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    if (colors) {
      g.setAttribute('color', new THREE.BufferAttribute(colors, 3, true));
    }
    g.setIndex(indices);
    g.computeVertexNormals();
    return g;
  }, [decoded]);

  return (
    <mesh geometry={geometry}>
      <meshStandardMaterial
        vertexColors={decoded.colors !== null}
        color={decoded.colors !== null ? undefined : '#cccccc'}
        side={THREE.DoubleSide}
        flatShading
      />
    </mesh>
  );
}

// ────────── Decode helpers ──────────

/**
 * Pulls the gzip-base64 payload out of the cell, inflates it via the
 * browser-native `DecompressionStream` API, parses the 40-byte PointCloud
 * header, and unpacks positions + colors into typed arrays sized for
 * Three.js BufferAttributes.
 *
 * Format (matches DatumIngest.Model.Spatial.PointCloudHeader, little-endian):
 *   header (40 bytes):
 *     byte  0    : version (= 1)
 *     byte  1    : flags (bit 0 = HasColor, 1 = HasNormals, 2 = HasIntensity)
 *     byte  2    : coordinate frame
 *     byte  3    : reserved
 *     uint32 4   : point count
 *     float32×3  : bbox min (12 bytes)
 *     float32×3  : bbox max (12 bytes)
 *     uint32 32  : width  (0 = unorganized)
 *     uint32 36  : height (0 = unorganized)
 *   per-point payload (16 bytes when HasColor, 12 when position-only):
 *     float32 x, y, z, then optional uint8 r, g, b, a
 */
async function decodePointCloud(cell: JsonCell): Promise<DecodedCloud> {
  const blob = await decodeCellBytes(cell);
  const view = new DataView(blob);
  if (view.byteLength < 40) {
    throw new Error(`PointCloud blob too short: ${view.byteLength} bytes (header is 40)`);
  }

  const version = view.getUint8(0);
  if (version !== 1) {
    throw new Error(`Unsupported PointCloud header version ${version}`);
  }
  const flags = view.getUint8(1);
  const hasColor = (flags & 0x01) !== 0;
  const hasNormals = (flags & 0x02) !== 0;
  const hasIntensity = (flags & 0x04) !== 0;
  if (hasNormals || hasIntensity) {
    throw new Error('PointCloud carries normals or intensity, which the viewer does not yet read');
  }
  const pointCount = view.getUint32(4, true);
  const bbox = {
    min: [view.getFloat32(8, true), view.getFloat32(12, true), view.getFloat32(16, true)] as [number, number, number],
    max: [view.getFloat32(20, true), view.getFloat32(24, true), view.getFloat32(28, true)] as [number, number, number],
  };
  const width = view.getUint32(32, true);
  const height = view.getUint32(36, true);

  const stride = hasColor ? 16 : 12;
  const expectedSize = 40 + pointCount * stride;
  if (view.byteLength < expectedSize) {
    throw new Error(`PointCloud blob truncated: expected ${expectedSize} bytes, got ${view.byteLength}`);
  }

  // Unpack into BufferAttribute-ready typed arrays. One pass over the
  // payload; the JS-side loop is the dominant cost for huge clouds
  // (~50ms per million points).
  const positions = new Float32Array(pointCount * 3);
  const colors = hasColor ? new Uint8Array(pointCount * 3) : null;
  for (let i = 0; i < pointCount; i++) {
    const base = 40 + i * stride;
    positions[i * 3 + 0] = view.getFloat32(base + 0, true);
    positions[i * 3 + 1] = view.getFloat32(base + 4, true);
    positions[i * 3 + 2] = view.getFloat32(base + 8, true);
    if (colors) {
      colors[i * 3 + 0] = view.getUint8(base + 12);
      colors[i * 3 + 1] = view.getUint8(base + 13);
      colors[i * 3 + 2] = view.getUint8(base + 14);
      // alpha byte at base+15 intentionally dropped — Three.js
      // PointsMaterial.vertexColors expects 3-component color.
    }
  }

  return { positions, colors, bbox, pointCount, width, height, hasColor };
}

/**
 * Pick a point size that's visible without overwhelming the scene. Smaller
 * clouds (sparse LiDAR) get larger points so individual points are
 * pickable; dense depth-map clouds get small points so the grid reads
 * as a coherent surface rather than a fuzzy blob.
 */
function pointSize(decoded: DecodedCloud): number {
  const extent = Math.max(
    decoded.bbox.max[0] - decoded.bbox.min[0],
    decoded.bbox.max[1] - decoded.bbox.min[1],
    decoded.bbox.max[2] - decoded.bbox.min[2],
    0.1,
  );
  // Heuristic: scale with bbox extent and inversely with point density.
  const densityFactor = Math.sqrt(1_000_000 / Math.max(decoded.pointCount, 1));
  return (extent / 1000) * densityFactor * 2;
}
