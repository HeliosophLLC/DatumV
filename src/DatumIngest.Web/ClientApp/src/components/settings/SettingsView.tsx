import { useEffect, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import {
  settingsState,
  setAnimations,
  setChromeStyle,
  setModelsDirectory,
  type ChromeStyle,
  type ThemePreference,
} from '@/state/settings';
import { setTheme } from '@/state/theme';
import { healthState, refreshHealth } from '@/state/health';
import { Button } from '@/components/ui/button';
import { cn } from '@/lib/utils';

const THEMES: readonly ThemePreference[] = ['system', 'light', 'dark'];
const CHROMES: readonly ChromeStyle[] = ['auto', 'windows', 'macos', 'linux'];
const LOCALES: readonly string[] = ['system', 'en'];
const ANIMATIONS: readonly ('on' | 'off')[] = ['on', 'off'];

// Settings page. Sections: Appearance, Language, Models, About. Each
// section is a labeled row + control. Models directory is the one
// "requires restart" setting; the hint is rendered inline rather than
// behind a toast so it's always visible when the field is dirty.
export function SettingsView() {
  const { t } = useTranslation('settings');
  const settings = useSnapshot(settingsState);
  const { data: health } = useSnapshot(healthState);

  // The Health endpoint reports the *effective* models directory after
  // the settings → env-var → default cascade. Refresh once on mount so
  // we render fresh truth rather than whatever was cached at app start.
  useEffect(() => {
    void refreshHealth();
  }, []);

  return (
    <div className="bg-editor flex h-full flex-col overflow-y-auto px-6 py-6">
      <div className="mx-auto flex w-full max-w-2xl flex-col gap-8">
        <h1 className="text-lg font-medium">{t('title')}</h1>

        <Section title={t('appearance.title')}>
          <Field label={t('appearance.theme')}>
            <ChipGroup
              options={THEMES}
              value={settings.theme}
              onChange={(v) => void setTheme(v)}
              labelFor={(v) =>
                t(`appearance.theme${capitalize(v)}` as 'appearance.themeSystem')
              }
            />
          </Field>
          <Field label={t('appearance.chrome')}>
            <ChipGroup
              options={CHROMES}
              value={settings.chromeStyle}
              onChange={(v) => void setChromeStyle(v)}
              labelFor={(v) =>
                t(`appearance.chrome${capitalize(v)}` as 'appearance.chromeAuto')
              }
            />
          </Field>
          <Field label={t('appearance.animations')}>
            <ChipGroup
              options={ANIMATIONS}
              value={settings.animations ? 'on' : 'off'}
              onChange={(v) => void setAnimations(v === 'on')}
              labelFor={(v) =>
                t(`appearance.animations${capitalize(v)}` as 'appearance.animationsOn')
              }
            />
          </Field>
        </Section>

        <Section title={t('language.title')}>
          <Field label={t('language.locale')}>
            <ChipGroup
              options={LOCALES}
              value={settings.locale}
              onChange={(v) => void setLocaleSetting(v)}
              labelFor={(v) =>
                t(`language.locale${capitalize(v)}` as 'language.localeSystem')
              }
            />
          </Field>
        </Section>

        <Section title={t('models.title')}>
          <ModelsDirectoryField
            persistedValue={settings.modelsDirectory}
            effectivePath={health?.modelsDirectory ?? null}
          />
        </Section>

        <Section title={t('about.title')}>
          <Field label={t('about.version')}>
            <span className="text-muted-foreground text-sm">
              {health?.version ?? '—'}
            </span>
          </Field>
          <Field label={t('about.catalogPath')}>
            <span className="text-muted-foreground font-mono text-xs break-all">
              {health?.catalogPath ?? '—'}
            </span>
          </Field>
          <Field label={t('about.node')}>
            <span className="text-muted-foreground text-sm">
              {health?.nodeId ?? '—'}
            </span>
          </Field>
        </Section>
      </div>
    </div>
  );
}

function Section({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <section className="flex flex-col gap-3">
      <h2 className="text-muted-foreground text-xs font-medium tracking-wide uppercase">
        {title}
      </h2>
      <div className="flex flex-col gap-3 rounded-xs border p-4">{children}</div>
    </section>
  );
}

function Field({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-1.5 sm:flex-row sm:items-center sm:justify-between sm:gap-4">
      <span className="text-sm font-medium">{label}</span>
      <div className="sm:flex-1 sm:text-right">{children}</div>
    </div>
  );
}

function ChipGroup<T extends string>({
  options,
  value,
  onChange,
  labelFor,
}: {
  options: readonly T[];
  value: T;
  onChange: (v: T) => void;
  labelFor: (v: T) => string;
}) {
  return (
    <div className="inline-flex flex-wrap gap-1.5">
      {options.map((opt) => (
        <button
          key={opt}
          type="button"
          onClick={() => onChange(opt)}
          aria-pressed={value === opt}
          className={cn(
            'rounded-xs border px-2.5 py-1 text-xs transition-colors',
            value === opt
              ? 'border-primary bg-primary text-primary-foreground'
              : 'border-border text-muted-foreground hover:border-foreground/40 hover:text-foreground',
          )}
        >
          {labelFor(opt)}
        </button>
      ))}
    </div>
  );
}

function ModelsDirectoryField({
  persistedValue,
  effectivePath,
}: {
  persistedValue: string;
  effectivePath: string | null;
}) {
  const { t } = useTranslation('settings');
  const [draft, setDraft] = useState(persistedValue);
  const [saved, setSaved] = useState(false);

  // Reset draft when the persisted value changes from elsewhere (e.g.
  // refreshSettings on app boot).
  useEffect(() => {
    setDraft(persistedValue);
    setSaved(false);
  }, [persistedValue]);

  const dirty = draft !== persistedValue;

  async function onSave() {
    await setModelsDirectory(draft.trim());
    setSaved(true);
  }

  return (
    <div className="flex flex-col gap-2">
      <span className="text-sm font-medium">{t('models.directory')}</span>
      {effectivePath && (
        <span className="text-muted-foreground font-mono text-xs break-all">
          {t('models.directoryEffective', { path: effectivePath })}
        </span>
      )}
      <div className="flex gap-2">
        <input
          type="text"
          value={draft}
          onChange={(e) => {
            setDraft(e.target.value);
            setSaved(false);
          }}
          placeholder={t('models.directoryPlaceholder')}
          className="bg-background focus-visible:ring-ring flex-1 rounded-xs border px-2 py-1 font-mono text-xs focus-visible:ring-2 focus-visible:outline-none"
        />
        <Button type="button" size="sm" onClick={onSave} disabled={!dirty}>
          {t('models.directorySave')}
        </Button>
      </div>
      <span className="text-muted-foreground text-xs">
        {saved ? t('models.directorySaved') : t('models.directoryHint')}
      </span>
    </div>
  );
}

function setLocaleSetting(locale: string): Promise<void> {
  // Mirrors setChromeStyle's shape; defined inline because state/locale
  // already owns the side-effect (apply to <html lang> + i18next). We
  // could import the existing setLocale, but doing the PATCH directly
  // keeps the dependency direction clean.
  return import('@/state/settings').then((m) =>
    m.updateSettings({ locale }),
  );
}

function capitalize(s: string): string {
  return s.length === 0 ? s : s[0].toUpperCase() + s.slice(1);
}
