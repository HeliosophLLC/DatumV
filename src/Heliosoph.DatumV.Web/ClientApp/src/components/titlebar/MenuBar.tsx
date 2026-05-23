import { useState, useRef, useEffect, useMemo, type ReactNode } from 'react';
import { useTranslation } from 'react-i18next';
import { createPortal } from 'react-dom';
import { buildMenu, type MenuNode } from '@/commands/menuDefinition';
import { runCommand } from '@/commands/registry';
import { cn } from '@/lib/utils';

// In-titlebar menu bar for Windows/Linux. macOS uses the native
// screen-top menubar (built by state/menu.ts from the same tree) and
// never mounts this component.
//
// Keyboard model (Windows convention):
//   - Hold Alt → underlines appear on mnemonic letters.
//   - Tap Alt or press F10 → enter menu mode: underlines stay,
//     first top-level becomes the keyboard target. Next letter
//     opens the matching menu.
//   - Alt+letter → opens the matching top-level directly.
//   - In an open popup: ↑/↓ to move, ←/→ to switch top-levels,
//     Enter to activate, letter to activate matching mnemonic, Esc
//     to back out.
//
// Accelerators (CmdOrCtrl+N etc.) are registered globally by main
// via Menu.setApplicationMenu, so they fire regardless of whether
// the menu is open or focused.

type SubmenuNode = Extract<MenuNode, { kind: 'submenu' }>;
type ItemNode = Extract<MenuNode, { kind: 'item' }>;
type RawItemNode = Extract<MenuNode, { kind: 'rawItem' }>;
type ActivatableNode = ItemNode | RawItemNode;

function isActivatable(node: MenuNode): node is ActivatableNode {
  return node.kind === 'item' || node.kind === 'rawItem';
}

interface ParsedLabel {
  display: string;
  mnemonic: string | null;
  mnemonicIndex: number;
}

// Walks the label, treating `&` as the mnemonic marker on the next
// character and `&&` as a literal `&`. First `&letter` wins.
function parseMnemonic(raw: string): ParsedLabel {
  let display = '';
  let mnemonic: string | null = null;
  let mnemonicIndex = -1;
  let i = 0;
  while (i < raw.length) {
    const ch = raw[i];
    if (ch === '&') {
      if (raw[i + 1] === '&') {
        display += '&';
        i += 2;
        continue;
      }
      if (i + 1 < raw.length && mnemonic === null) {
        mnemonicIndex = display.length;
        mnemonic = raw[i + 1].toLowerCase();
        display += raw[i + 1];
        i += 2;
        continue;
      }
      i++;
      continue;
    }
    display += ch;
    i++;
  }
  return { display, mnemonic, mnemonicIndex };
}

function MnemonicLabel({
  parsed,
  underline,
}: {
  parsed: ParsedLabel;
  underline: boolean;
}): ReactNode {
  if (!underline || parsed.mnemonicIndex < 0) return parsed.display;
  return (
    <>
      {parsed.display.slice(0, parsed.mnemonicIndex)}
      <span className="underline">{parsed.display[parsed.mnemonicIndex]}</span>
      {parsed.display.slice(parsed.mnemonicIndex + 1)}
    </>
  );
}

// Strips Electron's cross-platform `CmdOrCtrl` token down to `Ctrl`
// for display. This component only renders on Windows/Linux, so the
// disambiguation is dead weight in the visible UI. The native menu
// template still carries the raw `CmdOrCtrl`.
function formatAccelerator(accel: string): string {
  return accel.replace(/\b(CmdOrCtrl|CommandOrControl)\b/g, 'Ctrl');
}

