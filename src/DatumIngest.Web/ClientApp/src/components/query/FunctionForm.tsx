import { useEffect, useMemo, useRef } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import {
  ChevronDown,
  ChevronRight,
  FunctionSquare,
  Paperclip,
  Play,
  Square,
  X,
} from 'lucide-react';
import type {
  ScalarFunctionDto,
  ScalarFunctionParameterDto,
  ScalarFunctionSignatureDto,
} from '@/api/generated/openapi-client';
import {
  functionCatalogState,
  loadFunctionCatalog,
  toggleCategoryExpanded,
} from '@/state/functionCatalog';
import {
  ensureFunctionForm,
  functionFormState,
  runFunctionTab,
  setFunctionFormFile,
  setFunctionFormSearch,
  setFunctionFormSelection,
  setFunctionFormText,
} from '@/state/functionForm';
import { cancelTab, executionsState } from '@/state/execution';
import {
  isBinaryParameter,
  isFormableVariant,
  synthesizeFunctionScript,
} from '@/lib/synthesizeFunctionScript';
import { cn } from '@/lib/utils';

// Execute-Function tab body. Three regions, top-to-bottom:
//   1. Function picker (searchable list, left rail).
//   2. Form for the picked function: overload selector when multiple
//      variants exist, then one input control per parameter.
//   3. Script preview — the synthesized DECLARE+SELECT batch that
//      executes when the user hits Run. The preview is live; it's the
//      *exact* string the runner will send to /api/query/stream once
//      the Run wiring lands.
//
// State lives in two proxies (`functionCatalogState`, `functionFormState`)
// and a non-proxy file map (see state/functionForm.ts). Per the "API
// calls in state, not views" rule the catalog fetch is triggered here
// via a side-effect, but the actual `fetch` happens inside the state
// module.

