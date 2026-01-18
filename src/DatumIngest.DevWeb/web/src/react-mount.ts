// Small helper that owns the per-group React roots for the results
// pane. main.ts calls mountResultsPane() when a group is created and
// unmountResultsPane() when it's dissolved; this module tracks the
// (groupId → Root) map so unmount can find and tear down the right
// tree without main.ts holding the reference.

import { createRoot, type Root } from 'react-dom/client';
import { createElement } from 'react';
import { ResultsPane } from './results.js';

const rootsByGroupId = new Map<string, Root>();

export function mountResultsPane(paneEl: HTMLElement, groupId: string): void {
  // Idempotent: if a root already exists for this group, leave it alone.
  // Callers re-mount by calling unmountResultsPane() first.
  if (rootsByGroupId.has(groupId)) return;
  // Clear any pre-existing imperative content (legacy run-loop wrote
  // placeholders here).
  paneEl.innerHTML = '';
  const root = createRoot(paneEl);
  root.render(createElement(ResultsPane, { groupId }));
  rootsByGroupId.set(groupId, root);
}

export function unmountResultsPane(groupId: string): void {
  const root = rootsByGroupId.get(groupId);
  if (!root) return;
  root.unmount();
  rootsByGroupId.delete(groupId);
}
