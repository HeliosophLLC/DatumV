import { proxy } from 'valtio';
import { api } from '../api';
import type {
  SettingsDto,
  SettingsPatchDto,
  ThemePreference,
  ChromeStyle,
} from '../api/generated/openapi-client';

// Mirrors SettingsDto. Server defaults fill in any unset fields on GET, so
// after refreshSettings the local copy is always non-null. The initial value
// here is just what we render before the first fetch completes (~50ms after
// the renderer mounts).
interface SettingsState {
  theme: ThemePreference;
  chromeStyle: ChromeStyle;
  // BCP 47 tag (e.g. 'en', 'en-US') or the sentinel 'system'. Resolved into
  // a concrete supported locale by state/locale.ts.
  locale: string;
  // User-configured models directory. Empty string = "use the resolution
  // cascade" ($DATUM_MODELS env → %LOCALAPPDATA%/DatumIngest/models). Read
  // once at startup; runtime changes require a restart to take effect.
  modelsDirectory: string;
  // When false, the shell suppresses transition animations (chat dock
  // slide, future page transitions). Honours a user who prefers reduced
  // motion or just dislikes the movement.
  animations: boolean;
}

export const settingsState = proxy<SettingsState>({
  theme: 'system',
  chromeStyle: 'auto',
  locale: 'system',
  modelsDirectory: '',
  animations: true,
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

export function setChromeStyle(chromeStyle: ChromeStyle): Promise<void> {
  return updateSettings({ chromeStyle });
}

export function setAnimations(animations: boolean): Promise<void> {
  return updateSettings({ animations });
}

export function setModelsDirectory(modelsDirectory: string): Promise<void> {
  return updateSettings({ modelsDirectory });
}

export type { SettingsDto, ThemePreference, ChromeStyle };