export function MenuBar({ className }: { className?: string }) {
  const { t } = useTranslation();
  const tree = useMemo(() => buildMenu({ isMac: false }), []);

  // Top-level menus actually rendered here. We drop:
  //   - macRole'd submenus (App / Edit / Window) — macOS-only, kept
  //     in the shared tree only for the native menu.
  //   - submenus whose only children are role items, because role
  //     items render as null in the popup (their accelerators fire
  //     globally via the native menu instead). Edit and View are
  //     all-role on Win/Linux; showing an empty popup is worse than
  //     omitting them. The native menu still carries them so the
  //     accelerators stay registered.
  const topLevels = useMemo<SubmenuNode[]>(
    () =>
      tree.filter(
        (n): n is SubmenuNode =>
          n.kind === 'submenu' &&
          !n.macRole &&
          n.children.some((c) => isActivatable(c) || c.kind === 'submenu'),
      ),
    [tree],
  );

  // Parsed labels keyed by index — memoized because parseMnemonic
  // runs on every keystroke for mnemonic matching.
  const parsedTopLevels = useMemo(
    () => topLevels.map((n) => parseMnemonic(t(n.labelKey))),
    [topLevels, t],
  );

  const [openIdx, setOpenIdx] = useState<number | null>(null);
  // Whether mnemonic underlines should be visible. True while Alt is
  // held or while keyboard menu mode is active.
  const [showUnderlines, setShowUnderlines] = useState(false);
  // Keyboard menu mode: Alt was tapped (or F10 pressed). Arrow keys
  // navigate; first letter opens matching top-level.
  const [keyboardMode, setKeyboardMode] = useState(false);
  // Which top-level is keyboard-focused in menu mode (with or without
  // a popup open). null when not in keyboard mode.
  const [focusedTopIdx, setFocusedTopIdx] = useState<number | null>(null);
  // Which item is keyboard-focused inside the open popup. null = no
  // item focus (mouse mode), otherwise an index into activatable
  // children of topLevels[openIdx].
  const [focusedItemIdx, setFocusedItemIdx] = useState<number | null>(null);

  const barRef = useRef<HTMLDivElement>(null);
  // Tracks whether the current Alt-press got chorded with another
  // key — if so, releasing Alt should NOT enter menu mode.
  const altPressedRef = useRef(false);
  const altChordedRef = useRef(false);

  const reset = (): void => {
    setOpenIdx(null);
    setShowUnderlines(false);
    setKeyboardMode(false);
    setFocusedTopIdx(null);
    setFocusedItemIdx(null);
  };

  // Flat list of activatable items in the popup, in render order. The
  // popup body renders a single nested-submenu level (for Open Recent)
  // inline, so the flat order goes: direct activatable children of the
  // top-level, with any nested-submenu children spliced in at their
  // position. Keep this in sync with the Popup's itemCursor walk.
  const activatableChildren = (idx: number): ActivatableNode[] => {
    const out: ActivatableNode[] = [];
    for (const c of topLevels[idx].children) {
      if (isActivatable(c)) out.push(c);
      else if (c.kind === 'submenu') {
        for (const cc of c.children) {
          if (isActivatable(cc)) out.push(cc);
        }
      }
    }
    return out;
  };

  // Activate the item-of-focus or the supplied item. Always closes.
  const activate = (item: ActivatableNode): void => {
    if (item.enabled === false) {
      reset();
      return;
    }
    runCommand(item.commandId);
    reset();
  };

  useEffect(() => {
    const onKeyDown = (e: KeyboardEvent): void => {
      // Alt-press tracking. Don't set keyboardMode here — that's
      // decided on keyup based on whether a chord happened.
      if (e.key === 'Alt') {
        if (!altPressedRef.current) {
          altPressedRef.current = true;
          altChordedRef.current = false;
          setShowUnderlines(true);
        }
        return;
      }

      // F10 enters menu mode (Windows convention).
      if (
        e.key === 'F10' &&
        !e.altKey &&
        !e.ctrlKey &&
        !e.shiftKey &&
        !e.metaKey
      ) {
        e.preventDefault();
        setShowUnderlines(true);
        setKeyboardMode(true);
        setFocusedTopIdx(0);
        setOpenIdx(null);
        return;
      }

      // Alt+letter while Alt is held — open matching top-level.
      if (e.altKey && altPressedRef.current && e.key.length === 1) {
        altChordedRef.current = true;
        const ch = e.key.toLowerCase();
        const idx = parsedTopLevels.findIndex((p) => p.mnemonic === ch);
        if (idx >= 0) {
          e.preventDefault();
          setShowUnderlines(true);
          setKeyboardMode(true);
          setOpenIdx(idx);
          setFocusedTopIdx(idx);
          setFocusedItemIdx(0);
        }
        return;
      }

      // Any other key while Alt is held marks the press as chorded
      // so keyup won't enter menu mode.
      if (e.altKey && altPressedRef.current) {
        altChordedRef.current = true;
        return;
      }

      // From here down, we only act if menu mode is on OR a popup
      // is open from a mouse click — otherwise the page owns the key.
      const popupOpen = openIdx !== null;
      if (!keyboardMode && !popupOpen) return;

      if (e.key === 'Escape') {
        e.preventDefault();
        reset();
        return;
      }

      if (e.key === 'ArrowLeft') {
        e.preventDefault();
        const cur = focusedTopIdx ?? 0;
        const next = (cur - 1 + topLevels.length) % topLevels.length;
        setFocusedTopIdx(next);
        if (popupOpen) {
          setOpenIdx(next);
          setFocusedItemIdx(0);
        }
        return;
      }
      if (e.key === 'ArrowRight') {
        e.preventDefault();
        const cur = focusedTopIdx ?? 0;
        const next = (cur + 1) % topLevels.length;
        setFocusedTopIdx(next);
        if (popupOpen) {
          setOpenIdx(next);
          setFocusedItemIdx(0);
        }
        return;
      }

      if (e.key === 'ArrowDown') {
        e.preventDefault();
        if (!popupOpen) {
          const idx = focusedTopIdx ?? 0;
          setOpenIdx(idx);
          setFocusedTopIdx(idx);
          setFocusedItemIdx(0);
          return;
        }
        const items = activatableChildren(openIdx);
        if (items.length === 0) return;
        const cur = focusedItemIdx ?? -1;
        setFocusedItemIdx((cur + 1) % items.length);
        return;
      }
      if (e.key === 'ArrowUp') {
        e.preventDefault();
        if (!popupOpen) return;
        const items = activatableChildren(openIdx);
        if (items.length === 0) return;
        const cur = focusedItemIdx ?? 0;
        setFocusedItemIdx((cur - 1 + items.length) % items.length);
        return;
      }

      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        if (!popupOpen) {
          const idx = focusedTopIdx ?? 0;
          setOpenIdx(idx);
          setFocusedItemIdx(0);
          return;
        }
        const items = activatableChildren(openIdx);
        const item = items[focusedItemIdx ?? 0];
        if (item) activate(item);
        return;
      }

      // Letter activation in menu mode.
      if (e.key.length === 1) {
        const ch = e.key.toLowerCase();
        if (popupOpen) {
          const items = activatableChildren(openIdx);
          const idx = items.findIndex((it) => {
            const label = it.kind === 'rawItem' ? it.label : t(it.labelKey);
            return parseMnemonic(label).mnemonic === ch;
          });
          if (idx >= 0) {
            e.preventDefault();
            activate(items[idx]);
          }
          return;
        }
        // Menu mode, no popup yet — open matching top-level.
        const idx = parsedTopLevels.findIndex((p) => p.mnemonic === ch);
        if (idx >= 0) {
          e.preventDefault();
          setOpenIdx(idx);
          setFocusedTopIdx(idx);
          setFocusedItemIdx(0);
        }
      }
    };

    const onKeyUp = (e: KeyboardEvent): void => {
      if (e.key !== 'Alt') return;
      const wasPressed = altPressedRef.current;
      const wasChorded = altChordedRef.current;
      altPressedRef.current = false;
      altChordedRef.current = false;
      if (!wasPressed) return;
      if (wasChorded) {
        // Alt was used in a chord (Alt+letter handled separately, or
        // some non-menu chord). If no menu opened, drop underlines.
        if (openIdx === null && !keyboardMode) setShowUnderlines(false);
        return;
      }
      // Bare Alt tap → enter menu mode if not already in something.
      if (openIdx === null && !keyboardMode) {
        setShowUnderlines(true);
        setKeyboardMode(true);
        setFocusedTopIdx(0);
      } else if (keyboardMode) {
        // Alt while already in menu mode → exit.
        reset();
      }
    };

    const onMouseDown = (e: MouseEvent): void => {
      // The popup renders into document.body via createPortal, so a
      // click on a popup item is NOT contained by barRef. Without the
      // [data-menu-popup] exception, the window-level mousedown
      // closes the menu before React fires the item's onClick — and
      // the command never runs.
      const target = e.target as Element | null;
      if (barRef.current?.contains(target)) return;
      if (target?.closest('[data-menu-popup]')) return;
      reset();
    };

    window.addEventListener('keydown', onKeyDown);
    window.addEventListener('keyup', onKeyUp);
    window.addEventListener('mousedown', onMouseDown);
    return () => {
      window.removeEventListener('keydown', onKeyDown);
      window.removeEventListener('keyup', onKeyUp);
      window.removeEventListener('mousedown', onMouseDown);
    };
  }, [openIdx, keyboardMode, focusedTopIdx, focusedItemIdx, parsedTopLevels, topLevels, t]);

  // Drop menu mode if the window loses focus (Alt-Tab away).
  useEffect(() => {
    const onBlur = (): void => {
      altPressedRef.current = false;
      altChordedRef.current = false;
      reset();
    };
    window.addEventListener('blur', onBlur);
    return () => window.removeEventListener('blur', onBlur);
  }, []);

  return (
    <div ref={barRef} className={cn('app-no-drag flex items-center', className)}>
      {topLevels.map((node, i) => (
        <TopLevel
          key={i}
          parsed={parsedTopLevels[i]}
          open={openIdx === i}
          focused={focusedTopIdx === i}
          showUnderline={showUnderlines}
          onMouseOpen={() => {
            // Mouse-driven open: no keyboard focus on items.
            setOpenIdx(i);
            setFocusedTopIdx(null);
            setFocusedItemIdx(null);
          }}
          onMouseHover={() => {
            if (openIdx !== null && openIdx !== i) {
              setOpenIdx(i);
              setFocusedTopIdx(null);
              setFocusedItemIdx(null);
            }
          }}
          onMouseClose={() => reset()}
          submenu={node.children}
          focusedItemIdx={focusedItemIdx}
          activate={activate}
          showUnderlines={showUnderlines}
        />
      ))}
    </div>
  );
}