export function FunctionForm({ tabId }: { tabId: string }) {
  const { t } = useTranslation('query');
  const catalogSnap = useSnapshot(functionCatalogState);
  const formsSnap = useSnapshot(functionFormState);

  // Make sure the form slot exists before any setter runs. Done as an
  // effect (not inline) because mutating the proxy during render would
  // re-trigger render under Valtio's invalidation rules.
  useEffect(() => {
    ensureFunctionForm(tabId);
  }, [tabId]);

  useEffect(() => {
    void loadFunctionCatalog();
  }, []);

  const formSnap = formsSnap.byTabId[tabId];
  const search = formSnap?.search ?? '';
  const selection = formSnap?.selection ?? null;

  // Group + filter the scalar functions for the picker. Hide body-scoped
  // entries (e.g. `infer` only valid inside CREATE MODEL bodies) — they
  // belong in the SQL editor, not a standalone form. Search only kicks
  // in at ≥ 2 characters so a single keystroke doesn't snap-collapse
  // every category.
  const groupedFunctions = useMemo<CategoryGroup[]>(() => {
    const term = search.trim().toLowerCase();
    const searchActive = term.length >= 2;
    const matchPredicate = (f: ScalarFunctionDto) =>
      !searchActive
      || (f.name ?? '').toLowerCase().includes(term)
      || (f.aliases ?? []).some((a) => a.toLowerCase().includes(term));

    const buckets = new Map<string, ScalarFunctionDto[]>();
    for (const fn of catalogSnap.entries as readonly ScalarFunctionDto[]) {
      if (fn.bodyScope !== 'None') continue;
      if (!matchPredicate(fn)) continue;
      const category = fn.category ?? 'Other';
      const list = buckets.get(category);
      if (list) list.push(fn);
      else buckets.set(category, [fn]);
    }
    // Sort each bucket alphabetically by function name, then return
    // category groups in alphabetical order. The Map keeps insertion
    // order; we sort the entries explicitly so the rendered order is
    // stable across renders.
    const groups: CategoryGroup[] = [];
    for (const [category, fns] of buckets.entries()) {
      groups.push({
        category,
        functions: fns
          .slice()
          .sort((a, b) => (a.name ?? '').localeCompare(b.name ?? '')),
      });
    }
    groups.sort((a, b) => a.category.localeCompare(b.category));
    return groups;
  }, [catalogSnap.entries, search]);

  const searchActive = search.trim().length >= 2;

  // useSnapshot returns deep-readonly views which TypeScript can't
  // implicitly assign to the mutable generated DTOs. We `ref(entries)`
  // in the state module so the runtime objects are NOT proxied; the
  // cast is the type-only complement of that runtime escape hatch.
  const selectedFunction = useMemo(() => {
    if (!selection) return null;
    const match = catalogSnap.entries.find(
      (f) => f.schema === selection.schema && f.name === selection.name,
    );
    return (match ?? null) as ScalarFunctionDto | null;
  }, [catalogSnap.entries, selection]);

  const selectedVariant = useMemo<ScalarFunctionSignatureDto | null>(() => {
    if (!selectedFunction || !selection) return null;
    return (selectedFunction.signatures?.[selection.variantIndex]
      ?? null) as ScalarFunctionSignatureDto | null;
  }, [selectedFunction, selection]);

  return (
    <div className="flex h-full min-h-0 w-full flex-row overflow-hidden">
      <FunctionPicker
        tabId={tabId}
        search={search}
        searchActive={searchActive}
        groups={groupedFunctions}
        expandedCategories={catalogSnap.expandedCategories}
        selection={selection}
        status={catalogSnap.status}
        error={catalogSnap.error}
      />
      <div className="flex min-h-0 min-w-0 flex-1 flex-col overflow-hidden">
        {selectedFunction === null || formSnap === undefined ? (
          <EmptySelectionHint />
        ) : (
          <FormBody
            tabId={tabId}
            fn={selectedFunction as ScalarFunctionDto}
            variantIndex={selection!.variantIndex}
            variant={selectedVariant ?? null}
            textValues={formSnap.textValues}
            fileNames={formSnap.fileNames}
            fieldErrors={formSnap.fieldErrors}
          />
        )}
      </div>
    </div>
  );

  function EmptySelectionHint() {
    return (
      <div className="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
        {t('fnFormNoSelection')}
      </div>
    );
  }
}

// ───────────────────────── Function picker ─────────────────────────

interface CategoryGroup {
  category: string;
  functions: ScalarFunctionDto[];
}

function FunctionPicker({
  tabId,
  search,
  searchActive,
  groups,
  expandedCategories,
  selection,
  status,
  error,
}: {
  tabId: string;
  search: string;
  /** True when the search input has ≥ 2 chars and the function list is filtered. */
  searchActive: boolean;
  groups: CategoryGroup[];
  expandedCategories: Readonly<Record<string, true>>;
  selection: ReturnType<typeof useSnapshot<typeof functionFormState>>['byTabId'][string]['selection'] | null;
  status: 'idle' | 'loading' | 'ready' | 'error';
  error: string | null;
}) {
  const { t } = useTranslation('query');

  function onPick(fn: ScalarFunctionDto) {
    // Default to the first formable variant when picking a fresh
    // function — the user can flip to another one in the overload row
    // afterward. Falls back to 0 if none are formable (the form body
    // will render the "use a SQL tab" hint).
    const signatures = fn.signatures ?? [];
    const idx = signatures.findIndex(isFormableVariant);
    setFunctionFormSelection(tabId, {
      schema: fn.schema ?? 'system',
      name: fn.name ?? '',
      variantIndex: idx >= 0 ? idx : 0,
    });
  }

  return (
    <div className="border-border bg-background flex w-72 shrink-0 flex-col overflow-hidden border-r">
      <div className="border-border border-b p-2">
        <input
          type="text"
          value={search}
          onChange={(e) => setFunctionFormSearch(tabId, e.target.value)}
          placeholder={t('fnPickerSearchPlaceholder')}
          className={cn(
            'bg-input/30 placeholder:text-muted-foreground w-full rounded-md px-2 py-1.5 text-sm',
            'outline-none focus:ring-2 focus:ring-ring',
          )}
        />
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto p-1">
        {status === 'loading' && (
          <div className="text-muted-foreground p-2 text-xs">
            {t('fnPickerLoading')}
          </div>
        )}
        {status === 'error' && (
          <div className="text-destructive p-2 text-xs">
            {t('fnPickerError', { message: error ?? '' })}{' '}
            <button
              type="button"
              className="underline"
              onClick={() => void loadFunctionCatalog()}
            >
              {t('fnPickerRetry')}
            </button>
          </div>
        )}
        {status === 'ready' && groups.length === 0 && (
          <div className="text-muted-foreground p-2 text-xs">
            {t('fnPickerEmpty')}
          </div>
        )}
        <div className="flex flex-col">
          {groups.map((group) => (
            <CategorySection
              key={group.category}
              group={group}
              expanded={searchActive || !!expandedCategories[group.category]}
              // Search overrides the user's expand state — the toggle
              // would do nothing useful while a search is active, so
              // the click is suppressed and the chevron hides.
              clickable={!searchActive}
              selection={selection}
              onPick={onPick}
            />
          ))}
        </div>
      </div>
    </div>
  );
}

