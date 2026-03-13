import { useEffect, useMemo, useRef, useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useSnapshot } from 'valtio';
import {
  ChevronDown,
  ChevronRight,
  FunctionSquare,
  Paperclip,
  Play,
  Plus,
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
  addVariadicSlot,
  ensureFunctionForm,
  functionFormState,
  removeVariadicSlot,
  runFunctionTab,
  setFunctionFormFile,
  setFunctionFormKindOverride,
  setFunctionFormSearch,
  setFunctionFormSelection,
  setFunctionFormText,
  setFunctionFormVariadicKindOverride,
  variadicSlotCount,
} from '@/state/functionForm';
import { cancelTab, executionsState } from '@/state/execution';
import {
  declaredKindFor,
  declaredKindForVariadicSlot,
  isBinaryParameter,
  isBinaryVariadic,
  isFormableVariant,
  synthesizeFunctionScript,
} from '@/lib/synthesizeFunctionScript';
import type {
  ScalarFunctionVariadicDto,
} from '@/api/generated/openapi-client';
import {
  describeCheck,
  isInCheck,
  numericBoundsFor,
} from '@/lib/parameterCheck';
import { Badge } from '@/components/ui/badge';
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
            kindOverrides={formSnap.kindOverrides}
            variadicCounts={formSnap.variadicCounts}
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
  kindOverrides,
  variadicCounts,
}: {
  tabId: string;
  fn: ScalarFunctionDto;
  variantIndex: number;
  variant: ScalarFunctionSignatureDto | null;
  textValues: Record<string, string>;
  fileNames: Record<string, string>;
  fieldErrors: Record<string, string>;
  kindOverrides: Record<string, string>;
  variadicCounts: Record<string, number>;
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
                  kindOverride={kindOverrides[p.name ?? '']}
                />
              ))}
              {variant.variadic && (
                <VariadicField
                  tabId={tabId}
                  variadic={variant.variadic}
                  textValues={textValues}
                  fileNames={fileNames}
                  fieldErrors={fieldErrors}
                  kindOverrides={kindOverrides}
                  variadicCounts={variadicCounts}
                />
              )}
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
            kindOverrides={kindOverrides}
            variadicCounts={variadicCounts}
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
  kindOverride,
}: {
  tabId: string;
  param: ScalarFunctionParameterDto;
  text: string;
  fileName: string | null;
  error: string | null;
  kindOverride: string | undefined;
}) {
  const { t } = useTranslation('query');
  const name = param.name ?? '';
  const binary = isBinaryParameter(param);
  // Constraint hint — compact one-liner next to the input ("0 – 1",
  // "≥ 0", "one of 416, 640", …). Renders only when the parameter
  // declared a Check.
  const constraintHint = param.check ? describeCheck(param.check) : null;
  // The kind currently in use: override wins, else value-inferred, else
  // static fallback. Highlighting the matching pill keeps the UI honest
  // about which kind the synthesized DECLARE will actually use.
  const activeKind = binary
    ? null
    : declaredKindFor(param, text, kindOverride);

  return (
    <div className="flex flex-col gap-1">
      <label
        htmlFor={`fn-${tabId}-${name}`}
        className="text-foreground flex flex-wrap items-center gap-x-2 gap-y-1 text-sm"
      >
        <span className="font-mono">{name}</span>
        <KindChips
          kinds={param.acceptedKinds ?? []}
          acceptsAny={param.acceptsAnyKind === true}
          fallbackLabel={param.kindLabel ?? ''}
          activeKind={activeKind}
          onPick={
            binary
              ? undefined
              : (kind) => setFunctionFormKindOverride(tabId, name, kind)
          }
        />
        {param.isOptional && (
          <span className="text-muted-foreground text-xs">
            {t('fnFieldOptional')}
          </span>
        )}
        {constraintHint && (
          <span
            className="text-muted-foreground/80 font-mono text-xs"
            title={constraintHint}
          >
            {constraintHint}
          </span>
        )}
      </label>
      {binary ? (
        <BinaryField tabId={tabId} paramName={name} fileName={fileName} />
      ) : (
        <div className="flex items-center gap-2">
          <div className="min-w-0 flex-1">
            <InlineField
              id={`fn-${tabId}-${name}`}
              tabId={tabId}
              paramName={name}
              text={text}
              param={param}
            />
          </div>
          {param.unit && (
            <span className="text-muted-foreground shrink-0 text-xs">
              {param.unit}
            </span>
          )}
        </div>
      )}
      {param.description && (
        <p className="text-muted-foreground text-xs">{param.description}</p>
      )}
      {param.defaultExpression && param.isOptional && !text && !binary && (
        <p className="text-muted-foreground/80 font-mono text-[11px]">
          defaults to <span className="italic">{param.defaultExpression}</span>
        </p>
      )}
      {error && <p className="text-destructive text-xs">{error}</p>}
    </div>
  );
}

