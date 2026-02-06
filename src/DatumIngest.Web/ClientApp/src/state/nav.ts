import { proxy } from 'valtio';

// Active view in the app shell. Chat is the default; Models is the first
// non-chat surface. Editor / Catalog / Settings etc. will land here as
// additional view ids when their UI rounds happen. No router yet — the
// shell is a flat view switcher, fine for the handful of views v1 needs.
//
// When a real router lands (likely when the conversation surface grows
// shareable URLs or "open this table"-style deep links), this state
// becomes a derived value of `location` and the actions go away. Until
// then, this is the source of truth.

export type ActiveView = 'chat' | 'models';

interface NavState {
  view: ActiveView;
}

export const navState = proxy<NavState>({
  view: 'chat',
});

export function setView(view: ActiveView): void {
  navState.view = view;
}
