import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import maplibregl from 'maplibre-gl';
import 'maplibre-gl/dist/maplibre-gl.css';
import type { Feature, FeatureCollection, Point } from 'geojson';
import { isHttpUrl, openExternalUrl } from '@/lib/openExternal';
import { settingsState } from '@/state/settings';
import type { CellResult, JsonCell } from '@/state/execution';

// Map rendering of a query result whose schema carries latitude/longitude
// columns. Points render as GPU circle layers with clustering — DOM markers
// don't survive metro-density data where many rows share a building — and
// clusters that can't unclutter by zooming (same-address stacks) open a
// row-list popup instead. Loaded lazily from ResultsPane so maplibre-gl
// stays out of the startup bundle.
//
// Basemap tiles come from CARTO's public raster basemaps (light/dark to
// follow the app theme) with OSM attribution; a configurable tile URL is
// the upgrade path if usage ever outgrows the courtesy tier.
const LIGHT_TILES = [
  'https://a.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png',
  'https://b.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png',
  'https://c.basemaps.cartocdn.com/light_all/{z}/{x}/{y}.png',
];
const DARK_TILES = [
  'https://a.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png',
  'https://b.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png',
  'https://c.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}.png',
];
const TILE_ATTRIBUTION = '© OpenStreetMap contributors © CARTO';
// Cluster count labels are symbol layers, which need a glyph endpoint —
// CARTO serves these alongside its basemaps. If the fetch fails the counts
// degrade to unlabeled bubbles and the map error handler logs it.
const GLYPHS_URL = 'https://tiles.basemaps.cartocdn.com/fonts/{fontstack}/{range}.pbf';

// GeoJSON sources handle far more than DOM markers ever could; this cap is
// about popup/legend sanity, not rendering.
const MAX_PINS = 20_000;

// Beyond this zoom, clustering stops — coincident same-address points can
// never separate spatially, so their cluster click opens a row list.
const CLUSTER_MAX_ZOOM = 15;

// How many non-coordinate columns a popup lists before cutting off.
const MAX_POPUP_FIELDS = 8;

// Categorical palette for color-by-column — same Tableau-10 set the
// engine's plot_scatter_agg uses, so map and chart colors agree.
const CATEGORY_PALETTE = [
  '#4e79a7', '#f28e2b', '#e15759', '#76b7b2', '#59a14f',
  '#edc948', '#b07aa1', '#ff9da7', '#9c755f', '#bab0ac',
];
const OVERFLOW_COLOR = '#9ca3af';
const DEFAULT_POINT_COLOR = '#2563eb';

// Sampling bounds for the data-driven pickers (coordinate candidates and
// color-by candidates) — enough rows to be representative, cheap per render.
const COLUMN_SAMPLE_ROWS = 100;
// A column qualifies for color-by when its sampled distinct values fit a
// categorical read; beyond this it's an identifier, not a category.
const MAX_COLOR_CATEGORIES = 12;

interface MapPoint {
  /** Index into cell.rows — the shared key linking pins to grid rows. */
  rowIndex: number;
  lat: number;
  lon: number;
  colorValue: string;
  fields: { name: string; text: string; isUrl: boolean }[];
}

/**
 * Extracts pin data from the result rows. Rows with a null or unparseable
 * coordinate are skipped (a `No_Match` geocode row simply doesn't pin).
 */
function extractPoints(
  cell: CellResult,
  latIndex: number,
  lonIndex: number,
  colorColumn: number | null,
): { points: MapPoint[]; skipped: number; truncated: number } {
  const points: MapPoint[] = [];
  let skipped = 0;
  let truncated = 0;
  const schema = cell.schema!;

  for (let rowIndex = 0; rowIndex < cell.rows.length; rowIndex++) {
    const row = cell.rows[rowIndex];
    const lat = coordinateOf(row[latIndex]);
    const lon = coordinateOf(row[lonIndex]);
    if (lat === null || lon === null) {
      skipped++;
      continue;
    }
    if (points.length >= MAX_PINS) {
      truncated++;
      continue;
    }

    const fields: MapPoint['fields'] = [];
    for (let i = 0; i < row.length && fields.length < MAX_POPUP_FIELDS; i++) {
      if (i === latIndex || i === lonIndex) continue;
      const value = row[i];
      if (value.kind === 'null') continue;
      // Popups carry the scalar-text columns (names, addresses, websites);
      // media/array/struct cells stay in the grid where their renderers live.
      if (value.kind !== 'text' && value.kind !== 'json') continue;
      const text = value.text ?? '';
      if (text.length === 0) continue;
      fields.push({
        name: schema[i]?.name ?? `#${i}`,
        text,
        isUrl: isHttpUrl(text),
      });
    }
    const colorCell = colorColumn !== null ? row[colorColumn] : undefined;
    points.push({
      rowIndex,
      lat,
      lon,
      colorValue: colorCell !== undefined && colorCell.kind === 'text' ? (colorCell.text ?? '') : '',
      fields,
    });
  }
  return { points, skipped, truncated };
}