/**
 * Renders the accepted-kinds set as a row of compact pills. Single-kind
 * slots show one pill; polymorphic slots (`Any`) show a single "Any"
 * pill rather than a 15-row dump. Multi-kind slots show one pill per
 * kind, wrapped onto subsequent label rows when needed.
 *
 * When `onPick` is provided, pills become interactive: clicking a pill
 * pins the parameter to that kind (overriding value-driven inference).
 * The currently-active kind (`activeKind`) renders highlighted with a
 * default cursor — it's the current state, no action available; the
 * other pills render with a pointer cursor on hover and dispatch
 * `onPick` on click.
 *
 * When `onPick` is omitted (binary slots), all pills are static labels.
 */
function KindChips({
  kinds,
  acceptsAny,
  fallbackLabel,
  activeKind,
  onPick,
}: {
  kinds: readonly string[];
  acceptsAny: boolean;
  fallbackLabel: string;
  activeKind: string | null;
  onPick: ((kind: string) => void) | undefined;
}) {
  if (acceptsAny) {
    return (
      <Badge variant="muted" className="font-mono text-[10px] leading-none">
        Any
      </Badge>
    );
  }
  if (kinds.length === 0) {
    // No structured kind list AND not Any-kind. Fall back to the textual
    // label so we still show something rather than a silent gap; in
    // practice this branch is unreachable because `isFormableVariant`
    // already filtered out empty-matcher parameters.
    if (!fallbackLabel) return null;
    return (
      <span className="text-muted-foreground text-xs">{fallbackLabel}</span>
    );
  }
  return (
    <span className="inline-flex flex-wrap items-center gap-1">
      {kinds.map((k) => (
        <KindChip
          key={k}
          kind={k}
          selected={k === activeKind}
          onPick={onPick}
        />
      ))}
    </span>
  );
}

function KindChip({
  kind,
  selected,
  onPick,
}: {
  kind: string;
  selected: boolean;
  onPick: ((kind: string) => void) | undefined;
}) {
  const interactive = !!onPick && !selected;
  return (
    <Badge
      variant={selected ? 'default' : 'muted'}
      onClick={interactive ? () => onPick!(kind) : undefined}
      className={cn(
        'font-mono text-[10px] leading-none',
        interactive && 'cursor-pointer hover:brightness-110',
        selected && 'cursor-default',
      )}
    >
      {kind}
    </Badge>
  );
}

/**
 * Trailing variadic slot — renders as a "label + (shared kind chip row
 * when same-kind-required) + N value rows + Add button" group. Each
 * row is the same input control a fixed parameter would render, but
 * keyed by `${variadicName}_${index}` and accompanied by a remove
 * button (suppressed when removing would drop count below 1 — there's
 * always at least one row).
 *
 * For `requireSameKindAcrossArgs` variadics, a single kind-chip row
 * sits above the slots; clicking a chip broadcasts the override to
 * every occurrence so the synthesized DECLAREs stay in lock-step.
 * Non-same-kind variants get per-slot chips inside each row instead.
 */