function TopLevel({
  parsed,
  open,
  focused,
  showUnderline,
  onMouseOpen,
  onMouseHover,
  onMouseClose,
  submenu,
  focusedItemIdx,
  activate,
  showUnderlines,
}: {
  parsed: ParsedLabel;
  open: boolean;
  focused: boolean;
  showUnderline: boolean;
  onMouseOpen: () => void;
  onMouseHover: () => void;
  onMouseClose: () => void;
  submenu: MenuNode[];
  focusedItemIdx: number | null;
  activate: (item: ActivatableNode) => void;
  showUnderlines: boolean;
}) {
  const ref = useRef<HTMLButtonElement>(null);
  const rect = ref.current?.getBoundingClientRect();
  return (
    <>
      <button
        ref={ref}
        type="button"
        tabIndex={-1}
        onMouseDown={(e) => {
          e.preventDefault();
          if (open) onMouseClose();
          else onMouseOpen();
        }}
        onMouseEnter={onMouseHover}
        className={cn(
          'h-full px-3 text-xs hover:bg-muted',
          (open || focused) && 'bg-muted',
        )}
      >
        <MnemonicLabel parsed={parsed} underline={showUnderline} />
      </button>
      {open && rect &&
        createPortal(
          <Popup
            x={rect.left}
            y={rect.bottom}
            nodes={submenu}
            focusedItemIdx={focusedItemIdx}
            activate={activate}
            showUnderlines={showUnderlines}
          />,
          document.body,
        )}
    </>
  );
}

