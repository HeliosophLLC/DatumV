import { proxy } from 'valtio';
import { api } from '../api';
import type {
  SettingsDto,
  SettingsPatchDto,
  ThemePreference,
  ChromeStyle,
  KeepRawDownloadsMode,
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
  // cascade" ($DATUM_MODELS env → %LOCALAPPDATA%/Heliosoph.DatumV/models). Read
  // once at startup; runtime changes require a restart to take effect.
  modelsDirectory: string;
  // User-configured raw datasets cache directory. Empty string = "use
  // the resolution cascade" ($DATUM_DATASETS env →
  // %LOCALAPPDATA%/Heliosoph.DatumV/datasets-cache). Read once at startup;
  // runtime changes require a restart to take effect.
  datasetsDirectory: string;
  // What to do with the raw archives in the dataset cache after a
  // successful ingest. `ask` keeps the cache and (in a later release)
  // surfaces a prompt; `always` keeps it forever; `never` deletes it
  // immediately after each install. Re-read by the dataset download
  // service on every install — flipping the chip applies without a
  // restart.
  keepRawDownloads: KeepRawDownloadsMode;
  // When false, the shell suppresses transition animations (chat dock
  // slide, future page transitions). Honours a user who prefers reduced
  // motion or just dislikes the movement.
  animations: boolean;
  // Persisted dock layout. Initial values match the server defaults so
  // the pre-fetch render shows the canonical layout; the dock state
  // module (state/nav.ts) re-seeds these fields' downstream copies via
  // hydrateDockFromSettings() once the real document arrives.
  dockLeftItems: string[];
  dockRightItems: string[];
  openLeftPanel: string | null;
  openRightPanel: string | null;
  // Per-cell-kind default display mode for the results-pane grid (see
  // state/columnDisplayModes.ts). Keys are registry `kindKey` values;
  // values are mode ids registered for that kind. Missing keys fall
  // back to the registry baseline. Per-column chip overrides happen
  // client-side in CellTable and don't persist.
  columnDisplayModeDefaults: Record<string, string>;
  // Catalog name of the preferred chat LLM. Null = "auto" (largest
  // model fitting in the VRAM budget). The server reads this once on
  // first chat load — runtime changes apply after a restart.
  defaultLlmModel: string | null;
}

export const settingsState = proxy<SettingsState>({
  theme: 'system',
  chromeStyle: 'auto',
  locale: 'system',
  modelsDirectory: '',
  datasetsDirectory: '',
  keepRawDownloads: 'ask',
  animations: true,
  dockLeftItems: ['catalog', 'procedures', 'projects'],
  dockRightItems: [],
  openLeftPanel: null,
  openRightPanel: null,
  columnDisplayModeDefaults: {},
  defaultLlmModel: null,
});

function applyDto(dto: SettingsDto): void {
  // NSwag generates every field as optional even though the server's
  // GET path fills defaults — fall back to the proxy's current values
  // so a partial document (which we shouldn't ever see in practice)
  // doesn't blow null into typed slots.
  settingsState.theme = dto.theme ?? settingsState.theme;
  settingsState.chromeStyle = dto.chromeStyle ?? settingsState.chromeStyle;
  settingsState.locale = dto.locale ?? settingsState.locale;
  settingsState.modelsDirectory = dto.modelsDirectory ?? settingsState.modelsDirectory;
  settingsState.datasetsDirectory = dto.datasetsDirectory ?? settingsState.datasetsDirectory;
  settingsState.keepRawDownloads = dto.keepRawDownloads ?? settingsState.keepRawDownloads;
  settingsState.animations = dto.animations ?? settingsState.animations;
  settingsState.dockLeftItems = dto.dockLeftItems ?? [];
  settingsState.dockRightItems = dto.dockRightItems ?? [];
  settingsState.openLeftPanel = dto.openLeftPanel ?? null;
  settingsState.openRightPanel = dto.openRightPanel ?? null;
  // NSwag may type the dict as `{ [key: string]: string } | undefined`;
  // empty-object fallback keeps every read site safe to index.
  settingsState.columnDisplayModeDefaults = (dto.columnDisplayModeDefaults ?? {}) as Record<string, string>;
  settingsState.defaultLlmModel = dto.defaultLlmModel ?? null;
}

export async function refreshSettings(): Promise<void> {
  try {
    const dto = await api.settings.get();
    applyDto(dto);
  } catch (err) {
    console.error('[settings] refresh failed', err);
  }
}

export async function updateSettings(patch: SettingsPatchDto): Promise<void> {
  try {
    const dto = await api.settings.patch(patch);
    applyDto(dto);
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

export function setDatasetsDirectory(datasetsDirectory: string): Promise<void> {
  return updateSettings({ datasetsDirectory });
}

export function setKeepRawDownloads(
  keepRawDownloads: KeepRawDownloadsMode,
): Promise<void> {
  return updateSettings({ keepRawDownloads });
}

// `null` reverts the chat surface to the auto-pick (largest installed
// LLM that fits VRAM). The server flips the persisted slot back to null
// via the explicit clear flag — sending `defaultLlmModel: null` alone
// would be ignored as a no-op patch.
export function setDefaultLlmModel(name: string | null): Promise<void> {
  if (name === null) {
    return updateSettings({ clearDefaultLlmModel: true });
  }
  return updateSettings({ defaultLlmModel: name });
}

/**
 * Upserts the default display mode for a single cell-kind. The server
 * patches as a full-dict replace, so we merge the existing dict with
 * the new entry client-side and ship the merged result. Passing the
 * registry's baseline mode removes the entry rather than persisting an
 * override that just matches the default (keeps the dict minimal).
 */
export function setColumnDisplayModeDefault(
  kindKey: string,
  modeId: string,
  registryBaseline: string,
): Promise<void> {
  const next: Record<string, string> = { ...settingsState.columnDisplayModeDefaults };
  if (modeId === registryBaseline) {
    delete next[kindKey];
  } else {
    next[kindKey] = modeId;
  }
  return updateSettings({ columnDisplayModeDefaults: next });
}

export type { SettingsDto, ThemePreference, ChromeStyle, KeepRawDownloadsMode };