function coordinateOf(cell: JsonCell | undefined): number | null {
  if (!cell || cell.kind === 'null' || cell.text === undefined) return null;
  const value = Number.parseFloat(cell.text);
  if (!Number.isFinite(value)) return null;
  return value;
}

/**
 * Picks the candidate column whose sampled rows actually contain parseable
 * coordinates. Joins commonly produce two coordinate pairs — a source
 * table's own (frequently empty) Latitude/Longitude columns plus the
 * geocoded ones — and position in the schema says nothing about which pair
 * holds data.
 */
function pickCoordinateColumn(cell: CellResult, candidates: readonly number[]): number {
  if (candidates.length === 1) return candidates[0];
  const sample = Math.min(cell.rows.length, COLUMN_SAMPLE_ROWS);
  let best = candidates[0];
  let bestCount = -1;
  for (const index of candidates) {
    let count = 0;
    for (let r = 0; r < sample; r++) {
      if (coordinateOf(cell.rows[r]?.[index]) !== null) count++;
    }
    if (count > bestCount) {
      bestCount = count;
      best = index;
    }
  }
  return best;
}

/**
 * String columns whose sampled distinct values look categorical — status
 * fields, cluster labels, states — offered in the color-by picker.
 */
function findColorCandidates(cell: CellResult): { index: number; name: string }[] {
  const schema = cell.schema;
  if (schema === null) return [];
  const sample = Math.min(cell.rows.length, COLUMN_SAMPLE_ROWS);
  const candidates: { index: number; name: string }[] = [];
  for (let i = 0; i < schema.length; i++) {
    if (schema[i].kind !== 'String' || schema[i].isArray) continue;
    const distinct = new Set<string>();
    let hasValue = false;
    for (let r = 0; r < sample && distinct.size <= MAX_COLOR_CATEGORIES; r++) {
      const value = cell.rows[r]?.[i];
      if (!value || value.kind !== 'text') continue;
      const text = value.text ?? '';
      if (text.length === 0) continue;
      hasValue = true;
      distinct.add(text);
    }
    if (hasValue && distinct.size >= 2 && distinct.size <= MAX_COLOR_CATEGORIES) {
      candidates.push({ index: i, name: schema[i].name });
    }
  }
  return candidates;
}

/**
 * Frequency-ranked value→color assignment; the top palette-sized values get
 * distinct colors, the tail shares the overflow gray.
 */
function buildColorMap(points: readonly MapPoint[]): Map<string, string> {
  const counts = new Map<string, number>();
  for (const point of points) {
    if (point.colorValue.length === 0) continue;
    counts.set(point.colorValue, (counts.get(point.colorValue) ?? 0) + 1);
  }
  const ranked = [...counts.entries()].sort((a, b) => b[1] - a[1]);
  const colors = new Map<string, string>();
  ranked.forEach(([value], rank) => {
    colors.set(value, rank < CATEGORY_PALETTE.length ? CATEGORY_PALETTE[rank] : OVERFLOW_COLOR);
  });
  return colors;
}

function toFeatureCollection(
  points: readonly MapPoint[],
  colorMap: Map<string, string> | null,
): FeatureCollection<Point> {
  return {
    type: 'FeatureCollection',
    features: points.map((point) => ({
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [point.lon, point.lat] },
      properties: {
        rowIndex: point.rowIndex,
        color: colorMap?.get(point.colorValue) ?? DEFAULT_POINT_COLOR,
      },
    })),
  };
}

/**
 * Bounds over the central mass of the points: the initial framing ignores
 * the outer 2% per side so one geocode that landed in the wrong state
 * doesn't zoom the whole view out to the continent. Small sets frame fully.
 */
