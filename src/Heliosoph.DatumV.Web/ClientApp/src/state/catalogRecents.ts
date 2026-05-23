import { proxy } from 'valtio';
import { host, type RecentCatalog } from '@/host';

// Mirror of the recents list that lives in main's `recents.json`. Read
// at app start (and after each catalog swap) so the File → Open Recent
// submenu can render without an async fetch in the menu-build path.

interface CatalogRecentsState {
  recents: RecentCatalog[];
}

export const catalogRecentsState = proxy<CatalogRecentsState>({ recents: [] });

export async function refreshCatalogRecents(): Promise<void> {
  try {
    const list = await host.getRecentCatalogs();
    catalogRecentsState.recents = list;
  } catch (err) {
    console.error('[catalogRecents] failed to refresh:', err);
  }
}
