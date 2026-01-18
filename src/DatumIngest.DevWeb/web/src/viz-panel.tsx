// 3D visualization drawer panel. Mounted once (per boot) into
// #viz-drawer; visibility is gated by viz.open in state.ts.
//
// The component pipeline:
//   1. Read focused tab + its lastResult/runningRes via useSnapshot.
//   2. Pick the result set (vizConfig.resultSetIndex; default 0).
//   3. If tab.vizConfig is null, auto-detect roles from the schema and
//      seed it into the proxy so the dropdown selectors reflect what
//      the auto-detection chose.
//   4. Bind the schema+rows+roles into SceneData (memoised).
//   5. Push SceneData into the three.js scene via updateScene().
//
// The three.js handle lives in a useRef — never in valtio — because
// THREE objects have circular internal refs that proxies handle
// poorly. ResizeObserver on the canvas parent drives the renderer's
// viewport updates. Camera/orbit state is owned by OrbitControls and
// also kept off valtio.

import {
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { useSnapshot } from 'valtio';
import { state, viz, type Tab } from './state.js';
import {
  autoDetectRoles,
  bindSceneFromResultSet,
} from './viz-bind.js';
import {
  createScene,
  exportSceneAsGlb,
  updateScene,
  type VizSceneHandle,
} from './viz-three.js';
import type { QueryResult, ResultSet, SchemaColumn } from './result-types.js';
import type { RoleAssignments } from './viz-types.js';

// ===== Top-level =====

export function VizPanel() {
  const snap = useSnapshot(state);
  const vizSnap = useSnapshot(viz);

  if (!vizSnap.open) return null;

  // Find the focused tab. (activeTabId is a getter on state proxying
  // the focused group's value — useSnapshot returns its current
  // resolved value.)
  const tab = snap.tabs.find((t) => t.id === snap.activeTabId);
  if (!tab) {
    return (
      <div className="viz-empty">
        <div className="meta">No active tab.</div>
      </div>
    );
  }

  // Pick a result set: prefer running res for live updates, fall back
  // to lastResult.
  const result =
    (tab.runningRes as QueryResult | null) ??
    (tab.lastResult as QueryResult | null | undefined) ??
    null;

  if (!result || result.error || result.resultSets.length === 0) {
    return (
      <div className="viz-empty">
        <div className="meta">
          {result?.error
            ? `Cannot visualize: ${result.error}`
            : 'No result to visualize. Run a query first.'}
        </div>
      </div>
    );
  }

  const setIndex = tab.vizConfig?.resultSetIndex ?? 0;
  const safeSetIndex = Math.max(
    0,
    Math.min(setIndex, result.resultSets.length - 1),
  );
  const resultSet = result.resultSets[safeSetIndex];
  if (!resultSet || !resultSet.schema) {
    return (
      <div className="viz-empty">
        <div className="meta">Result set has no schema yet.</div>
      </div>
    );
  }

  return (
    <VizPanelInner
      tabId={tab.id}
      tab={tab as Tab}
      resultSet={resultSet as ResultSet}
      resultSetIndex={safeSetIndex}
      resultSetCount={result.resultSets.length}
    />
  );
}

// ===== Inner panel (with resolved data) =====

function VizPanelInner({
  tabId,
  tab,
  resultSet,
  resultSetIndex,
  resultSetCount,
}: {
  tabId: string;
  tab: Tab;
  resultSet: ResultSet;
  resultSetIndex: number;
  resultSetCount: number;
}) {
  const schema = (resultSet.schema ?? []) as readonly SchemaColumn[];

  // Seed vizConfig if it doesn't exist yet for this tab — auto-detect
  // from schema. Done inside an effect so we don't mutate state during
  // render.
  useEffect(() => {
    const liveTab = state.tabs.find((t) => t.id === tabId);
    if (!liveTab) return;
    if (!liveTab.vizConfig) {
      liveTab.vizConfig = {
        resultSetIndex,
        roles: autoDetectRoles(schema),
      };
    }
  }, [tabId, resultSetIndex, schema]);

  // Use the live config as it stands now. Reads via the snapshotted
  // tab; mutations write to state.tabs.find(...).vizConfig.
  const config = tab.vizConfig;
  const roles = (config?.roles ?? autoDetectRoles(schema)) as RoleAssignments;

  // Build SceneData. The dependency on resultSet.rows captures the
  // streaming case — new rows mutate the array via valtio, useSnapshot
  // creates a new snapshot, this useMemo recomputes.
  const sceneData = useMemo(
    () => bindSceneFromResultSet(resultSet, roles),
    [resultSet, roles],
  );

  return (
    <div className="viz-panel">
      <VizHeader
        tabId={tabId}
        roles={roles}
        schema={schema}
        warnings={sceneData.warnings}
        fatalError={sceneData.fatalError}
        resultSetIndex={resultSetIndex}
        resultSetCount={resultSetCount}
      />
      <VizCanvas sceneData={sceneData} />
    </div>
  );
}

// ===== Header (role selectors + download) =====

function VizHeader({
  tabId,
  roles,
  schema,
  warnings,
  fatalError,
  resultSetIndex,
  resultSetCount,
}: {
  tabId: string;
  roles: RoleAssignments;
  schema: readonly SchemaColumn[];
  warnings: readonly string[];
  fatalError: string | null;
  resultSetIndex: number;
  resultSetCount: number;
}) {
  const updateRoles = (patch: Partial<RoleAssignments>) => {
    const liveTab = state.tabs.find((t) => t.id === tabId);
    if (!liveTab) return;
    if (!liveTab.vizConfig) {
      liveTab.vizConfig = {
        resultSetIndex,
        roles: { ...roles, ...patch },
      };
    } else {
      liveTab.vizConfig.roles = { ...liveTab.vizConfig.roles, ...patch };
    }
  };

  const setResultSet = (i: number) => {
    const liveTab = state.tabs.find((t) => t.id === tabId);
    if (!liveTab) return;
    if (!liveTab.vizConfig) {
      liveTab.vizConfig = {
        resultSetIndex: i,
        roles: autoDetectRoles(schema),
      };
    } else {
      liveTab.vizConfig.resultSetIndex = i;
    }
  };

  const close = () => {
    viz.open = false;
  };

  // Column option lists
  const allCols = schema.map((c) => c.name);

  return (
    <div className="viz-header">
      <button
        type="button"
        className="viz-close"
        title="Close viz panel"
        onClick={close}
      >
        ✕
      </button>

      {resultSetCount > 1 && (
        <label className="viz-field">
          <span>Set</span>
          <select
            value={resultSetIndex}
            onChange={(e) => setResultSet(parseInt(e.target.value, 10))}
          >
            {Array.from({ length: resultSetCount }, (_, i) => (
              <option key={i} value={i}>
                {i + 1}
              </option>
            ))}
          </select>
        </label>
      )}

      <label className="viz-field">
        <span>Position</span>
        <select
          value={roles.positionMode}
          onChange={(e) =>
            updateRoles({
              positionMode: e.target.value as 'xyz' | 'point3d',
            })
          }
        >
          <option value="xyz">x/y/z columns</option>
          <option value="point3d">Point3D column</option>
        </select>
      </label>

      {roles.positionMode === 'xyz' ? (
        <>
          <ColumnSelect
            label="X"
            value={roles.xColumn}
            options={allCols}
            onChange={(v) => updateRoles({ xColumn: v })}
          />
          <ColumnSelect
            label="Y"
            value={roles.yColumn}
            options={allCols}
            onChange={(v) => updateRoles({ yColumn: v })}
          />
          <ColumnSelect
            label="Z"
            value={roles.zColumn}
            options={allCols}
            onChange={(v) => updateRoles({ zColumn: v })}
            allowNone
          />
        </>
      ) : (
        <ColumnSelect
          label="Point"
          value={roles.positionColumn}
          options={allCols}
          onChange={(v) => updateRoles({ positionColumn: v })}
        />
      )}

      <ColumnSelect
        label="Color"
        value={roles.colorColumn}
        options={allCols}
        onChange={(v) => updateRoles({ colorColumn: v })}
        allowNone
      />
      <ColumnSelect
        label="Size"
        value={roles.sizeColumn}
        options={allCols}
        onChange={(v) => updateRoles({ sizeColumn: v })}
        allowNone
      />
      <ColumnSelect
        label="Order"
        value={roles.orderColumn}
        options={allCols}
        onChange={(v) => updateRoles({ orderColumn: v })}
        allowNone
      />
      <ColumnSelect
        label="Group"
        value={roles.groupColumn}
        options={allCols}
        onChange={(v) => updateRoles({ groupColumn: v })}
        allowNone
      />

      <span className="spacer" />

      <DownloadGlbButton />

      {(fatalError || warnings.length > 0) && (
        <div className="viz-warnings">
          {fatalError && <div className="error-line">{fatalError}</div>}
          {warnings.map((w, i) => (
            <div key={i} className="warn-line">
              {w}
            </div>
          ))}
        </div>
      )}
    </div>
  );
}

function ColumnSelect({
  label,
  value,
  options,
  onChange,
  allowNone = false,
}: {
  label: string;
  value: string | null;
  options: readonly string[];
  onChange: (value: string | null) => void;
  allowNone?: boolean;
}) {
  return (
    <label className="viz-field">
      <span>{label}</span>
      <select
        value={value ?? ''}
        onChange={(e) => onChange(e.target.value || null)}
      >
        {allowNone && <option value="">(none)</option>}
        {!allowNone && value === null && <option value="">— pick —</option>}
        {options.map((name) => (
          <option key={name} value={name}>
            {name}
          </option>
        ))}
      </select>
    </label>
  );
}

// ===== Canvas =====

function VizCanvas({ sceneData }: { sceneData: ReturnType<typeof bindSceneFromResultSet> }) {
  const containerRef = useRef<HTMLDivElement>(null);
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const handleRef = useRef<VizSceneHandle | null>(null);

  // Initialise three.js scene once on mount; tear down on unmount.
  useLayoutEffect(() => {
    const canvas = canvasRef.current;
    const container = containerRef.current;
    if (!canvas || !container) return;

    const handle = createScene(canvas);
    handleRef.current = handle;

    // Resize canvas to its container and keep it in sync.
    const sync = () => {
      handle.resize(container.clientWidth, container.clientHeight);
    };
    sync();
    const ro = new ResizeObserver(sync);
    ro.observe(container);

    return () => {
      ro.disconnect();
      handle.dispose();
      handleRef.current = null;
    };
  }, []);

  // Push new SceneData into the scene whenever it changes.
  useEffect(() => {
    const handle = handleRef.current;
    if (!handle) return;
    updateScene(handle, sceneData);
  }, [sceneData]);

  // Expose the handle on the container for the download button to find.
  // (Could pass via context, but the button is in a sibling header so
  // this is simpler than threading a ref up and back down.)
  useEffect(() => {
    const container = containerRef.current;
    if (!container || !handleRef.current) return;
    (container as HTMLDivElement & { _vizHandle?: VizSceneHandle })._vizHandle =
      handleRef.current;
  });

  return (
    <div className="viz-canvas-host" ref={containerRef}>
      <canvas ref={canvasRef} />
    </div>
  );
}

// ===== Download button =====

function DownloadGlbButton() {
  const [status, setStatus] = useState<'idle' | 'busy' | 'error'>('idle');

  const onClick = async () => {
    // Find the rendered scene by walking the DOM. Crude but effective —
    // there's exactly one viz panel mounted at a time.
    const host = document.querySelector('.viz-canvas-host') as
      | (HTMLDivElement & { _vizHandle?: VizSceneHandle })
      | null;
    const handle = host?._vizHandle;
    if (!handle) {
      console.warn('[Viz] No scene handle to export');
      return;
    }
    setStatus('busy');
    try {
      const blob = await exportSceneAsGlb(handle);
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = `datum-viz-${Date.now()}.glb`;
      document.body.appendChild(a);
      a.click();
      a.remove();
      setTimeout(() => URL.revokeObjectURL(url), 1000);
      setStatus('idle');
    } catch (err) {
      console.error('[Viz] glTF export failed:', err);
      setStatus('error');
      setTimeout(() => setStatus('idle'), 2000);
    }
  };

  return (
    <button
      type="button"
      className="viz-download"
      title="Download current scene as glTF binary (.glb)"
      onClick={onClick}
      disabled={status === 'busy'}
    >
      {status === 'busy' ? 'exporting…' : status === 'error' ? 'failed' : 'Download .glb'}
    </button>
  );
}
