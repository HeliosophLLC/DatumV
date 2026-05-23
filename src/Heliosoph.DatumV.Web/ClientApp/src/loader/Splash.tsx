import { useEffect, useState } from 'react';

// Loader rendered during initial backend startup and during catalog
// swap. Stage updates arrive via main's `splash:status` IPC; the
// preload bridge exposes that subscription as `onSplashStatus`.
export function Splash(): React.JSX.Element {
  const [status, setStatus] = useState('Starting…');

  useEffect(() => {
    return window.electronHost.onSplashStatus((text) => setStatus(text));
  }, []);

  return (
    <div className="flex w-full w-[420px] flex-col items-center p-8">
      <div className="text-foreground mb-1.5 text-3xl font-semibold tracking-wide">DatumV</div>
      <div className="text-muted-foreground mb-7 text-xs">Heliosoph</div>
      <div className="text-muted-foreground min-h-[1.2em] text-center text-xs">{status}</div>
      <ProgressBar />
    </div>
  );
}

function ProgressBar(): React.JSX.Element {
  return (
    <div className="bg-muted relative mt-4 h-0.5 w-3/5 overflow-hidden rounded-sm">
      <div className="splash-bar absolute top-0 h-full w-2/5" />
      <style>{`
        .splash-bar {
          left: -40%;
          background: linear-gradient(90deg, transparent, var(--primary), transparent);
          animation: splash-slide 1.4s ease-in-out infinite;
        }
        @keyframes splash-slide {
          0%   { left: -40%; }
          100% { left: 100%; }
        }
      `}</style>
    </div>
  );
}
