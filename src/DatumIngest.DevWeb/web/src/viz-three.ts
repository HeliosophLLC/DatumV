// three.js scene management + GLTFExporter wrapper. Takes the
// render-agnostic SceneData from viz-bind.ts and produces a real
// THREE.Scene with one THREE.Points or THREE.Line per SceneGroup.
//
// The lifecycle is split: createScene() initialises a renderer + camera
// + controls bound to a canvas, returning a handle. updateScene()
// rebuilds the meshes from new SceneData (disposing previous ones).
// dispose() tears everything down — required because three.js objects
// hold native GL resources outside the JS heap.

import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls.js';
import { GLTFExporter } from 'three/examples/jsm/exporters/GLTFExporter.js';
import type { SceneData, SceneGroup } from './viz-types.js';

export interface VizSceneHandle {
  scene: THREE.Scene;
  camera: THREE.PerspectiveCamera;
  renderer: THREE.WebGLRenderer;
  controls: OrbitControls;
  // Container for all data meshes — separate from the scene root so
  // updateScene can clear/rebuild without touching grid / lights.
  dataRoot: THREE.Group;
  // Resize handler — call from a ResizeObserver on the canvas parent.
  resize: (width: number, height: number) => void;
  // Tear down GL resources, dispose geometries / materials, drop
  // event listeners, and stop the render loop.
  dispose: () => void;
  // Manual render (called every frame from the render loop).
  render: () => void;
}

export function createScene(canvas: HTMLCanvasElement): VizSceneHandle {
  const scene = new THREE.Scene();
  scene.background = new THREE.Color(0x0e1116);

  const camera = new THREE.PerspectiveCamera(50, 1, 0.01, 1e9);
  camera.position.set(2, 2, 5);
  camera.lookAt(0, 0, 0);

  const renderer = new THREE.WebGLRenderer({
    canvas,
    antialias: true,
    alpha: true,
  });
  renderer.setPixelRatio(window.devicePixelRatio);

  const controls = new OrbitControls(camera, canvas);
  controls.enableDamping = true;
  controls.dampingFactor = 0.08;

  const dataRoot = new THREE.Group();
  scene.add(dataRoot);

  // Subtle ambient + directional so any future mesh materials look
  // reasonable. Points materials don't need lighting but cheap to
  // include for forward compatibility.
  scene.add(new THREE.AmbientLight(0xffffff, 0.6));
  const dir = new THREE.DirectionalLight(0xffffff, 0.6);
  dir.position.set(1, 2, 1);
  scene.add(dir);

  // Animation loop — drives controls damping and re-renders. Stopped
  // by dispose().
  let frameHandle = 0;
  const tick = () => {
    controls.update();
    renderer.render(scene, camera);
    frameHandle = requestAnimationFrame(tick);
  };
  frameHandle = requestAnimationFrame(tick);

  const resize = (width: number, height: number) => {
    if (width <= 0 || height <= 0) return;
    renderer.setSize(width, height, false);
    camera.aspect = width / height;
    camera.updateProjectionMatrix();
  };

  const dispose = () => {
    cancelAnimationFrame(frameHandle);
    controls.dispose();
    disposeDataMeshes(dataRoot);
    renderer.dispose();
  };

  const render = () => {
    renderer.render(scene, camera);
  };

  return {
    scene,
    camera,
    renderer,
    controls,
    dataRoot,
    resize,
    dispose,
    render,
  };
}

// Walk the dataRoot, dispose every geometry + material, and clear it.
// Called by both updateScene (before adding new meshes) and dispose
// (final teardown).
function disposeDataMeshes(dataRoot: THREE.Group): void {
  const removed: THREE.Object3D[] = [];
  dataRoot.traverse((obj) => {
    if (obj === dataRoot) return;
    const mesh = obj as THREE.Mesh & { material?: THREE.Material | THREE.Material[]; geometry?: THREE.BufferGeometry };
    if (mesh.geometry) mesh.geometry.dispose();
    if (mesh.material) {
      const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
      for (const m of mats) m.dispose();
    }
    removed.push(obj);
  });
  for (const obj of removed) dataRoot.remove(obj);
}

