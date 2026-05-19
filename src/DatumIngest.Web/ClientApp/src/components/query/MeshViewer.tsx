import { useEffect, useMemo, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Canvas } from '@react-three/fiber';
import { OrbitControls } from '@react-three/drei';
import * as THREE from 'three';
import { MediaPreview } from './MediaPreview';
import { decodeCellBytes } from './decodeCellBytes';
import type { JsonCell } from '@/state/execution';

interface DecodedMesh {
  positions: Float32Array;
  colors: Uint8Array | null;
  normals: Float32Array | null;
  indices: Uint32Array;
  bbox: { min: [number, number, number]; max: [number, number, number] };
  vertexCount: number;
  triangleCount: number;
  hasColor: boolean;
  hasNormals: boolean;
}

interface DecodeError {
  message: string;
}

/**
 * Modal wrapper — Mesh cell click handler opens this. Renders
 * <MeshViewerBody> inside a MediaPreview portal at 80vw × 80vh.
 */
export function MeshViewer({
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
        <MeshViewerBody cell={cell} active={open} />
      </div>
    </MediaPreview>
  );
}

/**
 * The actual viewer — decodes the cell's gzip-base64 payload on mount
 * (gated on `active`) and renders a Three.js scene via react-three-fiber.
 * Rendered both inside <MeshViewer> (modal) and inline as the full-pane
 * body when the query result is a single Mesh value.
 *
 * Uses <mesh> + <meshStandardMaterial> with vertex colors (when HasColor)
 * and smooth shading via per-vertex normals (always present on Phase 1
 * meshes from mesh_from_organized).
 */
export function MeshViewerBody({
  cell,
  active,
}: {
  cell: JsonCell;
  active: boolean;
}) {
  const { t } = useTranslation('query');
  const [decoded, setDecoded] = useState<DecodedMesh | null>(null);
  const [error, setError] = useState<DecodeError | null>(null);

  useEffect(() => {
    if (!active) return;
    let cancelled = false;
    setDecoded(null);
    setError(null);
    decodeMesh(cell)
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

  // Camera framing — same approach as PointCloudViewer: center on bbox,
  // pull back along +Z by 2.5× the bbox extent so the mesh fits in view
  // and the user can orbit around the centroid.
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
      position: [center[0], center[1], center[2] + extent * 2.5] as [number, number, number],
    };
  }, [decoded]);

  return (
    <div className="flex h-full w-full flex-col gap-2">
      <div className="text-muted-foreground flex items-center gap-3 px-2 text-xs">
        {decoded && (
          <>
            <span>
              {decoded.vertexCount.toLocaleString()} {t('meshVerticesLabel')}
            </span>
            <span>
              · {decoded.triangleCount.toLocaleString()} {t('meshTrianglesLabel')}
            </span>
            {decoded.hasColor && <span>· {t('meshColored')}</span>}
            {decoded.hasNormals && <span>· {t('meshShaded')}</span>}
          </>
        )}
      </div>
      <div className="bg-[#1a1a1a] relative flex-1 overflow-hidden rounded-md">
        {error && (
          <div className="text-destructive absolute inset-0 flex items-center justify-center text-sm">
            {error.message}
          </div>
        )}
        {!error && !decoded && (
          <div className="text-muted-foreground absolute inset-0 flex items-center justify-center text-sm">
            {t('meshDecoding')}
          </div>
        )}
        {decoded && camera && (
          <Canvas
            camera={{ position: camera.position, fov: 60, near: 0.001, far: 1000 }}
            gl={{ antialias: true, alpha: false }}
          >
            <color attach="background" args={['#1a1a1a']} />
            <ambientLight intensity={0.6} />
            <directionalLight position={[1, 1, 1]} intensity={0.7} />
            <directionalLight position={[-1, -0.5, -1]} intensity={0.3} />
            <MeshRenderer decoded={decoded} />
            <axesHelper
              args={[
                Math.max(
                  ...decoded.bbox.max.map(Math.abs),
                  ...decoded.bbox.min.map(Math.abs),
                ) * 0.5,
              ]}
            />
            <OrbitControls target={camera.center} makeDefault />
          </Canvas>
        )}
      </div>
    </div>
  );
}

// ────────── Renderer ──────────

function MeshRenderer({ decoded }: { decoded: DecodedMesh }) {
  const geometry = useMemo(() => {
    const g = new THREE.BufferGeometry();
    g.setAttribute('position', new THREE.BufferAttribute(decoded.positions, 3));
    if (decoded.colors) {
      // normalized=true → uint8 [0,255] divided by 255 in the shader.
      g.setAttribute('color', new THREE.BufferAttribute(decoded.colors, 3, true));
    }
    if (decoded.normals) {
      g.setAttribute('normal', new THREE.BufferAttribute(decoded.normals, 3));
    } else {
      // Defensive — Phase 1 meshes always carry normals, but the format
      // permits absence. computeVertexNormals derives them from face
      // geometry so meshStandardMaterial still gets a valid normal source.
      g.computeVertexNormals();
    }
    g.setIndex(new THREE.BufferAttribute(decoded.indices, 1));
    return g;
  }, [decoded]);

  return (
    <mesh geometry={geometry}>
      <meshStandardMaterial
        vertexColors={decoded.hasColor}
        color={decoded.hasColor ? undefined : '#cccccc'}
        side={THREE.DoubleSide}
        flatShading={!decoded.hasNormals}
      />
    </mesh>
  );
}

