import { proxy } from 'valtio';

// Active view in the app shell. Query (SQL editor) is the default — it's
// the surface a user is most likely opening the app to work in, and
// landing on chat collapses the editor area to 0% via
// `secondary.collapse()` in App.tsx, requiring a manual resize on every
// boot. No router yet — the shell is a flat view switcher, fine for
// the handful of views v1 needs.
//
// When a real router lands (likely when the conversation surface grows
// shareable URLs or "open this table"-style deep links), this state
// becomes a derived value of `location` and the actions go away. Until
// then, this is the source of truth.

export type ActiveView = 'chat' | 'query' | 'models' | 'settings';

interface NavState {
  view: ActiveView;
}

export const navState = proxy<NavState>({
  view: 'query',
});

export function setView(view: ActiveView): void {
  navState.view = view;
}
