// Import the editor API subpath instead of the top-level `monaco-editor`
// barrel. The barrel auto-registers every Monarch language Monaco ships
// (JSON, TS, CSS, HTML, every basic SQL dialect, ~30 others) and pulls
// every language worker into the build. Going through the editor API
// alone gives a stripped surface — we then register only the languages
// we actually need below.
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import { loader } from '@monaco-editor/react';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
import { initLsp } from './lsp';
// Register the generic SQL Monarch contribution. This is the only
// language we ship today; PR 3 will swap our DatumIngest grammar in via
// `monaco.languages.setMonarchTokensProvider('sql', …)` and replace the
// contribution's tokenizer with our own. The contribution still adds
// the brackets / comments / autoclosing-pairs configuration, which we
// want to keep.
import 'monaco-editor/esm/vs/basic-languages/sql/sql.contribution.js';

// Monaco bootstrap. `@monaco-editor/react` defaults to loading Monaco from a
// CDN at runtime — fine for a public web app, broken for an Electron app
// expected to work offline. Point its loader at the bundled `monaco-editor`
// instance instead so everything ships in the app's own assets.
//
// Workers: Monaco delegates heavy tasks (tokenization, basic editing
// operations) to a dedicated Web Worker. Vite resolves `?worker` imports
// as separate worker bundles. SQL has no language-specific worker —
// Monarch tokenization runs on the main thread, and our REST-backed
// completion / hover / signature / diagnostics providers (PR 3) plug
// into Monaco's language API directly, no worker needed.

declare global {
  interface Window {
    MonacoEnvironment?: {
      getWorker(workerId: string, label: string): Worker;
    };
  }
}

let initialized = false;

export function initMonaco(): void {
  if (initialized) return;
  initialized = true;

  self.MonacoEnvironment = {
    getWorker(): Worker {
      return new editorWorker();
    },
  };

  // Hand `@monaco-editor/react` the locally bundled instance so it skips
  // the CDN-load step entirely. Must be called before any <Editor />
  // mounts.
  loader.config({ monaco });

  // Unbind Monaco's defaults for Ctrl/Cmd+Enter and F5 globally so those
  // keystrokes bubble out of Monaco to our window-level "run the focused
  // tab" handler in QueryEditorView. Two things to know:
  //
  //   1. Per-editor `editor.addCommand(key, fn)` doesn't work across
  //      multiple editors — Monaco's StandaloneKeybindingService matches
  //      the FIRST registered command for a given keystroke regardless
  //      of which editor has focus, so the keystroke kept firing the
  //      pre-split leaf's handler after a pane was split.
  //   2. Monaco's default Ctrl+Enter is `editor.action.insertLineAfter`;
  //      without disabling it, the keystroke would both insert a line
  //      AND fall through to our window handler (which would still run
  //      the query). Mapping `command: null` clears the default and
  //      lets the event bubble untouched.
  monaco.editor.addKeybindingRules([
    { keybinding: monaco.KeyMod.CtrlCmd | monaco.KeyCode.Enter, command: null },
    { keybinding: monaco.KeyCode.F5, command: null },
  ]);

  // Fire-and-forget LSP wiring: fetches the Monarch grammar and
  // registers completion / hover / signature / diagnostics providers
  // against the SQL language. Async because the initial grammar fetch
  // is over HTTP, but we don't block app boot — providers register
  // synchronously and the editor uses the built-in tokenizer until
  // the fetched grammar lands a moment later.
  void initLsp();
}
