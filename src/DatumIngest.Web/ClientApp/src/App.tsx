import { useEffect, useRef, useState } from 'react';
import { useSnapshot } from 'valtio';
import { usePanelRef } from 'react-resizable-panels';
import { refreshHealth } from './state/health';
import { refreshSettings, settingsState } from './state/settings';
import { conversationState } from './state/conversation';
import { navState, type ActiveView } from './state/nav';
import { isTornOutWindow } from './state/tabs';
import { WindowChrome } from '@/components/window/WindowChrome';
import { HomePage } from '@/components/home/HomePage';
import { ConversationView } from '@/components/chat/ConversationView';
import { ModelsView } from '@/components/models/ModelsView';
import { QueryEditorView } from '@/components/query/QueryEditorView';
import { SettingsView } from '@/components/settings/SettingsView';
import {
  ResizableHandle,
  ResizablePanel,
  ResizablePanelGroup,
} from '@/components/ui/resizable';
import { cn } from '@/lib/utils';

type SecondaryView = Exclude<ActiveView, 'chat'>;

// Default split when chat is docked next to a secondary view.
const DEFAULT_SECONDARY_SIZE = '65%';
const DEFAULT_CHAT_SIZE = '35%';
const PANEL_MIN_SIZE = '20%';
const COLLAPSE_ANIMATION_MS = 300;

export default function App() {
  const { messages, status } = useSnapshot(conversationState);
  const { view } = useSnapshot(navState);
  const { animations } = useSnapshot(settingsState);

  useEffect(() => {
    refreshHealth();
    refreshSettings();
  }, []);

  // Torn-out tab windows skip the main shell entirely: no SideNav,
  // no chat panel, no view switcher — just the query editor for the
  // tab that was torn out. The split / DnD / Monaco machinery inside
  // QueryEditorView is identical to the main window's, so dragging
  // tabs back into the main works seamlessly.
  //
  // The `flex-1 flex-col` wrapper is load-bearing: WindowChrome's
  // content slot is a flex row (SideNav | main panel split in the
  // regular shell), so a direct flex-row child without explicit grow
  // settles at content width — the tab strip shrinks to fit the tabs
  // and the editor sits empty to the right. Wrapping in flex-col +
  // flex-1 lets the column claim the row's full width, and matches
  // the layout the ResizablePanel provides in the main-window path.
  if (isTornOutWindow) {
    return (
      <WindowChrome>
        <div className="flex flex-1 flex-col overflow-hidden">
          <QueryEditorView />
        </div>
      </WindowChrome>
    );
  }

  const showConversation = messages.length > 0 || status !== 'idle';

  // Keep the most recently active non-chat view mounted across the slide
  // back to chat — without this, the secondary content unmounts at click
  // time and only chat animates, which looks like a flash rather than a
  // slide.
  const [secondaryView, setSecondaryView] = useState<SecondaryView | null>(
    view === 'chat' ? null : view,
  );
  useEffect(() => {
    if (view !== 'chat') setSecondaryView(view);
  }, [view]);

  // Imperative collapse/expand on nav change. The library sets panel sizes
  // via inline `flex-grow`; we apply a CSS transition on that property
  // only while a programmatic resize is in flight, then strip it. The
  // class isn't present during user drag, so drag stays instant.
  const secondaryRef = usePanelRef();
  const [animating, setAnimating] = useState(false);
  const hasInitialized = useRef(false);

  useEffect(() => {
    const panel = secondaryRef.current;
    if (!panel) return;

    const wantCollapsed = view === 'chat';
    if (panel.isCollapsed() === wantCollapsed) {
      hasInitialized.current = true;
      return;
    }

    // First run snaps without animation so chat starts at full width on
    // app boot rather than flashing the default 65/35 split.
    const isFirstRun = !hasInitialized.current;
    hasInitialized.current = true;

    if (animations && !isFirstRun) {
      setAnimating(true);
      const t = window.setTimeout(
        () => setAnimating(false),
        COLLAPSE_ANIMATION_MS + 20,
      );
      if (wantCollapsed) panel.collapse();
      else panel.expand();
      return () => window.clearTimeout(t);
    }
    if (wantCollapsed) panel.collapse();
    else panel.expand();
  }, [view, animations, secondaryRef]);

  const transitionClass = animating
    ? 'transition-[flex-grow] duration-300 ease-in-out'
    : '';

  return (
    <WindowChrome>
      <ResizablePanelGroup orientation="horizontal" className="flex-1">
        <ResizablePanel
          panelRef={secondaryRef}
          id="secondary"
          collapsible
          collapsedSize="0%"
          defaultSize={DEFAULT_SECONDARY_SIZE}
          minSize={PANEL_MIN_SIZE}
          className={cn('flex flex-col overflow-hidden', transitionClass)}
        >
          {secondaryView === 'query' && <QueryEditorView />}
          {secondaryView === 'models' && <ModelsView />}
          {secondaryView === 'settings' && <SettingsView />}
        </ResizablePanel>
        <ResizableHandle
          className={cn(
            'transition-opacity duration-300',
            view === 'chat' && 'pointer-events-none opacity-0',
          )}
        />
        <ResizablePanel
          id="chat"
          defaultSize={DEFAULT_CHAT_SIZE}
          minSize={PANEL_MIN_SIZE}
          className={cn('flex flex-col overflow-hidden', transitionClass)}
        >
          {showConversation ? <ConversationView /> : <HomePage />}
        </ResizablePanel>
      </ResizablePanelGroup>
    </WindowChrome>
  );
}
