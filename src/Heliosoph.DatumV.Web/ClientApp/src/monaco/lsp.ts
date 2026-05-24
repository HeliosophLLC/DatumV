import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import { api } from '@/api';
import type {
  CompletionItemKind as ServerCompletionItemKind,
  DiagnosticSeverity as ServerDiagnosticSeverity,
} from '@/api/generated/openapi-client';
import { RETOKENIZE_DUMMY_LANGUAGE_ID } from './setup';
import { DOCS_TAB_ID, MODELS_TAB_ID, selectTab } from '@/state/tabs';
import { setSelectedId } from '@/state/models';

// SQL language wiring against the Heliosoph.DatumV LSP REST endpoints. Each
// Monaco provider translates a (model, position) call into a server
// request, then maps the response back into Monaco's shapes. Providers
// are language-global (registered once for 'sql'), so this runs at app
// boot via `initLsp()` — NOT per-tab.
//
// Diagnostics don't have a registerable-provider analogue; we listen for
// model creation and per-model content changes, debounce, then call
// `setModelMarkers` directly.

const MARKERS_OWNER = 'datumv';
const DIAGNOSTICS_DEBOUNCE_MS = 250;
const GRAMMAR_URL = '/api/lang/grammar';

let initialized = false;

export async function initLsp(): Promise<void> {
  if (initialized) return;
  initialized = true;

  // Grammar bootstrap. The endpoint returns a Monarch language definition
  // shaped exactly like what `setMonarchTokensProvider` accepts. The
  // generated client types it as a FileResponse (NSwag can't statically
  // see the JSON shape from an IActionResult return); plain fetch is
  // simpler than re-typing it. Best-effort: failures fall back to
  // Monaco's built-in `sql` tokenizer (registered by sql.contribution).
  try {
    const res = await fetch(GRAMMAR_URL, {
      headers: { Accept: 'application/json' },
      credentials: 'include',
    });
    if (res.ok) {
      const grammar = (await res.json()) as monaco.languages.IMonarchLanguage;
      monaco.languages.setMonarchTokensProvider('sql', grammar);
      // Force-retokenise every existing SQL model AND any model created
      // during the brief window where the contribution's lazy loader
      // might still resolve and re-override our provider. Two complementary
      // techniques:
      //
      //   1. A double `setMonarchTokensProvider` with a delay — re-registers
      //      our grammar on the next macrotask so it runs AFTER any
      //      in-flight async override from sql.contribution's lazy loader
      //      (which is a local dynamic import and resolves in <10ms; 50ms
      //      is a safe upper bound).
      //   2. Per-model retokenisation via the dummy-language flip + a
      //      private `resetTokenization()` call (when available). The flip
      //      alone has been observed to not retokenise in some Monaco
      //      versions; `resetTokenization` is private but stable.
      const grammarSnapshot = grammar;
      const forceRetokenize = (): void => {
        monaco.languages.setMonarchTokensProvider('sql', grammarSnapshot);
        for (const model of monaco.editor.getModels()) {
          if (model.getLanguageId() === 'sql') {
            // Private API path: directly invalidate cached tokens.
            const m = model as unknown as { resetTokenization?: () => void };
            if (typeof m.resetTokenization === 'function') {
              m.resetTokenization();
              continue;
            }
            // Fallback: language flip through the registered dummy.
            monaco.editor.setModelLanguage(model, RETOKENIZE_DUMMY_LANGUAGE_ID);
            monaco.editor.setModelLanguage(model, 'sql');
          }
        }
      };
      forceRetokenize();
      // Second pass past any pending sql.contribution async load.
      setTimeout(forceRetokenize, 50);
      // Forensic log so we can tell at a glance whether the custom grammar
      // landed.
      console.info('[lsp] custom SQL grammar registered');
    } else {
      console.warn(`[lsp] grammar fetch failed: ${res.status} ${res.statusText}`);
    }
  } catch (err) {
    console.warn('[lsp] grammar fetch errored, falling back to built-in tokenizer', err);
  }

  registerCompletionProvider();
  registerHoverProvider();
  registerHoverCommands();
  registerSignatureHelpProvider();
  registerDiagnosticsPump();
}

// Commands invoked by `command:` links inside trusted hover markdown
// emitted by the language server. Monaco parses the URI's query string
// as JSON and passes the result positionally; both commands here take a
// single string argument (the catalog entry id / documentation section
// key respectively). Registered once at boot — Monaco's command registry
// is global, not per-editor.
function registerHoverCommands(): void {
  monaco.editor.registerCommand('datum.openModelInTab', (_accessor, id: string) => {
    if (typeof id !== 'string' || id.length === 0) return;
    selectTab(MODELS_TAB_ID);
    setSelectedId(id);
  });

  // The hover "See more" links the LS appends to function and keyword
  // hovers. We don't currently jump to the section inside the Docs tab,
  // so the v1 handler just focuses the tab; future work can route the
  // sectionKey to a hash + scroll. Registering even the stub keeps the
  // trusted-markdown link from rendering as a broken click target.
  monaco.editor.registerCommand('datumv.openDoc', (_accessor, _sectionKey: string) => {
    selectTab(DOCS_TAB_ID);
  });
}