function Popup({
  x,
  y,
  nodes,
  focusedItemIdx,
  activate,
  showUnderlines,
}: {
  x: number;
  y: number;
  nodes: MenuNode[];
  focusedItemIdx: number | null;
  activate: (item: ActivatableNode) => void;
  showUnderlines: boolean;
}) {
  const { t } = useTranslation();
  // Map render-position → activatable-item-index for focus styling.
  // Separators are visible but unfocusable; roles render as null and
  // don't count either.
  let itemCursor = -1;
  return (
    <div
      data-menu-popup
      className="fixed z-50 min-w-[14rem] rounded-xs border bg-popover py-1 text-xs text-popover-foreground shadow-md"
      style={{ left: x, top: y }}
    >
      {nodes.map((n, i) => {
        if (n.kind === 'separator') return <div key={i} className="my-1 border-t" />;
        // Role items (cut/copy/paste/undo/zoom/devtools) work via
        // the native menu's global accelerators on Win/Linux even
        // when the bar itself is hidden. Omitting them from the
        // in-window menu keeps it focused on commands the user
        // can't trigger any other way.
        if (n.kind === 'role') return null;
        if (n.kind === 'submenu') {
          // Nested submenus aren't fully implemented in the in-window
          // bar yet: render the first level only and surface the
          // children as flat items beneath their parent label. The
          // submenu's own label is shown as a non-interactive divider
          // so the recents grouping is still visible. Native menus
          // (macOS / Electron's accelerator hosting) get the full
          // nested tree.
          if (n.children.length === 0) return null;
          return (
            <div key={i}>
              <div className="px-3 py-1 text-[10px] uppercase tracking-wide text-muted-foreground">
                {t(n.labelKey).replace(/&/g, '')}
              </div>
              {n.children.map((c, j) => {
                if (!isActivatable(c)) return null;
                itemCursor++;
                const label = c.kind === 'rawItem' ? c.label : t(c.labelKey);
                const parsed = parseMnemonic(label);
                const isFocused = focusedItemIdx === itemCursor;
                return (
                  <button
                    key={`${i}-${j}`}
                    type="button"
                    tabIndex={-1}
                    disabled={c.enabled === false}
                    onClick={() => activate(c)}
                    className={cn(
                      'flex w-full items-center justify-between px-3 py-1 text-left hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50',
                      isFocused && 'bg-muted',
                    )}
                  >
                    <span className="truncate">
                      <MnemonicLabel parsed={parsed} underline={showUnderlines} />
                    </span>
                    {c.kind === 'item' && c.accelerator && (
                      <span className="ml-6 text-muted-foreground">
                        {formatAccelerator(c.accelerator)}
                      </span>
                    )}
                  </button>
                );
              })}
            </div>
          );
        }
        itemCursor++;
        const label = n.kind === 'rawItem' ? n.label : t(n.labelKey);
        const parsed = parseMnemonic(label);
        const isFocused = focusedItemIdx === itemCursor;
        return (
          <button
            key={i}
            type="button"
            tabIndex={-1}
            disabled={n.enabled === false}
            onClick={() => activate(n)}
            className={cn(
              'flex w-full items-center justify-between px-3 py-1 text-left hover:bg-muted disabled:cursor-not-allowed disabled:opacity-50',
              isFocused && 'bg-muted',
            )}
          >
            <span className="truncate">
              <MnemonicLabel parsed={parsed} underline={showUnderlines} />
            </span>
            {n.kind === 'item' && n.accelerator && (
              <span className="ml-6 text-muted-foreground">
                {formatAccelerator(n.accelerator)}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}