function VariadicField({
  tabId,
  variadic,
  textValues,
  fileNames,
  fieldErrors,
  kindOverrides,
  variadicCounts,
}: {
  tabId: string;
  variadic: ScalarFunctionVariadicDto;
  textValues: Record<string, string>;
  fileNames: Record<string, string>;
  fieldErrors: Record<string, string>;
  kindOverrides: Record<string, string>;
  variadicCounts: Record<string, number>;
}) {
  const { t } = useTranslation('query');
  const name = variadic.name ?? '';
  const minOccurrences = variadic.minOccurrences ?? 0;
  const count = variadicSlotCount(
    { textValues, fileNames, fieldErrors, kindOverrides, variadicCounts,
      search: '', selection: null },
    name,
    minOccurrences,
  );
  const sameKind = variadic.requireSameKindAcrossArgs === true;
  const binary = isBinaryVariadic(variadic);

  // The shared kind chip row (same-kind case) renders once and
  // broadcasts clicks across every slot. The "selected" chip mirrors
  // slot 0's resolved kind — slots 1..N either follow via broadcast or
  // already share a kind via inference, so slot 0 is the authoritative
  // anchor.
  const slot0Text = textValues[`${name}_0`];
  const slot0Override = kindOverrides[`${name}_0`];
  const sharedActiveKind =
    !binary && sameKind
      ? declaredKindForVariadicSlot(variadic, slot0Text, slot0Override)
      : null;

  // Auto-focus the freshly-added slot. The state mutation lands on the
  // proxy synchronously in `addVariadicSlot`, but the new input doesn't
  // mount until the next render — so we record the index here and look
  // it up by DOM id from a passive effect, which fires after layout.
  // Cleared once focus has been applied so a later re-render (e.g. text
  // edit on the new slot) doesn't snap focus back.
  const [pendingFocusIndex, setPendingFocusIndex] = useState<number | null>(null);
  useEffect(() => {
    if (pendingFocusIndex === null) return;
    const el = document.getElementById(
      `fn-${tabId}-${name}_${pendingFocusIndex}`,
    );
    if (el && el instanceof HTMLElement) {
      el.focus();
    }
    setPendingFocusIndex(null);
  }, [pendingFocusIndex, tabId, name]);

  function onAdd() {
    // `count` here is the pre-add count; the new slot's index equals it.
    addVariadicSlot(tabId, name, minOccurrences);
    setPendingFocusIndex(count);
  }
  function onRemove(i: number) {
    removeVariadicSlot(tabId, name, minOccurrences, i);
  }
  function onSharedKindPick(kind: string) {
    // Read the current count fresh; this handler doesn't need to be
    // closed over because the broadcaster walks 0..count itself.
    setFunctionFormVariadicKindOverride(tabId, name, count, kind);
  }

  return (
    <div className="flex flex-col gap-2">
      <div className="text-foreground flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">
        <span className="font-mono">{name}</span>
        {sameKind ? (
          <KindChips
            kinds={variadic.acceptedKinds ?? []}
            acceptsAny={variadic.acceptsAnyKind === true}
            fallbackLabel={variadic.kindLabel ?? ''}
            activeKind={sharedActiveKind}
            onPick={binary ? undefined : onSharedKindPick}
          />
        ) : (
          // Non-same-kind: show the matcher label statically. Per-slot
          // chips live inside each row below.
          <span className="text-muted-foreground text-xs">
            {variadic.kindLabel}
          </span>
        )}
        {minOccurrences > 0 && (
          <span className="text-muted-foreground/80 font-mono text-xs">
            {t('fnVariadicMin', { count: minOccurrences })}
          </span>
        )}
      </div>
      {Array.from({ length: count }, (_, i) => {
        const slotKey = `${name}_${i}`;
        return (
          <VariadicSlot
            key={slotKey}
            tabId={tabId}
            variadic={variadic}
            slotKey={slotKey}
            slotIndex={i}
            text={textValues[slotKey] ?? ''}
            fileName={fileNames[slotKey] ?? null}
            error={fieldErrors[slotKey] ?? null}
            kindOverride={kindOverrides[slotKey]}
            showKindChips={!sameKind && !binary}
            canRemove={count > 1}
            onRemove={() => onRemove(i)}
          />
        );
      })}
      <button
        type="button"
        onClick={onAdd}
        className={cn(
          'text-muted-foreground hover:text-foreground inline-flex w-fit cursor-pointer items-center gap-1 rounded-md px-2 py-1 text-xs',
          'hover:bg-muted outline-none focus:ring-2 focus:ring-ring',
        )}
      >
        <Plus className="size-3.5" />
        {t('fnVariadicAdd')}
      </button>
    </div>
  );
}

