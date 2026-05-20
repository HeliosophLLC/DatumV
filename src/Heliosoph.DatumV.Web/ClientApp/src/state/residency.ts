import { proxy } from 'valtio';

import { api } from '@/api';
import {
  acquireCatalogHub,
  onModelActiveChanged,
  onModelEvicted,
  onModelLoaded,
} from '@/api/catalogHub';
import type { EvictStatus } from '@/api/generated/openapi-client';

// Live residency state for the status-bar chip. Seeded once from
// /api/model-runtime/residency, then kept current via CatalogHub
// events: OnModelLoaded / OnModelEvicted / OnModelActiveChanged.
//
// The chip reads `residencyState.byName` (map of model name → entry)
// to render the resident-models list. The popover renders the same
// data with the EVICT button per row.

export interface ResidencyEntry {
  modelName: string;
  weightCostBytes: number;
  // 0 when idle, >= 1 when at least one query holds a lease. The
  // engine coalesces emissions to the 0↔1 edges, so this is "idle vs
  // busy" rather than a precise refcount.
  activeRefs: number;
}

interface ResidencyState {
  byName: Record<string, ResidencyEntry>;
  // Bumped each time a state mutation lands so consumers using
  // useSnapshot see the change even when the change is a single
  // field replacement on an existing entry. Valtio observes nested
  // mutations on its own, but the explicit revision lets memoised
  // computations (sums, sorted lists) invalidate on any update with
  // a single dependency.
  revision: number;
  // True once the initial seed completed (success or failure).
  seeded: boolean;
}

export const residencyState = proxy<ResidencyState>({
  byName: {},
  revision: 0,
  seeded: false,
});

function bump(): void {
  residencyState.revision = (residencyState.revision + 1) | 0;
}

/**
 * One-shot initial seed from /api/model-runtime/residency. Idempotent:
 * a second call replaces the current map with the latest server view.
 * Hub events that arrive between the call and the response are
 * harmless — they mutate the same map and the seed-fetched data
 * overwrites them; the worst case is a brief transient where a
 * just-loaded model is missing from the chip for one tick.
 */
export async function refreshResidency(): Promise<void> {
  try {
    const list = await api.modelRuntime.getResidency();
    const next: Record<string, ResidencyEntry> = {};
    for (const r of list) {
      // NSwag types every field as optional. The server never omits
      // these in practice — skip the row if name is missing rather
      // than insert under an empty key.
      if (!r.name) continue;
      next[r.name] = {
        modelName: r.name,
        weightCostBytes: r.weightCostBytes ?? 0,
        activeRefs: r.activeRefs ?? 0,
      };
    }
    residencyState.byName = next;
    residencyState.seeded = true;
    bump();
  } catch (err) {
    console.error('[residency] seed failed', err);
    residencyState.seeded = true;
  }
}

/**
 * User-driven evict via /api/model-runtime/{name}/evict. The optimistic
 * removal here is a UX nicety — the server's OnModelEvicted hub event
 * will land shortly and remove the entry authoritatively. If the
 * server refuses (model is pinned), we re-fetch to restore the row.
 */
export async function evictModel(modelName: string): Promise<EvictStatus> {
  try {
    const outcome = await api.modelRuntime.evict(modelName);
    const status: EvictStatus = outcome.status ?? 'notResident';
    if (status === 'pinned') {
      // Server refused. Re-fetch to make sure our local view didn't
      // get out of sync from a half-applied optimistic removal.
      await refreshResidency();
    }
    return status;
  } catch (err) {
    console.error('[residency] evict failed', err);
    return 'notResident';
  }
}

// ───────────────────────── Hub event subscriptions ─────────────────────────

// Prime the hub connection so events flow even before a chip mounts.
// `state/conversation.ts` and `state/downloads.ts` use the same
// pattern — fire and forget; failures surface when an actual op runs.
void acquireCatalogHub().catch(() => {
  // Connection failure surfaces later when an op runs.
});

onModelLoaded((ev) => {
  residencyState.byName[ev.modelName] = {
    modelName: ev.modelName,
    weightCostBytes: ev.weightCostBytes,
    // OnModelLoaded fires at the end of the loader path, where the
    // loader's own lease bump is the first active ref. The
    // ActiveChanged event coalescing fires the 0→1 edge separately;
    // seed at 1 here so the chip flags the model as busy
    // immediately rather than waiting for the second event.
    activeRefs: 1,
  };
  bump();
});

onModelEvicted((ev) => {
  delete residencyState.byName[ev.modelName];
  bump();
});

onModelActiveChanged((ev) => {
  const entry = residencyState.byName[ev.modelName];
  if (entry) {
    entry.activeRefs = ev.activeRefs;
    bump();
  }
  // No-op when the entry isn't in our map yet — a 0→1 fired before
  // the seed completed (rare race). The next seed will pick it up.
});