// ────────── Decoder ──────────

/**
 * Pulls the gzip-base64 payload out of the cell, inflates it via
 * <c>decodeCellBytes</c>, parses the 48-byte MeshHeader, and unpacks
 * vertices + indices into typed arrays sized for Three.js
 * BufferAttributes.
 *
 * Header layout (matches Heliosoph.DatumV.Model.Spatial.MeshHeader, little-endian):
 *   byte  0    : version (= 1)
 *   byte  1    : flags (bit 0 = HasColor, 1 = HasNormals, 2 = HasUVs, 3 = HasTexture)
 *   byte  2    : coordinate frame
 *   byte  3    : reserved
 *   uint32 4   : vertex count
 *   uint32 8   : triangle count
 *   float32×3  : bbox min (12 bytes)
 *   float32×3  : bbox max (12 bytes)
 *   uint32 36  : texture offset (0 in Phase 1)
 *   uint32 40  : texture length (0 in Phase 1)
 *   pad to 48
 *
 * Vertex payload follows the header, stride derived from flags
 * (pos 12 + color 4 if HasColor + normal 12 if HasNormals + uv 8 if HasUVs).
 * Triangle indices (3 × uint32 per triangle) follow the vertex payload.
 */
async function decodeMesh(cell: JsonCell): Promise<DecodedMesh> {
  const blob = await decodeCellBytes(cell);
  const view = new DataView(blob);

  if (view.byteLength < 48) {
    throw new Error(`Mesh blob too short: ${view.byteLength} bytes (header is 48)`);
  }

  const version = view.getUint8(0);
  if (version !== 1) {
    throw new Error(`Unsupported Mesh header version ${version}`);
  }
  const flags = view.getUint8(1);
  const hasColor = (flags & 0x01) !== 0;
  const hasNormals = (flags & 0x02) !== 0;
  const hasUVs = (flags & 0x04) !== 0;
  const hasTexture = (flags & 0x08) !== 0;
  if (hasUVs || hasTexture) {
    throw new Error(
      'This Mesh carries UV coordinates or an embedded texture, '
      + 'which the viewer does not yet support.',
    );
  }

  const vertexCount = view.getUint32(4, true);
  const triangleCount = view.getUint32(8, true);
  const bbox = {
    min: [
      view.getFloat32(12, true),
      view.getFloat32(16, true),
      view.getFloat32(20, true),
    ] as [number, number, number],
    max: [
      view.getFloat32(24, true),
      view.getFloat32(28, true),
      view.getFloat32(32, true),
    ] as [number, number, number],
  };

  // Compute per-vertex stride from flags.
  const stride =
    12 // position
    + (hasColor ? 4 : 0)
    + (hasNormals ? 12 : 0);

  const expectedSize =
    48 + vertexCount * stride + triangleCount * 12;
  if (view.byteLength < expectedSize) {
    throw new Error(
      `Mesh blob truncated: expected ${expectedSize} bytes, got ${view.byteLength}`,
    );
  }

  // Unpack vertex attributes.
  const positions = new Float32Array(vertexCount * 3);
  const colors = hasColor ? new Uint8Array(vertexCount * 3) : null;
  const normals = hasNormals ? new Float32Array(vertexCount * 3) : null;
  const normalOffset = 12 + (hasColor ? 4 : 0);
  for (let i = 0; i < vertexCount; i++) {
    const base = 48 + i * stride;
    positions[i * 3 + 0] = view.getFloat32(base + 0, true);
    positions[i * 3 + 1] = view.getFloat32(base + 4, true);
    positions[i * 3 + 2] = view.getFloat32(base + 8, true);
    if (colors) {
      colors[i * 3 + 0] = view.getUint8(base + 12);
      colors[i * 3 + 1] = view.getUint8(base + 13);
      colors[i * 3 + 2] = view.getUint8(base + 14);
      // alpha byte at base+15 dropped — meshStandardMaterial.vertexColors expects 3-component
    }
    if (normals) {
      normals[i * 3 + 0] = view.getFloat32(base + normalOffset + 0, true);
      normals[i * 3 + 1] = view.getFloat32(base + normalOffset + 4, true);
      normals[i * 3 + 2] = view.getFloat32(base + normalOffset + 8, true);
    }
  }

  // Unpack triangle indices.
  const indicesBase = 48 + vertexCount * stride;
  const indices = new Uint32Array(triangleCount * 3);
  for (let i = 0; i < triangleCount * 3; i++) {
    indices[i] = view.getUint32(indicesBase + i * 4, true);
  }

  return {
    positions,
    colors,
    normals,
    indices,
    bbox,
    vertexCount,
    triangleCount,
    hasColor,
    hasNormals,
  };
}