// ===== Update from SceneData =====

export function updateScene(handle: VizSceneHandle, data: SceneData): void {
  disposeDataMeshes(handle.dataRoot);

  if (data.groups.length === 0) return;

  for (const group of data.groups) {
    const obj = group.isLine ? buildLine(group) : buildPoints(group);
    handle.dataRoot.add(obj);
  }

  // Re-frame camera on the new bounds. Set the OrbitControls target to
  // the data center, position the camera back along +z relative to the
  // bounding sphere radius. radius * 2 leaves headroom on first paint.
  const { center, radius } = data.bounds;
  handle.controls.target.set(center[0], center[1], center[2]);
  const camDist = Math.max(radius * 2.5, 1);
  handle.camera.position.set(
    center[0] + camDist * 0.6,
    center[1] + camDist * 0.6,
    center[2] + camDist * 0.6,
  );
  handle.camera.near = Math.max(0.001, radius / 1000);
  handle.camera.far = Math.max(10, radius * 100);
  handle.camera.updateProjectionMatrix();
  handle.controls.update();
}

function buildPoints(group: SceneGroup): THREE.Points {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute(
    'position',
    new THREE.BufferAttribute(group.positions, 3),
  );
  if (group.colors) {
    geometry.setAttribute('color', new THREE.BufferAttribute(group.colors, 3));
  }
  const material = new THREE.PointsMaterial({
    size: 0.02 * pointSizeBaseFor(group),
    sizeAttenuation: false,
    vertexColors: !!group.colors,
    color: group.colors ? 0xffffff : rgbToHex(group.defaultColor),
  });
  const points = new THREE.Points(geometry, material);
  points.name = group.name;
  return points;
}

function buildLine(group: SceneGroup): THREE.Line {
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute(
    'position',
    new THREE.BufferAttribute(group.positions, 3),
  );
  if (group.colors) {
    geometry.setAttribute('color', new THREE.BufferAttribute(group.colors, 3));
  }
  const material = new THREE.LineBasicMaterial({
    vertexColors: !!group.colors,
    color: group.colors ? 0xffffff : rgbToHex(group.defaultColor),
    linewidth: 1, // most browsers ignore > 1; cosmetic only
  });
  const line = new THREE.Line(geometry, material);
  line.name = group.name;
  return line;
}

// PointsMaterial.size is in world units (not pixels). 2% of nothing
// produces invisible points when bounds are tiny, so use the bounds
// extent if you have it — but viz-bind already normalised positions
// in their own coordinate space, so just use a fixed pixel-scaled
// value here. THREE.PointsMaterial with sizeAttenuation: false makes
// `size` measured in screen pixels.
function pointSizeBaseFor(_group: SceneGroup): number {
  // 4 pixels at default — visible without dominating.
  return 200;
}

function rgbToHex(rgb: [number, number, number]): number {
  const r = Math.round(Math.max(0, Math.min(1, rgb[0])) * 255);
  const g = Math.round(Math.max(0, Math.min(1, rgb[1])) * 255);
  const b = Math.round(Math.max(0, Math.min(1, rgb[2])) * 255);
  return (r << 16) | (g << 8) | b;
}

// ===== glTF export =====

// Returns a Promise resolving to a Blob with the .glb (binary glTF)
// contents. Uses GLTFExporter's `binary: true` for a single-file
// download; switch to `binary: false` if a JSON .gltf is preferred.
export function exportSceneAsGlb(handle: VizSceneHandle): Promise<Blob> {
  return new Promise((resolve, reject) => {
    const exporter = new GLTFExporter();
    exporter.parse(
      handle.scene,
      (result) => {
        if (result instanceof ArrayBuffer) {
          resolve(new Blob([result], { type: 'model/gltf-binary' }));
        } else {
          // GLTFExporter returned the JSON form — wrap as Blob.
          resolve(
            new Blob([JSON.stringify(result)], { type: 'model/gltf+json' }),
          );
        }
      },
      (error) => reject(error),
      { binary: true },
    );
  });
}