function VariadicSlot({
  tabId,
  variadic,
  slotKey,
  slotIndex,
  text,
  fileName,
  error,
  kindOverride,
  showKindChips,
  canRemove,
  onRemove,
}: {
  tabId: string;
  variadic: ScalarFunctionVariadicDto;
  slotKey: string;
  slotIndex: number;
  text: string;
  fileName: string | null;
  error: string | null;
  kindOverride: string | undefined;
  showKindChips: boolean;
  canRemove: boolean;
  onRemove: () => void;
}) {
  const { t } = useTranslation('query');
  const binary = isBinaryVariadic(variadic);
  const activeKind = binary
    ? null
    : declaredKindForVariadicSlot(variadic, text, kindOverride);

  // VariadicSlot reuses the same input controls as a fixed parameter
  // but doesn't have a real `ScalarFunctionParameterDto` to feed
  // `InlineField` / `BinaryField`. We pass a synthetic one built from
  // the variadic's matcher fields; the inputs only read kind metadata
  // off it so the synthesised shape is enough.
  const syntheticParam = {
    name: slotKey,
    kindLabel: variadic.kindLabel,
    acceptedKinds: variadic.acceptedKinds,
    acceptsAnyKind: variadic.acceptsAnyKind,
    isOptional: false,
    arrayMatch: variadic.arrayMatch,
    // Variadics don't carry per-slot checks / units / descriptions;
    // those would live on each occurrence's declaration, not on the
    // variadic. Leave them unset.
  };

  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-start gap-2">
        <span className="text-muted-foreground/80 mt-1.5 w-6 shrink-0 text-right font-mono text-[10px]">
          {slotIndex}
        </span>
        <div className="min-w-0 flex-1 flex-col">
          {binary ? (
            <BinaryField tabId={tabId} paramName={slotKey} fileName={fileName} />
          ) : (
            <InlineField
              id={`fn-${tabId}-${slotKey}`}
              tabId={tabId}
              paramName={slotKey}
              text={text}
              param={syntheticParam}
            />
          )}
        </div>
        {showKindChips && (
          <div className="shrink-0">
            <KindChips
              kinds={variadic.acceptedKinds ?? []}
              acceptsAny={variadic.acceptsAnyKind === true}
              fallbackLabel={variadic.kindLabel ?? ''}
              activeKind={activeKind}
              onPick={
                binary
                  ? undefined
                  : (kind) => setFunctionFormKindOverride(tabId, slotKey, kind)
              }
            />
          </div>
        )}
        <button
          type="button"
          onClick={onRemove}
          disabled={!canRemove}
          aria-label={t('fnVariadicRemove')}
          title={t('fnVariadicRemove')}
          className={cn(
            'text-muted-foreground hover:text-foreground rounded-xs p-1 transition-colors',
            'outline-none focus:ring-2 focus:ring-ring',
            canRemove ? 'cursor-pointer hover:bg-muted' : 'cursor-not-allowed opacity-30',
          )}
        >
          <X className="size-3.5" />
        </button>
      </div>
      {error && <p className="text-destructive pl-8 text-xs">{error}</p>}
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

  const inputClass = cn(
    'bg-input/30 w-full rounded-md px-2 py-1.5 font-mono text-sm',
    'outline-none focus:ring-2 focus:ring-ring',
  );
  const selectClass = cn(
    'bg-input/30 w-full rounded-md px-2 py-1.5 text-sm',
    'outline-none focus:ring-2 focus:ring-ring',
  );

  // InCheck wins over the kind-based dispatch — even a numeric slot with
  // an enumerated allowed set wants a dropdown. The check's values are
  // strings; coercion back to the declared numeric kind happens at
  // submit time in `buildFunctionRequest`.
  if (param.check && isInCheck(param.check)) {
    const values = param.check.values ?? [];
    return (
      <select
        id={id}
        value={text}
        onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
        className={selectClass}
      >
        <option value="">…</option>
        {values.map((v) => (
          <option key={v} value={v}>
            {v}
          </option>
        ))}
      </select>
    );
  }

  if (isBoolean) {
    return (
      <select
        id={id}
        value={text}
        onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
        className={selectClass}
      >
        <option value="">…</option>
        <option value="true">true</option>
        <option value="false">false</option>
      </select>
    );
  }

  if (isStringy) {
    // RegexCheck → wire the pattern attr so the browser surfaces an
    // invalid hint on submit even before our validator runs.
    const pattern =
      param.check && param.check.kind === 'regex'
        ? (param.check as { pattern?: string }).pattern
        : undefined;
    return (
      <textarea
        id={id}
        value={text}
        rows={2}
        onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
        // textareas don't support `pattern`, so we tuck the regex on a
        // data-attr and rely on `validateCheck` for definitive checking.
        data-pattern={pattern}
        className={cn(
          'bg-input/30 w-full resize-y rounded-md px-2 py-1.5 font-mono text-sm',
          'outline-none focus:ring-2 focus:ring-ring',
        )}
      />
    );
  }

  // Numeric or polymorphic fallback. Pull min/max from any range check
  // and the explicit `step` declared by the parameter metadata.
  const bounds = param.check ? numericBoundsFor(param.check) : {};
  const stepAttr = param.step !== undefined ? String(param.step) : undefined;
  return (
    <input
      id={id}
      type={isNumeric ? 'number' : 'text'}
      inputMode={isNumeric ? 'decimal' : undefined}
      step={isNumeric ? stepAttr ?? 'any' : undefined}
      min={isNumeric ? bounds.min ?? undefined : undefined}
      max={isNumeric ? bounds.max ?? undefined : undefined}
      value={text}
      onChange={(e) => setFunctionFormText(tabId, paramName, e.target.value)}
      className={inputClass}
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
  kindOverrides,
  variadicCounts,
}: {
  fn: ScalarFunctionDto;
  variant: ScalarFunctionSignatureDto | null;
  textValues: Record<string, string>;
  fileNames: Record<string, string>;
  kindOverrides: Record<string, string>;
  variadicCounts: Record<string, number>;
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
    kindOverrides,
    variadicCounts,
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
