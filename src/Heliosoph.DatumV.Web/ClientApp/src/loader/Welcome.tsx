import { useEffect, useState } from 'react';

interface RecentCatalog {
  path: string;
  displayName: string;
  lastOpenedAt: string;
}

// First-launch picker. Two primary actions (New / Open) plus a
// recents list when main has any to offer. All catalog actions go
// through the same preload bridge the SPA uses for the File menu —
// main owns the picker, swap, and backend respawn.
export function Welcome(): React.JSX.Element {
  const [recents, setRecents] = useState<RecentCatalog[]>([]);
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    void window.electronHost.catalogGetRecents().then(setRecents);
  }, []);

  async function withBusy<T>(fn: () => Promise<T>): Promise<void> {
    if (busy) return;
    setBusy(true);
    try {
      const result = (await fn()) as { canceled?: boolean } | undefined;
      // On success, main reloads this window with splash → SPA; the
      // page is about to navigate away so leaving busy=true is fine.
      // On cancel/error, restore the buttons.
      if (!result || result.canceled) setBusy(false);
    } catch (err) {
      console.error('[welcome] action failed:', err);
      setBusy(false);
    }
  }

  return (
    <div className="flex w-full max-w-[560px] flex-col gap-7 select-none">
      <div className="text-center">
        <h1 className="text-foreground m-0 mb-2 text-3xl font-semibold tracking-wide">
          Welcome to DatumV
        </h1>
        <p className="text-muted-foreground m-0 text-sm leading-relaxed">
          A catalog is a folder on your machine that holds your tables,
          queries, and project files. Pick one to begin.
        </p>
      </div>

      <div className="app-no-drag flex flex-col gap-3">
        <PrimaryButton
          label="New Catalog…"
          hint="Create a fresh catalog in an empty folder."
          disabled={busy}
          onClick={() => withBusy(() => window.electronHost.catalogNewPicker())}
        />
        <PrimaryButton
          label="Open Catalog…"
          hint="Open an existing catalog from disk."
          disabled={busy}
          onClick={() => withBusy(() => window.electronHost.catalogOpenPicker())}
        />
      </div>

      {recents.length > 0 && (
        <RecentsList
          recents={recents}
          disabled={busy}
          onPick={(path) => withBusy(() => window.electronHost.catalogOpenPath(path))}
        />
      )}

      <p className="text-muted-foreground/70 m-0 text-center text-[11px]">
        You can switch catalogs later from the File menu.
      </p>
    </div>
  );
}

interface PrimaryButtonProps {
  label: string;
  hint: string;
  disabled: boolean;
  onClick: () => void;
}

function PrimaryButton({ label, hint, disabled, onClick }: PrimaryButtonProps): React.JSX.Element {
  return (
    <button
      type="button"
      disabled={disabled}
      onClick={onClick}
      className="app-no-drag border-border bg-card/60 hover:bg-accent hover:border-accent-foreground/20 flex cursor-pointer flex-col items-start gap-1 rounded-md border px-5 py-4 text-left transition-colors disabled:cursor-progress disabled:opacity-50"
    >
      <span className="text-foreground text-[15px] font-semibold">{label}</span>
      <span className="text-muted-foreground text-xs">{hint}</span>
    </button>
  );
}

interface RecentsListProps {
  recents: RecentCatalog[];
  disabled: boolean;
  onPick: (path: string) => void;
}

function RecentsList({ recents, disabled, onPick }: RecentsListProps): React.JSX.Element {
  return (
    <div className="app-no-drag flex flex-col gap-2">
      <div className="text-muted-foreground px-1 text-[11px] font-medium uppercase tracking-wider">
        Recent
      </div>
      <div className="flex flex-col">
        {recents.map((r) => (
          <button
            key={r.path}
            type="button"
            disabled={disabled}
            onClick={() => onPick(r.path)}
            className="hover:bg-accent flex cursor-pointer flex-col items-start gap-0.5 rounded-md px-3 py-2 text-left transition-colors disabled:cursor-progress disabled:opacity-50"
            title={r.path}
          >
            <span className="text-foreground text-sm font-medium">{r.displayName}</span>
            <span className="text-muted-foreground w-full truncate text-xs">{r.path}</span>
          </button>
        ))}
      </div>
    </div>
  );
}
