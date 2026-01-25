import { proxy } from 'valtio';
import { api } from '../api';
import type { SettingsDto, SettingsPatchDto, ThemePreference } from '../api/generated/openapi-client';

// Mirrors SettingsDto. Server defaults fill in any unset fields on GET, so
// after refreshSettings the local copy is always non-null. The initial value
// here is just what we render before the first fetch completes (~50ms in
// Photino).
interface SettingsState {
  theme: ThemePreference;
}

export const settingsState = proxy<SettingsState>({
  theme: 'system',
});

export async function refreshSettings(): Promise<void> {
  try {
    const dto = await api.settings.get();
    Object.assign(settingsState, dto);
  } catch (err) {
    console.error('[settings] refresh failed', err);
  }
}

export async function updateSettings(patch: SettingsPatchDto): Promise<void> {
  try {
    const dto = await api.settings.patch(patch);
    Object.assign(settingsState, dto);
  } catch (err) {
    console.error('[settings] update failed', err);
  }
}

// Helper for state slices that want to assert SettingsDto shape (e.g. theme).
export type { SettingsDto, ThemePreference };