function robustBounds(points: readonly MapPoint[]): maplibregl.LngLatBounds {
  const lats = points.map((p) => p.lat).sort((a, b) => a - b);
  const lons = points.map((p) => p.lon).sort((a, b) => a - b);
  const lower = points.length >= 20 ? Math.floor(points.length * 0.02) : 0;
  const upper = points.length - 1 - lower;
  const bounds = new maplibregl.LngLatBounds();
  bounds.extend([lons[lower], lats[lower]]);
  bounds.extend([lons[upper], lats[upper]]);
  return bounds;
}

/**
 * Popup content built with DOM APIs (never innerHTML — cell text is data).
 * URL values render as a launch button through the same openExternalUrl
 * bridge the grid's link cells use.
 */
function buildPopupContent(point: MapPoint): HTMLElement {
  const root = document.createElement('div');
  root.className = 'flex max-w-[260px] flex-col gap-1 font-sans text-xs text-neutral-900';
  for (const field of point.fields) {
    const row = document.createElement('div');
    const name = document.createElement('span');
    name.className = 'mr-1 font-semibold';
    name.textContent = field.name;
    row.appendChild(name);
    if (field.isUrl) {
      const link = document.createElement('button');
      link.type = 'button';
      link.className = 'cursor-pointer break-all text-blue-700 underline';
      link.textContent = field.text;
      link.addEventListener('click', (e) => {
        e.preventDefault();
        openExternalUrl(field.text.trim());
      });
      row.appendChild(link);
    } else {
      const value = document.createElement('span');
      value.className = 'break-words';
      value.textContent = field.text;
      row.appendChild(value);
    }
    root.appendChild(row);
  }
  if (point.fields.length === 0) {
    root.textContent = `${point.lat.toFixed(5)}, ${point.lon.toFixed(5)}`;
  }
  return root;
}

/**
 * Row-list popup for a same-location stack. Each entry is labelled with
 * the row's first popup field (typically the name column) and clicking it
 * selects that row.
 */
function buildStackListContent(
  stack: readonly MapPoint[],
  title: string,
  onPick: (point: MapPoint) => void,
): HTMLElement {
  const root = document.createElement('div');
  root.className = 'flex max-h-[220px] max-w-[260px] flex-col gap-0.5 overflow-y-auto font-sans text-xs text-neutral-900';
  const heading = document.createElement('div');
  heading.className = 'mb-1 font-semibold';
  heading.textContent = title;
  root.appendChild(heading);
  for (const point of stack) {
    const item = document.createElement('button');
    item.type = 'button';
    item.className = 'cursor-pointer rounded-sm px-1 py-0.5 text-left hover:bg-neutral-200';
    item.textContent = point.fields[0]?.text ?? `#${point.rowIndex + 1}`;
    item.addEventListener('click', () => onPick(point));
    root.appendChild(item);
  }
  return root;
}

