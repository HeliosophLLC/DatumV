import { minimize, toggleMaximize, close } from '@/state/window';

// macOS-flavored: 28px tall, three circular "traffic lights" left
// (close/minimize/zoom), title centered to the full bar (not the remaining
// space after traffic lights — matches real macOS). Circles are the one
// place we break the rounded-xs rule; at 12px the *shape* is what people
// identify as Mac.
export function MacTitleBar() {
  return (
    <header className="app-drag relative flex h-7 items-center border-border bg-background px-3 select-none">
      <div className="app-no-drag z-10 flex items-center gap-2">
        <button
          type="button"
          onClick={close}
          aria-label="Close"
          className="size-3 rounded-full bg-[#ff5f57] hover:brightness-90"
        />
        <button
          type="button"
          onClick={minimize}
          aria-label="Minimize"
          className="size-3 rounded-full bg-[#febc2e] hover:brightness-90"
        />
        <button
          type="button"
          onClick={toggleMaximize}
          aria-label="Zoom"
          className="size-3 rounded-full bg-[#28c840] hover:brightness-90"
        />
      </div>
      <div className="pointer-events-none absolute inset-0 flex items-center justify-center text-xs text-muted-foreground">
        DatumIngest
      </div>
    </header>
  );
}