function CategorySection({
  group,
  expanded,
  clickable,
  selection,
  onPick,
}: {
  group: CategoryGroup;
  expanded: boolean;
  clickable: boolean;
  selection: ReturnType<typeof useSnapshot<typeof functionFormState>>['byTabId'][string]['selection'] | null;
  onPick: (fn: ScalarFunctionDto) => void;
}) {
  return (
    <section className="flex flex-col">
      <button
        type="button"
        onClick={clickable ? () => toggleCategoryExpanded(group.category) : undefined}
        disabled={!clickable}
        className={cn(
          'text-muted-foreground hover:text-foreground flex items-center gap-1.5 rounded-md px-2 py-1.5 text-left text-xs font-medium uppercase tracking-wide transition-colors',
          'outline-none',
          clickable && 'hover:bg-muted cursor-pointer',
        )}
      >
        {clickable ? (
          expanded ? (
            <ChevronDown className="size-3.5" />
          ) : (
            <ChevronRight className="size-3.5" />
          )
        ) : (
          <ChevronDown className="text-muted-foreground/60 size-3.5" />
        )}
        <span className="flex-1 truncate">{group.category}</span>
        <span className="text-muted-foreground/80 font-mono text-[10px]">
          {group.functions.length}
        </span>
      </button>
      {expanded && (
        <ul className="flex flex-col pl-2">
          {group.functions.map((fn) => {
            const isSelected =
              selection !== null
              && selection.schema === fn.schema
              && selection.name === fn.name;
            return (
              <li key={`${fn.schema}.${fn.name}`}>
                <button
                  type="button"
                  onClick={() => onPick(fn)}
                  className={cn(
                    'group flex w-full flex-col gap-0.5 rounded-md px-2 py-1.5 text-left text-sm transition-colors',
                    'cursor-pointer outline-none',
                    isSelected
                      ? 'bg-primary/15 text-foreground'
                      : 'text-foreground hover:bg-muted',
                  )}
                >
                  <span className="font-mono text-[0.8125rem]">{fn.name}</span>
                  {fn.description && (
                    <span
                      className={cn(
                        'text-muted-foreground line-clamp-2 text-xs',
                        isSelected && 'text-foreground/80',
                      )}
                    >
                      {fn.description}
                    </span>
                  )}
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}

// ───────────────────────── Form body ─────────────────────────

function FormBody({
  tabId,
  fn,
  variantIndex,
  variant,
  textValues,
  fileNames,
  fieldErrors,
}: {
  tabId: string;
  fn: ScalarFunctionDto;
  variantIndex: number;
  variant: ScalarFunctionSignatureDto | null;
  textValues: Record<string, string>;
  fileNames: Record<string, string>;
  fieldErrors: Record<string, string>;
}) {
  const { t } = useTranslation('query');
  const variants = fn.signatures ?? [];

  return (
    <div className="flex min-h-0 flex-1 flex-col overflow-hidden">
      {/* Function header — sticky so the Run button stays reachable
          while the form scrolls. */}
      <div className="border-border bg-background flex items-start justify-between gap-3 border-b px-4 py-3">
        <div className="min-w-0 flex-1">
          <div className="flex items-center gap-2">
            <FunctionSquare className="text-muted-foreground size-4" />
            <span className="truncate font-mono text-sm font-medium">
              {fn.schema}.{fn.name}
            </span>
          </div>
          {fn.description && (
            <p className="text-muted-foreground mt-1 text-xs">
              {fn.description}
            </p>
          )}
        </div>
        <RunButton tabId={tabId} disabled={variant === null || !isFormableVariant(variant)} />
      </div>

      <div className="flex min-h-0 flex-1 flex-col overflow-y-auto">
        {/* Overload picker — only when there's more than one variant. */}
        {variants.length > 1 && (
          <section className="border-border border-b p-4">
            <h3 className="text-muted-foreground mb-2 text-xs font-medium uppercase tracking-wide">
              {t('fnOverloadsHeader')}
            </h3>
            <div className="flex flex-col gap-1">
              {variants.map((v, idx) => (
                <OverloadRow
                  key={idx}
                  tabId={tabId}
                  fn={fn}
                  variant={v}
                  index={idx}
                  selected={idx === variantIndex}
                />
              ))}
            </div>
          </section>
        )}

        {/* Form for the selected variant. */}
        <section className="flex-1 p-4">
          <h3 className="text-muted-foreground mb-2 text-xs font-medium uppercase tracking-wide">
            {t('fnFormHeader')}
          </h3>
          {variant === null ? (
            <p className="text-muted-foreground text-sm">
              {t('fnOverloadUnsupported')}
            </p>
          ) : !isFormableVariant(variant) ? (
            <p className="text-muted-foreground text-sm">
              {t('fnOverloadUnsupported')}
            </p>
          ) : (
            <div className="flex flex-col gap-3">
              {(variant.parameters ?? []).map((p) => (
                <ParameterField
                  key={p.name}
                  tabId={tabId}
                  param={p}
                  text={textValues[p.name ?? ''] ?? ''}
                  fileName={fileNames[p.name ?? ''] ?? null}
                  error={fieldErrors[p.name ?? ''] ?? null}
                />
              ))}
            </div>
          )}
        </section>

        {/* Script preview — always rendered when a function is picked
            so the user sees the script shape even before any fields are
            filled. */}
        <section className="border-border border-t p-4">
          <h3 className="text-muted-foreground mb-2 text-xs font-medium uppercase tracking-wide">
            {t('fnPreviewHeader')}
          </h3>
          <ScriptPreview
            fn={fn}
            variant={variant}
            textValues={textValues}
            fileNames={fileNames}
          />
        </section>
      </div>
    </div>
  );
}

function OverloadRow({
  tabId,
  fn,
  variant,
  index,
  selected,
}: {
  tabId: string;
  fn: ScalarFunctionDto;
  variant: ScalarFunctionSignatureDto;
  index: number;
  selected: boolean;
}) {
  const { t } = useTranslation('query');
  const formable = isFormableVariant(variant);
  const label = describeSignature(fn, variant);

  function onPick() {
    if (!formable) return;
    setFunctionFormSelection(tabId, {
      schema: fn.schema ?? 'system',
      name: fn.name ?? '',
      variantIndex: index,
    });
  }

  return (
    <button
      type="button"
      onClick={onPick}
      disabled={!formable}
      title={formable ? undefined : t('fnOverloadUnsupported')}
      className={cn(
        'group flex flex-col gap-0.5 rounded-md px-2 py-1.5 text-left transition-colors',
        'outline-none',
        formable
          ? selected
            ? 'bg-primary/15 text-foreground cursor-pointer'
            : 'text-foreground hover:bg-muted cursor-pointer'
          : 'text-muted-foreground/60 cursor-not-allowed',
      )}
    >
      <span className="font-mono text-[0.8125rem]">{label}</span>
      {!formable && (
        <span className="text-muted-foreground/80 text-xs">
          {t('fnOverloadUnsupported')}
        </span>
      )}
    </button>
  );
}

function describeSignature(
  fn: ScalarFunctionDto,
  variant: ScalarFunctionSignatureDto,
): string {
  const params = (variant.parameters ?? [])
    .map((p) => `${p.name}: ${p.kindLabel}${p.isOptional ? '?' : ''}`)
    .join(', ');
  const ret = variant.returnType?.description ?? 'Unknown';
  return `${fn.name}(${params}) → ${ret}`;
}

function ParameterField({
  tabId,
  param,
  text,
  fileName,
  error,
}: {
  tabId: string;
  param: ScalarFunctionParameterDto;
  text: string;
  fileName: string | null;
  error: string | null;
}) {
  const { t } = useTranslation('query');
  const name = param.name ?? '';
  const binary = isBinaryParameter(param);

  return (
    <div className="flex flex-col gap-1">
      <label
        htmlFor={`fn-${tabId}-${name}`}
        className="text-foreground flex items-baseline gap-2 text-sm"
      >
        <span className="font-mono">{name}</span>
        <span className="text-muted-foreground text-xs">{param.kindLabel}</span>
        {param.isOptional && (
          <span className="text-muted-foreground text-xs">
            {t('fnFieldOptional')}
          </span>
        )}
      </label>
      {binary ? (
        <BinaryField tabId={tabId} paramName={name} fileName={fileName} />
      ) : (
        <InlineField
          id={`fn-${tabId}-${name}`}
          tabId={tabId}
          paramName={name}
          text={text}
          param={param}
        />
      )}
      {error && (
        <p className="text-destructive text-xs">{error}</p>
      )}
    </div>
  );
}

function RunButton({ tabId, disabled }: { tabId: string; disabled: boolean }) {
  const { t } = useTranslation('query');
  const { byTabId } = useSnapshot(executionsState);
  const exec = byTabId[tabId];
  const streaming = exec?.status === 'streaming';

  function onClick() {
    if (streaming) {
      cancelTab(tabId);
      return;
    }
    void runFunctionTab(tabId);
  }

  return (
    <button
      type="button"
      onClick={onClick}
      disabled={!streaming && disabled}
      title={streaming ? t('cancel') : t('run')}
      aria-label={streaming ? t('cancel') : t('run')}
      className={cn(
        'inline-flex shrink-0 items-center gap-1.5 rounded-md px-3 py-1.5 text-sm font-medium transition-colors',
        'outline-none focus:ring-2 focus:ring-ring',
        streaming
          ? 'bg-destructive/15 text-destructive hover:bg-destructive/25 cursor-pointer'
          : disabled
            ? 'bg-muted text-muted-foreground/60 cursor-not-allowed'
            : 'bg-primary text-primary-foreground hover:bg-primary/90 cursor-pointer',
      )}
    >
      {streaming ? (
        <Square className="size-3.5" />
      ) : (
        <Play className="size-3.5" />
      )}
      {streaming ? t('cancel') : t('run')}
    </button>
  );
}

function InlineField({
  id,
  tabId,
  paramName,
  text,
  param,
}: {
  id: string;
  tabId: string;
  paramName: string;
  text: string;
  param: ScalarFunctionParameterDto;
}) {
  const acceptedKinds = param.acceptedKinds ?? [];
  // String slots get a textarea — they're usually free-form and longer
  // than a number. Everything else stays a single-line input.
  const isStringy =
    acceptedKinds.length === 1 && acceptedKinds[0] === 'String';
  const isNumeric =
    acceptedKinds.length > 0 &&
    acceptedKinds.every((k) =>
      k.startsWith('Int') || k.startsWith('UInt') || k.startsWith('Float'),
    );
  const isBoolean =
    acceptedKinds.length === 1 && acceptedKinds[0] === 'Boolean';

  if (isBoolean) {
    return (
      <select
        id={id}
        value={text}
        onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
        className={cn(
          'bg-input/30 rounded-md px-2 py-1.5 text-sm',
          'outline-none focus:ring-2 focus:ring-ring',
        )}
      >
        <option value="">…</option>
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }

  if (isStringy) {
    return (
      <textarea
        id={id}
        value={text}
        rows={2}
        onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
        className={cn(
          'bg-input/30 resize-y rounded-md px-2 py-1.5 font-mono text-sm',
          'outline-none focus:ring-2 focus:ring-ring',
        )}
      />
    );
  }

  return (
    <input
      id={id}
      type="text"
      inputMode={isNumeric ? 'numeric' : undefined}
      value={text}
      onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
      className={cn(
        'bg-input/30 rounded-md px-2 py-1.5 font-mono text-sm',
        'outline-none focus:ring-2 focus:ring-ring',
      )}
    />
  );
}

function BinaryField({
  tabId,
  paramName,
  fileName,
}: {
  tabId: string;
  paramName: string;
  fileName: string | null;
}) {
  const { t } = useTranslation('query');
  const inputRef = useRef<HTMLInputElement | null>(null);

  function pick() {
    inputRef.current?.click();
  }

  function onChange(e: React.ChangeEvent<HTMLInputElement>) {
    const f = e.target.files?.[0] ?? null;
    setFunctionFormFile(tabId, paramName, f);
    // Reset the input so picking the same file twice in a row fires
    // another change event.
    e.target.value = '';
  }

  return (
    <div className="flex items-center gap-2">
      <button
        type="button"
        onClick={pick}
        className={cn(
          'bg-input/30 text-foreground inline-flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-sm',
          'hover:bg-muted cursor-pointer outline-none transition-colors focus:ring-2 focus:ring-ring',
        )}
      >
        <Paperclip className="size-3.5" />
        {fileName ? t('fnFieldReplaceFile') : t('fnFieldChooseFile')}
      </button>
      {fileName && (
        <>
          <span className="text-muted-foreground truncate text-xs">
            {fileName}
          </span>
          <button
            type="button"
            onClick={() => setFunctionFormFile(tabId, paramName, null)}
            className="text-muted-foreground hover:text-foreground cursor-pointer"
            aria-label={t('fnFieldClearFile')}
            title={t('fnFieldClearFile')}
          >
            <X className="size-3.5" />
          </button>
        </>
      )}
      <input
        ref={inputRef}
        type="file"
        onChange={onChange}
        className="hidden"
      />
    </div>
  );
}

// ───────────────────────── Script preview ─────────────────────────

function ScriptPreview({
  fn,
  variant,
  textValues,
  fileNames,
}: {
  fn: ScalarFunctionDto;
  variant: ScalarFunctionSignatureDto | null;
  textValues: Record<string, string>;
  fileNames: Record<string, string>;
}) {
  const { t } = useTranslation('query');

  if (variant === null || !isFormableVariant(variant)) {
    return (
      <p className="text-muted-foreground text-sm">{t('fnPreviewEmpty')}</p>
    );
  }

  const result = synthesizeFunctionScript(fn, variant, {
    search: '',
    selection: null,
    textValues,
    fileNames,
    fieldErrors: {},
  });

  return (
    <pre
      className={cn(
        'bg-editor border-border overflow-x-auto rounded-md border p-3 font-mono text-xs leading-relaxed',
        'text-foreground',
      )}
    >
      {result.script}
    </pre>
  );
}