export default function MapView({
  cell,
  latCandidates,
  lonCandidates,
  selectedRow,
  selectionSource,
  onSelectRow,
}: {
  cell: CellResult;
  latCandidates: readonly number[];
  lonCandidates: readonly number[];
  /** Row index of the linked selection; the matching pin is emphasised. */
  selectedRow?: number | null;
  /** Which side produced the link. The map only glides / opens the popup
      for grid-sourced changes — a pin click already opened its popup and
      needs no camera move. */
  selectionSource?: 'map' | 'grid';
  /** Fired when the user clicks a pin, carrying its row index. */
  onSelectRow?: (rowIndex: number) => void;
}) {
  const { t } = useTranslation('query');
  const settings = useSnapshot(settingsState);
  const isDark =
    settings.theme === 'dark'
    || (settings.theme === 'system' && document.documentElement.classList.contains('dark'));

  const containerRef = useRef<HTMLDivElement>(null);
  const mapRef = useRef<maplibregl.Map | null>(null);
  const popupRef = useRef<maplibregl.Popup | null>(null);
  const pointByRowRef = useRef(new Map<number, MapPoint>());
  // Latest link state, readable from map callbacks and the load handler
  // without re-running the map-building effect.
  const selectionRef = useRef<{ row: number | null | undefined; source: 'map' | 'grid' | undefined }>({
    row: selectedRow,
    source: selectionSource,
  });
  selectionRef.current = { row: selectedRow, source: selectionSource };
  const onSelectRowRef = useRef(onSelectRow);
  onSelectRowRef.current = onSelectRow;

  const latIndex = pickCoordinateColumn(cell, latCandidates);
  const lonIndex = pickCoordinateColumn(cell, lonCandidates);

  const colorCandidates = useMemo(
    () => findColorCandidates(cell),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [cell.schema, cell.rowCount >= COLUMN_SAMPLE_ROWS ? COLUMN_SAMPLE_ROWS : cell.rowCount],
  );
  const [colorColumn, setColorColumn] = useState<number | null>(null);
  const activeColorColumn =
    colorColumn !== null && colorCandidates.some((c) => c.index === colorColumn) ? colorColumn : null;

  const { points, skipped, truncated } = extractPoints(cell, latIndex, lonIndex, activeColorColumn);
  const colorMap = activeColorColumn !== null ? buildColorMap(points) : null;

  useEffect(() => {
    const container = containerRef.current;
    if (container === null) return;

    const map = new maplibregl.Map({
      container,
      style: {
        version: 8,
        glyphs: GLYPHS_URL,
        sources: {
          basemap: {
            type: 'raster',
            tiles: isDark ? DARK_TILES : LIGHT_TILES,
            tileSize: 256,
            attribution: TILE_ATTRIBUTION,
          },
        },
        layers: [{ id: 'basemap', type: 'raster', source: 'basemap' }],
      },
      // Neutral world view for the empty-result case; real results fit below.
      center: [-98, 39],
      zoom: 3,
      attributionControl: false,
    });
    // Corners: nav top-left (top-right belongs to the results pane's layout
    // buttons), attribution bottom-left, color-by picker bottom-right.
    map.addControl(new maplibregl.NavigationControl({ showCompass: false }), 'top-left');
    map.addControl(new maplibregl.AttributionControl({ compact: true }), 'bottom-left');
    // Surface tile / style failures in the devtools console — the default
    // silent failure mode reads as "blank map, no logs, nothing to go on".
    map.on('error', (event) => {
      console.warn('[map] maplibre error:', event.error ?? event);
    });

    mapRef.current = map;
    const pointByRow = new Map(points.map((point) => [point.rowIndex, point]));
    pointByRowRef.current = pointByRow;

    const openDetailPopup = (point: MapPoint) => {
      popupRef.current?.remove();
      popupRef.current = new maplibregl.Popup({ closeButton: false, maxWidth: '280px' })
        .setLngLat([point.lon, point.lat])
        .setDOMContent(buildPopupContent(point))
        .addTo(map);
    };

    map.on('load', () => {
      map.addSource('points', {
        type: 'geojson',
        data: toFeatureCollection(points, colorMap),
        cluster: true,
        clusterRadius: 42,
        clusterMaxZoom: CLUSTER_MAX_ZOOM,
      });
      // Selected point lives in its own (non-clustered) source so the
      // emphasis ring stays visible even while its point is absorbed into
      // a cluster bubble.
      map.addSource('selected', {
        type: 'geojson',
        data: { type: 'FeatureCollection', features: [] },
      });

      map.addLayer({
        id: 'clusters',
        type: 'circle',
        source: 'points',
        filter: ['has', 'point_count'],
        paint: {
          'circle-color': isDark ? '#3b82f6' : '#2563eb',
          'circle-opacity': 0.85,
          'circle-radius': ['step', ['get', 'point_count'], 12, 10, 16, 50, 22, 200, 28],
          'circle-stroke-width': 1.5,
          'circle-stroke-color': isDark ? '#0f172a' : '#ffffff',
        },
      });
      map.addLayer({
        id: 'cluster-count',
        type: 'symbol',
        source: 'points',
        filter: ['has', 'point_count'],
        layout: {
          'text-field': ['get', 'point_count_abbreviated'],
          'text-font': ['Open Sans Bold'],
          'text-size': 11,
        },
        paint: { 'text-color': '#ffffff' },
      });
      map.addLayer({
        id: 'point',
        type: 'circle',
        source: 'points',
        filter: ['!', ['has', 'point_count']],
        paint: {
          'circle-color': ['get', 'color'],
          'circle-radius': 6,
          'circle-stroke-width': 1.5,
          'circle-stroke-color': isDark ? '#0f172a' : '#ffffff',
        },
      });
      map.addLayer({
        id: 'selected-ring',
        type: 'circle',
        source: 'selected',
        paint: {
          'circle-color': 'rgba(0, 0, 0, 0)',
          'circle-radius': 11,
          'circle-stroke-width': 3,
          'circle-stroke-color': '#f59e0b',
        },
      });

      map.on('click', 'point', (event) => {
        const feature = event.features?.[0];
        const rowIndex = feature?.properties?.rowIndex;
        if (typeof rowIndex !== 'number') return;
        const point = pointByRow.get(rowIndex);
        if (point === undefined) return;
        openDetailPopup(point);
        onSelectRowRef.current?.(rowIndex);
      });

      map.on('click', 'clusters', (event) => {
        const feature = event.features?.[0] as Feature<Point> | undefined;
        const clusterId = feature?.properties?.cluster_id;
        if (typeof clusterId !== 'number' || feature === undefined) return;
        const source = map.getSource('points') as maplibregl.GeoJSONSource;
        source.getClusterExpansionZoom(clusterId).then((expansionZoom) => {
          if (expansionZoom <= CLUSTER_MAX_ZOOM) {
            map.easeTo({
              center: feature.geometry.coordinates as [number, number],
              zoom: expansionZoom + 0.25,
            });
            return;
          }
          // Same-location stack — zooming can never separate it; list the
          // rows instead and let a click pick one.
          source.getClusterLeaves(clusterId, 50, 0).then((leaves) => {
            const stack: MapPoint[] = [];
            for (const leaf of leaves) {
              const rowIndex = (leaf as Feature<Point>).properties?.rowIndex;
              if (typeof rowIndex === 'number') {
                const point = pointByRow.get(rowIndex);
                if (point !== undefined) stack.push(point);
              }
            }
            if (stack.length === 0) return;
            popupRef.current?.remove();
            popupRef.current = new maplibregl.Popup({ closeButton: false, maxWidth: '280px' })
              .setLngLat(feature.geometry.coordinates as [number, number])
              .setDOMContent(buildStackListContent(
                stack,
                t('mapView.stackTitle', { count: stack.length }),
                (point) => {
                  openDetailPopup(point);
                  onSelectRowRef.current?.(point.rowIndex);
                },
              ))
              .addTo(map);
          }).catch((error: unknown) => console.warn('[map] cluster leaves:', error));
        }).catch((error: unknown) => console.warn('[map] cluster expansion:', error));
      });

      for (const layer of ['point', 'clusters']) {
        map.on('mouseenter', layer, () => { map.getCanvas().style.cursor = 'pointer'; });
        map.on('mouseleave', layer, () => { map.getCanvas().style.cursor = ''; });
      }

      applySelection(map, pointByRow, openDetailPopup, selectionRef.current);
    });

    if (points.length === 1) {
      map.setCenter([points[0].lon, points[0].lat]);
      map.setZoom(12);
    } else if (points.length > 1) {
      map.fitBounds(robustBounds(points), { padding: 48, maxZoom: 14, animate: false });
    }

    // The pane resizes with splits / window changes; maplibre only measures
    // its container on load, so track it explicitly.
    const resizeObserver = new ResizeObserver(() => map.resize());
    resizeObserver.observe(container);

    return () => {
      resizeObserver.disconnect();
      popupRef.current?.remove();
      popupRef.current = null;
      pointByRowRef.current = new Map();
      mapRef.current = null;
      map.remove();
    };
    // Rebuild the map when the result identity, columns, palette, or theme
    // change; `points` derives from cell.rows which is ref()'d, so cellId +
    // rowCount are the reactive signals.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [cell.cellId, cell.rowCount, latIndex, lonIndex, activeColorColumn, isDark]);

  // Emphasise the linked pin; for grid-sourced links additionally glide to
  // it and open its popup (map-sourced links are pin clicks — the popup is
  // already open and the camera shouldn't move under the user). The load
  // handler re-applies this after streaming rebuilds.
  useEffect(() => {
    const map = mapRef.current;
    if (map === null || !map.isStyleLoaded()) return;
    applySelection(
      map,
      pointByRowRef.current,
      (point) => {
        popupRef.current?.remove();
        popupRef.current = new maplibregl.Popup({ closeButton: false, maxWidth: '280px' })
          .setLngLat([point.lon, point.lat])
          .setDOMContent(buildPopupContent(point))
          .addTo(map);
      },
      { row: selectedRow, source: selectionSource },
    );
  }, [selectedRow, selectionSource, cell.cellId, cell.rowCount]);

  const latName = cell.schema?.[latIndex]?.name ?? 'lat';
  const lonName = cell.schema?.[lonIndex]?.name ?? 'lon';
  const legend = colorMap !== null
    ? [...colorMap.entries()].filter(([, color]) => color !== OVERFLOW_COLOR)
    : null;

  return (
    // min-h floor so a collapsed flex chain can never shrink the canvas to
    // an invisible sliver — a broken layout should still show a short map.
    <div className="relative min-h-[160px] flex-1">
      {/* Positioning is inline, not a class: maplibre's stylesheet sets
          `position: relative` on its container (.maplibregl-map), and the
          lazy chunk's CSS is appended after the app bundle, so at equal
          specificity it overrides a Tailwind `absolute` and collapses the
          container to zero height. Inline style outranks both. */}
      <div ref={containerRef} style={{ position: 'absolute', inset: 0 }} />
      {points.length === 0 ? (
        <div className="pointer-events-none absolute inset-0 z-10 flex items-center justify-center">
          <div className="bg-background/90 text-muted-foreground max-w-[85%] rounded-md px-3 py-2 text-center text-xs shadow-sm">
            {t('mapView.noPoints', { count: skipped, lat: latName, lon: lonName })}
          </div>
        </div>
      ) : (
        <>
          {(skipped > 0 || truncated > 0) && (
            <div className="bg-background/85 text-muted-foreground absolute top-2 left-11 z-10 rounded-sm px-2 py-1 text-xs">
              {truncated > 0
                ? t('mapView.truncated', { shown: points.length, total: points.length + truncated })
                : t('mapView.plotted', { shown: points.length, total: points.length + skipped })}
            </div>
          )}
          {colorCandidates.length > 0 && (
            <div className="absolute right-2 bottom-2 z-10 flex flex-col items-end gap-1">
              {legend !== null && legend.length > 0 && (
                <div className="bg-background/90 flex max-w-[220px] flex-col gap-0.5 rounded-sm px-2 py-1 shadow-sm">
                  {legend.map(([value, color]) => (
                    <div key={value} className="flex items-center gap-1.5 text-xs">
                      <span
                        className="inline-block size-2.5 shrink-0 rounded-full"
                        style={{ backgroundColor: color }}
                      />
                      <span className="text-foreground truncate">{value}</span>
                    </div>
                  ))}
                </div>
              )}
              <select
                value={activeColorColumn ?? ''}
                onChange={(e) => setColorColumn(e.target.value === '' ? null : Number(e.target.value))}
                aria-label={t('mapView.colorBy')}
                title={t('mapView.colorBy')}
                className="bg-background/90 text-foreground border-border cursor-pointer rounded-sm border px-1.5 py-0.5 text-xs shadow-sm"
              >
                <option value="">{t('mapView.colorByNone')}</option>
                {colorCandidates.map((candidate) => (
                  <option key={candidate.index} value={candidate.index}>
                    {candidate.name}
                  </option>
                ))}
              </select>
            </div>
          )}
        </>
      )}
    </div>
  );
}

/**
 * Writes the linked row into the `selected` source (the emphasis ring) and,
 * for grid-sourced links, glides the camera to it and opens its popup.
 */
function applySelection(
  map: maplibregl.Map,
  pointByRow: ReadonlyMap<number, MapPoint>,
  openDetailPopup: (point: MapPoint) => void,
  selection: { row: number | null | undefined; source: 'map' | 'grid' | undefined },
): void {
  const source = map.getSource('selected') as maplibregl.GeoJSONSource | undefined;
  if (source === undefined) return;

  const point = selection.row !== null && selection.row !== undefined
    ? pointByRow.get(selection.row)
    : undefined;
  if (point === undefined) {
    source.setData({ type: 'FeatureCollection', features: [] });
    return;
  }

  source.setData({
    type: 'FeatureCollection',
    features: [{
      type: 'Feature',
      geometry: { type: 'Point', coordinates: [point.lon, point.lat] },
      properties: {},
    }],
  });
  if (selection.source === 'grid') {
    map.easeTo({
      center: [point.lon, point.lat],
      zoom: Math.max(map.getZoom(), 13),
      duration: 450,
    });
    openDetailPopup(point);
  }
}
