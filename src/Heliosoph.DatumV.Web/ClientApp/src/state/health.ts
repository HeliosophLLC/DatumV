import { proxy } from 'valtio';
import { api } from '../api';
import type { HealthDto } from '../api/generated/openapi-client';

// Proxy + actions for /api/health. Actions own the fetch — views never call
// the generated client directly. See feedback_api_calls_in_state memory.

export const healthState = proxy<{ data: HealthDto | null }>({
  data: null,
});

export async function refreshHealth(): Promise<void> {
  try {
    console.log('[health] fetching…');
    healthState.data = await api.health.get();
    console.log('[health] ok', healthState.data);
  } catch (err) {
    console.error('[health] failed', err);
  }
}
