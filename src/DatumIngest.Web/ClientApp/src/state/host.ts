import { proxy } from 'valtio';
import { os, runtime, type HostOs, type HostRuntime } from '../host';

// Detected once at module load — these don't change at runtime. The proxy
// is for consumers who prefer useSnapshot; for one-shot reads, import
// `os` / `runtime` from '../host' directly.
export const hostState = proxy<{ os: HostOs; runtime: HostRuntime }>({
  os,
  runtime,
});

export type { HostOs, HostRuntime };