// ────────── Completion ──────────

function registerCompletionProvider(): void {
  monaco.languages.registerCompletionItemProvider('sql', {
    // The base set Monaco invokes on typed identifiers covers the rest;
    // trigger chars are for cases the automatic invocation doesn't catch:
    //   ' ' / '.' — after a separator / qualifier (existing behaviour)
    //   "'"       — opening a string literal so struct-field enum values
    //               surface immediately (e.g. ChatMessage.role) without
    //               the user needing Ctrl+Space
    //   '{' / ',' — inside / between struct-literal field slots so field
    //               names surface as soon as the user starts a record
    triggerCharacters: [' ', '.', "'", '{', ','],

    async provideCompletionItems(model, position) {
      const sql = model.getValue();
      const offset = model.getOffsetAt(position);
      const items = await api.language.complete({ sql, offset });

      // Build a one-position range — Monaco computes the actual replace
      // range from the user's typed prefix.
      const word = model.getWordUntilPosition(position);
      const range = new monaco.Range(
        position.lineNumber,
        word.startColumn,
        position.lineNumber,
        word.endColumn,
      );

      return {
        suggestions: items.map<monaco.languages.CompletionItem>((item) => {
          const insertText = item.insertText ?? item.label ?? '';
          // When the inserted text ends with `(` (function/UDF/procedure
          // completions all do — InsertText = `${name}(`), fire Monaco's
          // built-in parameter-hints action right after the insert.
          // Without this, accepting a function completion swallows the
          // `(` signature-help trigger character (Monaco only fires
          // trigger chars on user keystrokes, not programmatic inserts),
          // and the user has to Ctrl+Space — which surfaces named-arg /
          // variable suggestions, not the signature popup.
          const triggerSignatureHelp = insertText.endsWith('(');
          return {
            // When the server emits a LabelSuffix, render it as Monaco's
            // structured label so the muted suffix shows inline in the row
            // (e.g. "depth_anything_v3_large (not installed)") without the
            // user having to highlight the item. Filter matching still
            // targets the bare name — Monaco indexes on the `label.label`
            // field, not the suffix.
            label: item.labelSuffix
              ? { label: item.label ?? '', detail: ` ${item.labelSuffix}` }
              : item.label ?? '',
            kind: completionKindToMonaco(item.kind),
            insertText,
            detail: item.detail,
            documentation: item.documentation
              ? { value: item.documentation, isTrusted: false }
              : undefined,
            sortText: item.sortOrder !== undefined
              ? item.sortOrder.toString().padStart(6, '0')
              : undefined,
            range,
            command: triggerSignatureHelp
              ? { id: 'editor.action.triggerParameterHints', title: 'Trigger Parameter Hints' }
              : undefined,
          };
        }),
      };
    },
  });
}

function completionKindToMonaco(
  kind: ServerCompletionItemKind | undefined,
): monaco.languages.CompletionItemKind {
  // Map our string union to Monaco's enum. Default keyword for unknowns
  // — keeps the suggestion list working even if the server adds new
  // kinds before the client knows about them.
  switch (kind) {
    case 'function':
      return monaco.languages.CompletionItemKind.Function;
    case 'column':
      return monaco.languages.CompletionItemKind.Field;
    case 'variable':
      return monaco.languages.CompletionItemKind.Variable;
    case 'table':
      return monaco.languages.CompletionItemKind.Class;
    case 'typeParameter':
      return monaco.languages.CompletionItemKind.TypeParameter;
    case 'enumMember':
      return monaco.languages.CompletionItemKind.EnumMember;
    case 'keyword':
    default:
      return monaco.languages.CompletionItemKind.Keyword;
  }
}

// ────────── Hover ──────────

function registerHoverProvider(): void {
  monaco.languages.registerHoverProvider('sql', {
    async provideHover(model, position) {
      const sql = model.getValue();
      const offset = model.getOffsetAt(position);
      const hover = await api.language.hover({ sql, offset });
      if (!hover) return null;

      return {
        range: toMonacoRange(
          hover.startLine,
          hover.startColumn,
          hover.endLine,
          hover.endColumn,
        ),
        // Trusted so `command:` links emitted by the language server (e.g.
        // the drift hint's "Update to X available" → `datum.openModelInTab`,
        // function/keyword hovers' "See more" → `datumv.openDoc`)
        // render as clickable instead of being stripped. The LS controls
        // the markdown; both commands are registered in registerHoverCommands.
        contents: [{ value: hover.contents ?? '', isTrusted: true }],
      };
    },
  });
}

