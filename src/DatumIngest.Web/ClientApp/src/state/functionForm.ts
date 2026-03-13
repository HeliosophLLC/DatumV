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
  /**
   * Per-parameter manual kind override, keyed by parameter name. When
   * set, takes precedence over `declaredKindFor`'s value-driven
   * inference so the user can force a specific kind from the slot's
   * accepted set (e.g. typing `100` into `abs(x)` infers `Int8`, but
   * an Int64 override sticks even as the value changes). Cleared on
   * function/variant change with the rest of the field state.
   *
   * For variadic slots, per-occurrence keys (`${name}_${i}`) hold each
   * slot's override. `RequireSameKindAcrossArgs` variadics broadcast a
   * single click to every slot at action time, so all keys end up with
   * the same value — no separate "shared" entry to keep in sync.
   */
  kindOverrides: Record<string, string>;
  /**
   * Per-variadic occurrence count, keyed by the variadic's name. Drives
   * the number of input rows the form renders for the trailing variadic
   * slot. Missing entry → `max(1, minOccurrences)` so there's always at
   * least one row visible to type into.
   *
   * Per-slot values live in `textValues` / `fileNames` / `fieldErrors`
   * under synthetic keys `${variadicName}_${index}` (0-based). Removing
   * a slot at index `i` shifts every entry with index `> i` down by one
   * so keys stay contiguous.
   */
  variadicCounts: Record<string, number>;
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
      kindOverrides: {},
      variadicCounts: {},
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
    state.kindOverrides = {};
    state.variadicCounts = {};
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

/**
 * Pins `paramName` to `kind` regardless of what value-driven inference
 * would otherwise pick. No-op when `kind` already matches the existing
 * override (so calling this on the currently-selected pill is harmless).
 * The override survives text edits — clears only on function/variant
 * change (handled in `setFunctionFormSelection`).
 */
export function setFunctionFormKindOverride(
  tabId: string,
  paramName: string,
  kind: string,
): void {
  const state = ensureFunctionForm(tabId);
  if (state.kindOverrides[paramName] === kind) return;
  state.kindOverrides = { ...state.kindOverrides, [paramName]: kind };
  clearFieldError(state, paramName);
}

/**
 * Broadcasts <paramref name="kind"/> across every slot of a
 * same-kind-required variadic. Mirrors `setFunctionFormKindOverride` but
 * fans out to `${variadicName}_0`, `${variadicName}_1`, … so the kind
 * stays consistent across all occurrences even if the slot count
 * changes later.
 */
export function setFunctionFormVariadicKindOverride(
  tabId: string,
  variadicName: string,
  count: number,
  kind: string,
): void {
  const state = ensureFunctionForm(tabId);
  const next = { ...state.kindOverrides };
  let changed = false;
  for (let i = 0; i < count; i++) {
    const key = `${variadicName}_${i}`;
    if (next[key] !== kind) {
      next[key] = kind;
      changed = true;
    }
  }
  if (!changed) return;
  state.kindOverrides = next;
}

/**
 * Returns the number of slots currently rendered for <paramref name="variadicName"/>.
 * Defaults to `max(1, minOccurrences)` so a fresh variadic always has at
 * least one input row to type into — the user can remove it if the
 * function genuinely allows zero arguments.
 */
export function variadicSlotCount(
  form: FunctionFormState,
  variadicName: string,
  minOccurrences: number,
): number {
  const explicit = form.variadicCounts[variadicName];
  if (typeof explicit === 'number') return explicit;
  return Math.max(1, minOccurrences);
}

/** Appends one new occurrence to the variadic slot list. */
export function addVariadicSlot(
  tabId: string,
  variadicName: string,
  minOccurrences: number,
): void {
  const state = ensureFunctionForm(tabId);
  const current = variadicSlotCount(state, variadicName, minOccurrences);
  // Copy the override (if any) from slot 0 forward so a same-kind
  // variadic's new slot inherits the existing pin. Slot-0 lookup also
  // works for non-same-kind because the broadcast helper above already
  // wrote slot 0 when the user clicked the chip.
  const newKey = `${variadicName}_${current}`;
  const slot0 = state.kindOverrides[`${variadicName}_0`];
  if (slot0 !== undefined && state.kindOverrides[newKey] === undefined) {
    state.kindOverrides = { ...state.kindOverrides, [newKey]: slot0 };
  }
  state.variadicCounts = {
    ...state.variadicCounts,
    [variadicName]: current + 1,
  };
}

/**
 * Removes the occurrence at <paramref name="index"/>, shifting every
 * higher-indexed slot's values + filenames + errors + override down by
 * one so keys stay contiguous. No-op when the slot doesn't exist or
 * removing it would drop count below 1 (we always keep at least one
 * row visible).
 */
export function removeVariadicSlot(
  tabId: string,
  variadicName: string,
  minOccurrences: number,
  index: number,
): void {
  const state = ensureFunctionForm(tabId);
  const current = variadicSlotCount(state, variadicName, minOccurrences);
  if (index < 0 || index >= current) return;
  if (current <= 1) return;

  const shiftedTextValues = { ...state.textValues };
  const shiftedFileNames = { ...state.fileNames };
  const shiftedFieldErrors = { ...state.fieldErrors };
  const shiftedKindOverrides = { ...state.kindOverrides };
  for (let i = index; i < current - 1; i++) {
    const from = `${variadicName}_${i + 1}`;
    const to = `${variadicName}_${i}`;
    copyOrDelete(shiftedTextValues, from, to);
    copyOrDelete(shiftedFileNames, from, to);
    copyOrDelete(shiftedFieldErrors, from, to);
    copyOrDelete(shiftedKindOverrides, from, to);
  }
  // Drop the now-orphan trailing key for each map.
  const tailKey = `${variadicName}_${current - 1}`;
  delete shiftedTextValues[tailKey];
  delete shiftedFileNames[tailKey];
  delete shiftedFieldErrors[tailKey];
  delete shiftedKindOverrides[tailKey];

  // Shift File handles too — file map mirrors fileNames but isn't
  // proxied.
  const tabFiles = filesByTabId.get(tabId);
  if (tabFiles) {
    for (let i = index; i < current - 1; i++) {
      const from = `${variadicName}_${i + 1}`;
      const to = `${variadicName}_${i}`;
      const f = tabFiles.get(from);
      if (f !== undefined) tabFiles.set(to, f);
      else tabFiles.delete(to);
    }
    tabFiles.delete(tailKey);
  }

  state.textValues = shiftedTextValues;
  state.fileNames = shiftedFileNames;
  state.fieldErrors = shiftedFieldErrors;
  state.kindOverrides = shiftedKindOverrides;
  state.variadicCounts = {
    ...state.variadicCounts,
    [variadicName]: current - 1,
  };
}

function copyOrDelete(
  map: Record<string, string>,
  from: string,
  to: string,
): void {
  const v = map[from];
  if (v === undefined) delete map[to];
  else map[to] = v;
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
