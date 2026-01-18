# DevWeb TypeScript migration

## Goal

Lift the ~4000-line inline `<script>` block out of
[src/DatumIngest.DevWeb/wwwroot/index.html](../src/DatumIngest.DevWeb/wwwroot/index.html)
into TypeScript modules so 3rd-party libraries can be added with type safety
and a real build pipeline. All current JavaScript is valid TypeScript, so this
is an incremental migration — each phase ships independently and the app
keeps working between phases.

The motivation is *not* "TypeScript is nicer." It is: the user wants to add
3rd-party libraries (charting, virtualization, etc.) and does not want to
hand-author them as untyped IIFE inline scripts. Phase 0 alone unblocks that.

## Current shape

- `index.html` is **4946 lines**. Lines 1–818 are markup/CSS, lines 819–926
  are body markup, **lines 928–4944 are a single IIFE** of inline JS sharing
  one closure scope.
- Monaco is loaded from `cdn.jsdelivr.net` via the AMD loader at
  [index.html:817](../src/DatumIngest.DevWeb/wwwroot/index.html#L817). No
  `package.json`, no `node_modules`, no bundler in
  [src/DatumIngest.DevWeb/](../src/DatumIngest.DevWeb/).
- The IIFE has roughly 15 logical sections (see Phase table below). Functions
  read shared mutables — the most important being `state` (line 1482-ish) —
  via closure. There are no explicit `import`/`export` boundaries today.

## Strategy

Vite + TypeScript, output to `wwwroot/dist/app.js`, replace the inline
`<script>` with `<script type="module" src="dist/app.js">`.

Two non-negotiables for the migration to stay incremental:

1. **Phase 0 lifts the IIFE verbatim into `main.ts`.** No semantic changes,
   no extraction, no type tightening. The point is to prove the build works
   and serves the same app the user sees today.
2. **Each later phase peels one cohesive section into its own module** with
   real exports, real types, and updated call sites in `main.ts`. The shared
   `state` object stays a singleton import for as long as needed — do not
   try to introduce DI in the same PR as an extraction.

## Phasing

The lines below refer to the current
[index.html](../src/DatumIngest.DevWeb/wwwroot/index.html).

### Phase 0 — Build infrastructure (~½ day)

**This is the only phase that unblocks 3rd-party libraries.** Everything
after it is cleanup the user can do at any pace.

- Add `src/DatumIngest.DevWeb/web/` containing `package.json`, `tsconfig.json`,
  `vite.config.ts`, and a single `src/main.ts`.
- `tsconfig.json` starts loose: `"allowJs": true`, `"strict": false`,
  `"noImplicitAny": false`, `"target": "ES2022"`, `"moduleResolution": "Bundler"`.
  These get tightened phase-by-phase.
- Copy the contents of the IIFE (index.html lines 929–4943, the inner body —
  not the `(() => { … })()` wrapper) into `main.ts` verbatim. It is already
  valid TS.
- `vite build` outputs `wwwroot/dist/app.js` (+ any chunks). Configure Vite's
  `build.outDir` to point at `wwwroot/dist/` and `build.emptyOutDir: true`.
- Wire the build into `DatumIngest.DevWeb.csproj` as a `BeforeTargets="Build"`
  exec target so `dotnet build` produces the bundle. Skip in `dotnet watch`
  scenarios where Vite dev server is preferred (document the dev workflow).
- Replace the inline `<script>…</script>` block in `index.html` with
  `<script type="module" src="dist/app.js"></script>`.
- Verify: load the page, run a query, switch workspaces, split groups,
  reload and confirm tabs persist. No behaviour should change.

**Done criterion:** the user can `npm install some-library` inside `web/`,
`import` it from `main.ts`, and have it land in the bundle without any
further plumbing.

### Phase 1 — Pure utilities (~1 day, ~700 LOC)

Self-contained sections with no dependency on `state` or DOM globals.
Each becomes a `.ts` file under `web/src/`; `main.ts` imports from it.

- `idb.ts` — the `IDB` IIFE at index.html:965-1053. Already self-contained;
  give it a typed `ResultRecord` interface and `Promise<…>` return types.
- `theme.ts` — `loadTheme`/`applyTheme`/`toggleTheme` at index.html:1056-1072.
- `modal.ts` — `showModal`/`alertModal`/`confirmModal`/`promptModal`/
  `openImageLightbox` at index.html:3500-3590.
- `html-util.ts` — `htmlNode`, `escapeHtml`, `truncate` at
  index.html:3822-3937.
- `parser-util.ts` — `parseParameterList`, `splitTopLevel`,
  `buildExecuteTemplate`, `buildModifyTemplateFromUdfRow`,
  `buildModifyTemplateFromProcedureRow`, `buildBuiltinExecuteTemplate` at
  index.html:4133-4229.
- `json-render.ts` — `renderJsonNode`/`renderJsonObject`/`renderJsonArray`
  at index.html:2770-2902. Define a typed `JsonValue` discriminated union.

After Phase 1, `main.ts` is ~3300 lines with 5–6 small typed modules
imported at the top.

### Phase 2 — State + workspace + tabs (~2–3 days, ~900 LOC)

This is the hardest phase because the closure-shared `state` object is read
by nearly every function in the file. Goal: extract `state.ts` exporting a
typed singleton + the workspace/tab manipulation functions.

- Define `WorkspaceState`, `Tab`, `Group` interfaces matching the on-disk
  shape (see comments at index.html:929-944).
- Extract index.html:1074-1480 (workspaces) and 1539-1953 (tabs) into
  `workspace.ts` + `tabs.ts`.
- `state.ts` exports a mutable `state: AppState` singleton — keep the
  singleton; don't introduce DI here.
- All consumers in `main.ts` get updated to `import { state } from './state'`.
- Tighten `tsconfig.json`: `"strict": true` for files under `web/src/`
  except `main.ts` (still in JS-ish mode while it's the holdout).

### Phase 3 — Results rendering + run loop (~2 days, ~880 LOC)

- `results.ts` — `renderResultsForActiveTab`, `renderResultSet`,
  `renderResult`, `renderRunningTab`, `renderCell`, `addCopyButton`,
  `dataB64ToBlobUrl`, `revokeMediaObjectUrlsForGroup` at index.html:2415-2993.
- `run.ts` — `getEditorSelection`, the run/streaming loop, `cancelActiveTabRun`
  at index.html:2999-3300.
- Define typed shapes for the streaming wire format (consult
  [NdjsonStreamingSink.cs](../src/DatumIngest.DevWeb/NdjsonStreamingSink.cs)
  for the source of truth).

### Phase 4 — Catalog sidebar (~1–2 days, ~830 LOC)

- `sidebar.ts` — sections setup, refresh, tree rendering, popovers, context
  menus at index.html:3592-4427.
- This module talks to several backend endpoints; type the response shapes
  alongside the C# DTOs they mirror.

### Phase 5 — Monaco from npm + language services (~2 days, ~450 LOC)

- `npm install monaco-editor` and the Vite Monaco plugin.
- Remove the CDN `<script src="…/loader.js">` at index.html:817.
- `editor.ts` — `initMonaco`, `bootMonacoForGroup`, `bootFallbackForGroup`,
  `swapEditorForGroup` at index.html:1955-2148.
- `language-services.ts` — `registerLanguageProviders`,
  `completionKindFromName`, `severityFromName`, `lspRangeToMonaco` at
  index.html:2150-2413. Use Monaco's actual TS types instead of the
  duck-typed objects current code relies on.
- This is also where the SignalR / WASM language-server transport types get
  formalised.

### Phase 6 — Group splitter, split/merge, boot (~1 day, ~500 LOC)

- `groups.ts` — `splitFocusedGroup`, `mergeAllGroupsIntoFocused`,
  `createEditorGroupElement`, `wireGroupToolbar`, `reconcileGroupDom`,
  splitter setup at index.html:4430-4843.
- `boot.ts` — top-level `boot()` at index.html:4845-end. Becomes the new
  `main.ts` entry point; the old `main.ts` is deleted.
- Tighten `tsconfig`: enable `"noUncheckedIndexedAccess"`,
  `"exactOptionalPropertyTypes"` if tolerable.

## Total effort

- **Phase 0 alone: ~½ day.** Sufficient to add 3rd-party libraries safely.
- **Full migration: ~10–12 working days** spread across phases.

The user can stop at any phase boundary and the app still works.

## Risks / things to watch

- **The csproj build wiring** — getting `dotnet build` to invoke `vite build`
  reliably on Windows (and on CI, if applicable) is the most likely source of
  Phase 0 friction. Consider gating on a `RestorePackages`-style flag so
  developers without Node installed get a clear error rather than a broken
  build.
- **Monaco AMD vs ESM** — Monaco's npm package wants to be bundled by Vite
  with the Monaco plugin. Mixing the AMD CDN loader with bundled ESM during
  Phase 5 will produce two Monaco instances; cut over in one PR.
- **The `state` singleton in Phase 2** — resist refactoring to DI. The
  closure pattern works; making it an imported singleton is enough. DI is a
  separate project.
- **Cross-window sync** (`setupCrossWindowSync` at index.html:3470) listens
  to `localStorage` events. Ensure the Vite dev server's HMR doesn't fire
  spurious storage events during Phase 2.
- **Result payload shapes** — the streaming/result types are currently
  inferred from runtime shape. Phase 3 should derive them from the C# DTOs
  ([JsonCell.cs](../src/DatumIngest.DevWeb/JsonCell.cs),
  [WebCellFormatter.cs](../src/DatumIngest.DevWeb/WebCellFormatter.cs),
  [NdjsonStreamingSink.cs](../src/DatumIngest.DevWeb/NdjsonStreamingSink.cs))
  rather than hand-typing them and risking drift.

## Out of scope

- Switching from Monaco to a different editor.
- Replacing the IndexedDB cache with a different store.
- React/Vue/Svelte. The DOM-manipulation style is fine; this migration is
  about types and module boundaries, not a framework rewrite.
- Test infrastructure for the front-end. Worth considering after Phase 3 but
  not part of this plan.
