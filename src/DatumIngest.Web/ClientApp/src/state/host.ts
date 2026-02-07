import { proxy } from 'valtio';
import { os, type HostOs } from '../host';

// OS is detected once at module load and never changes at runtime. The proxy
// exists so consumers who prefer useSnapshot get the same access pattern as
// the rest of the app state; one-shot readers can import `os` from '../host'
// directly.
//
// Runtime detection ('photino' vs 'browser') was removed when the app
// dropped browser support — DatumIngest is Photino-only.
export const hostState = proxy<{ os: HostOs }>({
  os,
});

export type { HostOs };
