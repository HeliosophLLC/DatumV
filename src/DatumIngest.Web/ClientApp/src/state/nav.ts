import { proxy } from 'valtio';

// Active view in the workspace (left/secondary) pane. Chat is no longer
// a switchable view — it lives permanently on the right side of the
// main window. No router yet; the shell is a flat view switcher, fine
// for the handful of views v1 needs.
//
// When a real router lands (likely when the conversation surface grows
// shareable URLs or "open this table"-style deep links), this state
// becomes a derived value of `location` and the actions go away. Until
// then, this is the source of truth.

export type ActiveView = 'query' | 'settings';

interface NavState {
  view: ActiveView;
}

export const navState = proxy<NavState>({
  view: 'query',
});

export function setView(view: ActiveView): void {
  navState.view = view;
}
