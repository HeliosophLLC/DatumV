// Import the editor API subpath instead of the top-level `monaco-editor`
// barrel. The barrel auto-registers every Monarch language Monaco ships
// (JSON, TS, CSS, HTML, every basic SQL dialect, ~30 others) and pulls
// every language worker into the build. Going through the editor API
// alone gives a stripped surface — we then register only the languages
// we actually need below.
import * as monaco from 'monaco-editor/esm/vs/editor/editor.api.js';
import { loader } from '@monaco-editor/react';
import editorWorker from 'monaco-editor/esm/vs/editor/editor.worker?worker';
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
}
