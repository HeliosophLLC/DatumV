import { proxy } from 'valtio';
import { runTab } from './execution';
import { functionCatalogState } from './functionCatalog';
import { buildFunctionRequest } from '@/lib/buildFunctionRequest';

// Per-tab state for an Execute-Function tab: which function the user
// picked, which overload variant, and the values they've entered for
// each parameter. Files are kept in a separate non-proxy map keyed by
// tabId — File objects are non-serialisable and shouldn't ride on the
// proxy (the proxy auto-persists into the tabs.ts localStorage path
// indirectly when consumers serialise it).
//
// On reload, text-shaped values rehydrate; file-shaped values don't.
// That's by v1 design (the user re-uploads each time per the agreed
// scope) — re-upload-on-reload is acceptable in a localhost dev tool.

export interface FunctionFormSelection {
  /** SQL schema of the picked function. */
  schema: string;
  /** Canonical name of the picked function. */
  name: string;
  /** Index into the function's `signatures` array. */
  variantIndex: number;
}

export interface FunctionFormState {
  /** Search text in the function picker. */
  search: string;
  /** Currently-picked function + variant; null until the user picks one. */
  selection: FunctionFormSelection | null;
  /**
   * Per-parameter text values, keyed by parameter name. Holds the raw
   * text the user typed (numbers + booleans live here as strings) so the
   * field can preserve in-progress input — "1.2e" while typing a Float64
   * — without being eagerly re-parsed. Form-submit re-parses at the
   * boundary.
   */
  textValues: Record<string, string>;
  /**
   * Per-parameter filename, mirroring the corresponding entry in
   * `filesByTabId.get(tabId)`. Kept on the proxy so the UI re-renders on
   * file change (the underlying File ref isn't proxied). Reset on tab
   * reload (the File is gone).
   */
  fileNames: Record<string, string>;
  /**
   * Per-parameter validation message from the last Run attempt. Set by
   * `runFunctionTab` when the form can't be assembled into a wire
   * request; cleared per field as the user edits it (so the message
   * disappears as soon as the user starts addressing it).
   */
  fieldErrors: Record<string, string>;
}

interface FunctionFormStore {
  byTabId: Record<string, FunctionFormState>;
}

export const functionFormState = proxy<FunctionFormStore>({
  byTabId: {},
});

/**
 * Non-proxy storage for File handles. Files can't flow through Valtio's
 * proxy (the structuredClone-style equality checks aren't meaningful on
 * binary blobs) and shouldn't persist anyway. Lookup by tabId then
 * parameter name; mirrors `FunctionFormState.fileNames` in shape.
 */
const filesByTabId = new Map<string, Map<string, File>>();

/**
 * Returns (creating if needed) the form state for `tabId`. The function-
 * tab body calls this on first render to make sure the proxy slot
 * exists before any setter runs.
 */
export function ensureFunctionForm(tabId: string): FunctionFormState {
  let state = functionFormState.byTabId[tabId];
  if (!state) {
    state = {
      search: '',
      selection: null,
      textValues: {},
      fileNames: {},
      fieldErrors: {},
    };
    functionFormState.byTabId[tabId] = state;
  }
  return state;
}

export function setFunctionFormSearch(tabId: string, search: string): void {
  const state = ensureFunctionForm(tabId);
  state.search = search;
}

export function setFunctionFormSelection(
  tabId: string,
  selection: FunctionFormSelection,
): void {
  const state = ensureFunctionForm(tabId);
  // Changing the function or variant invalidates the per-parameter form
  // values — parameter names rarely collide across functions, but even
  // when they do the value's interpretation depends on the new variant's
  // kind. Drop them rather than silently re-bind.
  if (
    !state.selection ||
    state.selection.schema !== selection.schema ||
    state.selection.name !== selection.name ||
    state.selection.variantIndex !== selection.variantIndex
  ) {
    state.textValues = {};
    state.fileNames = {};
    state.fieldErrors = {};
    filesByTabId.delete(tabId);
  }
  state.selection = selection;
}

export function setFunctionFormText(
  tabId: string,
  paramName: string,
  value: string,
): void {
  const state = ensureFunctionForm(tabId);
  state.textValues = { ...state.textValues, [paramName]: value };
  clearFieldError(state, paramName);
}

export function setFunctionFormFile(
  tabId: string,
  paramName: string,
  file: File | null,
): void {
  const state = ensureFunctionForm(tabId);
  let tabFiles = filesByTabId.get(tabId);
  if (!tabFiles) {
    tabFiles = new Map();
    filesByTabId.set(tabId, tabFiles);
  }
  if (file === null) {
    tabFiles.delete(paramName);
    // Drop the mirrored filename so the UI clears.
    const next = { ...state.fileNames };
    delete next[paramName];
    state.fileNames = next;
  } else {
    tabFiles.set(paramName, file);
    state.fileNames = { ...state.fileNames, [paramName]: file.name };
  }
  clearFieldError(state, paramName);
}

function clearFieldError(state: FunctionFormState, paramName: string): void {
  if (!(paramName in state.fieldErrors)) return;
  const next = { ...state.fieldErrors };
  delete next[paramName];
  state.fieldErrors = next;
}

/**
 * Returns the File for `(tabId, paramName)`, or null when none was
 * selected (or when the page was reloaded and the in-memory map was
 * cleared). PR 5 reads from here when building the multipart payload.
 */
export function getFunctionFormFile(
  tabId: string,
  paramName: string,
): File | null {
  return filesByTabId.get(tabId)?.get(paramName) ?? null;
}

/**
 * Drops every trace of the form for `tabId`. Called when the tab is
 * closed (parallel to `disposeTabExecution`); avoids unbounded growth in
 * the proxy + file map.
 */
export function disposeFunctionForm(tabId: string): void {
  delete functionFormState.byTabId[tabId];
  filesByTabId.delete(tabId);
}

/**
 * Builds a wire request from the current form and dispatches it to the
 * NDJSON runner. No-op when no function is picked. Validation failures
 * land in `state.fieldErrors` so the form can surface them per-field;
 * success clears the error map and streams results into
 * `executionsState.byTabId[tabId]`.
 *
 * Idempotent against double-clicks while a run is in flight — the
 * underlying `runTab` no-ops when the tab is already streaming.
 */
export async function runFunctionTab(tabId: string): Promise<void> {
  const state = functionFormState.byTabId[tabId];
  if (!state || !state.selection) return;
  const fn = functionCatalogState.entries.find(
    (f) =>
      f.schema === state.selection!.schema && f.name === state.selection!.name,
  );
  if (!fn) return;
  const variant = fn.signatures?.[state.selection.variantIndex];
  if (!variant) return;

  const built = buildFunctionRequest(tabId, fn, variant, state);
  if (!built.ok) {
    state.fieldErrors = built.fieldErrors;
    return;
  }
  state.fieldErrors = {};
  await runTab(tabId, built.sql, built.opts);
}