// ────────── Signature help ──────────

function registerSignatureHelpProvider(): void {
  monaco.languages.registerSignatureHelpProvider('sql', {
    signatureHelpTriggerCharacters: ['(', ','],
    signatureHelpRetriggerCharacters: [','],

    async provideSignatureHelp(model, position) {
      const sql = model.getValue();
      const offset = model.getOffsetAt(position);
      const sig = await api.language.signature({ sql, offset });
      if (!sig || !sig.signatures || sig.signatures.length === 0) return null;

      return {
        value: {
          signatures: sig.signatures.map<monaco.languages.SignatureInformation>((s) => ({
            label: s.label ?? '',
            documentation: s.documentation,
            parameters: (s.parameters ?? []).map<monaco.languages.ParameterInformation>(
              (p) => ({
                label: p.label ?? '',
                documentation: p.documentation,
              }),
            ),
          })),
          activeSignature: sig.activeSignature ?? 0,
          activeParameter: sig.activeParameter ?? 0,
        },
        dispose: () => {
          /* nothing to release */
        },
      };
    },
  });
}

// ────────── Diagnostics ──────────

// Diagnostics aren't a provider — markers are pushed. Hook every SQL
// model: on creation, on content change (debounced), run diagnose and
// set markers. Models created for non-SQL languages are ignored.
//
// One pending timer per model id, keyed on `model.id` (stable for the
// model's lifetime). Cleared on model disposal.
function registerDiagnosticsPump(): void {
  const timers = new Map<string, number>();

  const schedule = (model: monaco.editor.ITextModel): void => {
    if (model.getLanguageId() !== 'sql') return;
    const existing = timers.get(model.id);
    if (existing !== undefined) window.clearTimeout(existing);

    const handle = window.setTimeout(() => {
      timers.delete(model.id);
      // The model may have been disposed mid-debounce. Skip silently
      // rather than throw out of an async path.
      if (model.isDisposed()) return;
      void runDiagnostics(model);
    }, DIAGNOSTICS_DEBOUNCE_MS);
    timers.set(model.id, handle);
  };

  monaco.editor.onDidCreateModel((model) => {
    schedule(model);
    model.onDidChangeContent(() => schedule(model));
    model.onWillDispose(() => {
      const handle = timers.get(model.id);
      if (handle !== undefined) {
        window.clearTimeout(handle);
        timers.delete(model.id);
      }
      monaco.editor.setModelMarkers(model, MARKERS_OWNER, []);
    });
  });

  // Cover models that already existed before this hook was wired (e.g.
  // when initLsp runs after the first tab has mounted).
  for (const model of monaco.editor.getModels()) {
    schedule(model);
    model.onDidChangeContent(() => schedule(model));
  }
}

async function runDiagnostics(model: monaco.editor.ITextModel): Promise<void> {
  const sql = model.getValue();
  try {
    const diagnostics = await api.language.diagnose({ sql });
    if (model.isDisposed()) return;
    monaco.editor.setModelMarkers(
      model,
      MARKERS_OWNER,
      diagnostics.map<monaco.editor.IMarkerData>((d) => ({
        message: d.message ?? '',
        severity: diagnosticSeverityToMonaco(d.severity),
        startLineNumber: (d.startLine ?? 0) + 1,
        startColumn: (d.startColumn ?? 0) + 1,
        endLineNumber: (d.endLine ?? 0) + 1,
        endColumn: (d.endColumn ?? 0) + 1,
      })),
    );
  } catch (err) {
    // Network blip / server down — leave the previous markers in place
    // rather than clearing on every failure. The next successful run
    // overwrites them.
    console.warn('[lsp] diagnose failed', err);
  }
}

function diagnosticSeverityToMonaco(
  severity: ServerDiagnosticSeverity | undefined,
): monaco.MarkerSeverity {
  switch (severity) {
    case 'error':
      return monaco.MarkerSeverity.Error;
    case 'warning':
      return monaco.MarkerSeverity.Warning;
    case 'information':
    default:
      return monaco.MarkerSeverity.Info;
  }
}

// ────────── Helpers ──────────

function toMonacoRange(
  startLine: number | undefined,
  startColumn: number | undefined,
  endLine: number | undefined,
  endColumn: number | undefined,
): monaco.IRange {
  // Server emits 0-based positions; Monaco is 1-based. The `+ 1`
  // conversion happens here at the boundary so the rest of the file
  // can think in server units.
  return {
    startLineNumber: (startLine ?? 0) + 1,
    startColumn: (startColumn ?? 0) + 1,
    endLineNumber: (endLine ?? 0) + 1,
    endColumn: (endColumn ?? 0) + 1,
  };
}

