# Language Server

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Star Schema](star-schema.md) · [Programmatic API](api.md) · [Compute Backend](compute.md)

DatumIngest includes a SQL language server that provides autocomplete, diagnostics, and hover for the DatumIngest SQL dialect. Two transport options are available: **Blazor WebAssembly** (client-side, no server required) and **SignalR** (server-side, integrated into any ASP.NET host).

## Architecture

```
┌──────────────────────────┐     ┌──────────────────────────┐
│     Monaco Editor        │     │     Monaco Editor        │
│   (browser, standalone)  │     │   (browser, hosted app)  │
└──────────┬───────────────┘     └──────────┬───────────────┘
           │ [JSInvokable]                  │ SignalR (WebSocket)
┌──────────▼───────────────┐     ┌──────────▼───────────────┐
│  DatumIngest.Wasm        │     │  DatumIngest.Editor      │
│  LanguageServerInterop   │     │  LanguageServerHub       │
│  (Blazor WebAssembly)    │     │  (ASP.NET SignalR)       │
└──────────┬───────────────┘     └──────────┬───────────────┘
           │                                │
           └───────────┬───────────────────┘
                       │
           ┌───────────▼───────────────┐
           │  DatumIngest.LanguageServer │
           │  LanguageService (facade)   │
           │  ┌─────────────────────┐    │
           │  │ CompletionProvider   │    │
           │  │ DiagnosticsProvider  │    │
           │  │ SemanticAnalyzer     │    │
           │  │ HoverProvider        │    │
           │  └─────────────────────┘    │
           └───────────┬───────────────┘
                       │
           ┌───────────▼───────────────┐
           │  Schema Manifest (JSON)    │
           │  Pre-built via CLI:        │
           │  datumingest manifest-schema │
           └───────────────────────────┘
```

The design is transport-agnostic: `LanguageService` can be called from WASM interop, a SignalR hub, or wrapped in an LSP JSON-RPC server for VS Code integration.

## Schema Manifest

The language server does not access data files at runtime. Instead, a **schema manifest** is pre-built from the data catalog using the CLI, then loaded into the browser.

### Generating the manifest

```bash
datumingest manifest-schema --source ./data --output schema.json
```

This introspects all registered data sources and produces a JSON file containing:

- **Table schemas** — table names, column names, data kinds, nullability
- **Function signatures** — all 149+ built-in functions with parameter names, types, descriptions
- **Keywords** — all SQL keywords recognized by the DatumIngest dialect

### Manifest format

```json
{
  "version": 1,
  "tables": [
    {
      "name": "images",
      "columns": [
        { "name": "path", "kind": "String", "nullable": false },
        { "name": "label", "kind": "String", "nullable": true },
        { "name": "embedding", "kind": "Vector", "nullable": false }
      ]
    }
  ],
  "functions": [
    {
      "name": "abs",
      "parameters": [{ "name": "value", "kind": "Float32" }],
      "returnType": "Float32",
      "description": "Element-wise absolute value: abs(x) = |x|."
    },
    {
      "name": "unnest",
      "parameters": [{ "name": "array_column", "kind": "Vector" }],
      "returnType": "Float32",
      "description": "Expands a vector column into individual rows.",
      "isTableValued": true
    }
  ],
  "keywords": ["SELECT", "FROM", "WHERE", "JOIN", ...]
}
```

## Completion Zones

The completion engine classifies the cursor position using tokenizer-only analysis (no full parse, so it works with incomplete SQL). Each zone determines which completions are offered:

| Zone | Trigger | Completions |
|------|---------|-------------|
| `StatementStart` | Empty input | `SELECT` |
| `AfterSelect` | After `SELECT` | Columns, scalar functions, `FROM`, `AS`, `CAST` |
| `AfterFrom` / `AfterJoin` | After `FROM` or `JOIN` | Tables, table-valued functions. Table names that require quoting (contain spaces, hyphens, or collide with keywords) are automatically double-quoted in the inserted text. |
| `AfterWhere` / `AfterOn` | After `WHERE` or `ON` | Columns, functions, `AND`, `OR`, `NOT`, etc. |
| `AfterOrderBy` | After `ORDER BY` | Columns, `ASC`, `DESC` |
| `AfterDot` | After `alias.` | Columns from the qualified table |
| `InFunctionArguments` | Inside `func(` | Columns, scalar functions |
| `AfterInto` / `AfterAs` | After `INTO` or `AS` | None (file paths / user-defined aliases) |

Prefix filtering narrows suggestions as the user types. For example, typing `na` after `SELECT` shows only items matching that prefix.

## Diagnostics

The diagnostics pipeline uses an **error-recovering parser** that collects multiple errors per document and produces partial ASTs for downstream analysis:

1. **Syntax errors** — `SqlParser.TryParseRecovering` parses SQL clause-by-clause (SELECT, FROM, JOIN, WHERE, INTO, ORDER BY, LIMIT, OFFSET). When a clause fails, the error is recorded and the parser skips to the next clause keyword to continue. This yields multiple `Error`-severity diagnostics from a single parse pass rather than stopping at the first failure.

2. **Semantic warnings** — When the service has been initialized with a manifest and a partial AST is available, it is passed to `SemanticAnalyzer` which validates every table, column, and function reference against the known schema. `ErrorExpression` placeholder nodes inserted during recovery are silently skipped. Unknown references produce `Warning`-severity diagnostics with accurate underline spans.

### Validated references

| Reference kind | Example | Diagnostic message |
|----------------|---------|--------------------|
| Unknown table | `FROM ghost` | Unknown table 'ghost'. |
| Unknown column (unqualified) | `SELECT phantom FROM t` | Unknown column 'phantom'. |
| Unknown column (qualified) | `SELECT t.bogus FROM t` | Unknown column 'bogus' in table 't'. |
| Unknown scalar function | `SELECT bad_fn(x) FROM t` | Unknown function 'bad_fn'. |
| Unknown table-valued function | `FROM missing_tvf('path')` | Unknown function 'missing_tvf'. |
| Unknown table qualifier | `SELECT z.id FROM t AS u` | Unknown table or alias 'z'. |

All lookups are **case-insensitive** — the manifest may use `Users` while the query says `users`.

### Opaque sources

Subqueries and table-valued function sources are treated as **opaque**: their output columns are unknown at manifest-analysis time, so column references qualified by a subquery alias or function alias are never flagged.

### Source spans

AST nodes that carry names (`ColumnReference`, `TableReference`, `FunctionCallExpression`, `FunctionSource`, `CastExpression`, `SelectTableColumns`) include a `SourceSpan` with 1-based line, column, and character length. `DiagnosticsProvider` converts these to 0-based LSP coordinates for accurate editor underlining.

## Hover

Hovering over a token shows contextual documentation:

- **Keywords** — Brief description of the SQL clause
- **Functions** — Full signature with parameter types and description
- **Tables** — Column list with types and nullability
- **Columns** — Data kind, nullability, and source table

## Syntax Highlighting

The hub exposes a `GetMonarchGrammar` method that returns a [Monarch grammar](https://microsoft.github.io/monaco-editor/docs.html#interfaces/languages.IMonarchLanguage.html) for the DatumIngest SQL dialect. Monarch is Monaco Editor's built-in client-side tokenizer — syntax highlighting runs entirely in the browser with no server round-trip per keystroke.

Call `GetMonarchGrammar` once after the connection opens, before creating the editor model:

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl('/language-server')
    .build();

await connection.start();

// Fetch the grammar and register it with Monaco.
const grammar = await connection.invoke('GetMonarchGrammar');
monaco.languages.register({ id: 'datumingest' });
monaco.languages.setMonarchTokensProvider('datumingest', grammar);

// Then initialize with the schema manifest and create the editor normally.
await connection.invoke('Initialize', manifestJson);
const editor = monaco.editor.create(container, {
    language: 'datumingest',
    value: '',
});
```

`GetMonarchGrammar` does not require `Initialize` to have been called — it is stateless and returns the same grammar for every connection.

### Token types

The grammar uses standard Monaco token type names that map automatically to editor theme colors:

| Token type | What it covers |
|---|---|
| `keyword` | SQL clause and operator keywords (`SELECT`, `FROM`, `LET`, `PIVOT`, …) |
| `keyword.constant` | `TRUE`, `FALSE`, `NULL` |
| `string` | Single-quoted string literals (`'hello'`, `''` escape) |
| `number` | Integer, decimal, and scientific notation literals |
| `variable` | Named parameter placeholders (`$threshold`) |
| `comment` | Line comments (`--`) and block comments (`/* */`) |
| `operator` | Arithmetic and comparison symbols (`+`, `<=`, `<>`, …) |
| `delimiter` | Commas, parentheses, dots |
| `identifier` | Unquoted and double-quoted identifiers (default) |

## WASM Integration

The `DatumIngest.Wasm` project exposes four `[JSInvokable]` methods via `LanguageServerInterop`:

```javascript
// Initialize with pre-built manifest
const interop = await DotNet.createJSObjectReference(assembly, 'DatumIngest.Wasm');
interop.invokeMethod('Initialize', manifestJson);

// Get completions at cursor position
const completionsJson = interop.invokeMethod('GetCompletions', sql, cursorOffset);
const completions = JSON.parse(completionsJson);

// Get parse errors
const diagnosticsJson = interop.invokeMethod('GetDiagnostics', sql);
const diagnostics = JSON.parse(diagnosticsJson);

// Get hover info
const hoverJson = interop.invokeMethod('GetHover', sql, cursorOffset);
const hover = JSON.parse(hoverJson); // null if nothing to display
```

All methods are synchronous (no I/O) and return JSON strings for simple marshaling.

## Projects

| Project | Purpose |
|---------|---------|
| `DatumIngest.LanguageServer` | Core library: completion, diagnostics, hover. No WASM or ASP.NET dependency. |
| `DatumIngest.Editor` | SignalR hub + extension methods. Class library — add to any ASP.NET host. |
| `DatumIngest.Wasm` | Blazor WebAssembly host with `[JSInvokable]` interop surface. |

The core library is reusable — it can also be wrapped in an LSP server for VS Code extension support without any Blazor dependency.

## SignalR Integration

The `DatumIngest.Editor` package provides a SignalR hub that wraps `LanguageService` for server-side language intelligence over WebSocket. Each connection maintains its own `LanguageService` instance, cleaned up automatically on disconnect.

### Host setup

```csharp
// The host application already has SignalR configured (e.g. with Redis backplane).
builder.Services.AddSignalR().AddStackExchangeRedis(...);

// Map the language server hub (defaults to /language-server).
app.MapDatumIngestEditor();
// Or with a custom path:
app.MapDatumIngestEditor("/custom/path");
```

### JavaScript client

```javascript
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/language-server")
    .withAutomaticReconnect()
    .build();

await connection.start();
await connection.invoke("Initialize", manifestJson);

// On keystroke (debounced)
const completions = await connection.invoke("GetCompletions", sql, cursorOffset);
const diagnostics = await connection.invoke("GetDiagnostics", sql);
const hover = await connection.invoke("GetHover", sql, cursorOffset);
```

SignalR handles JSON serialization natively — no manual marshaling.

### Connection lifecycle

- **Initialize**: Client sends the manifest JSON. The hub creates a `LanguageService` bound to this connection.
- **Request/Response**: `GetCompletions`, `GetDiagnostics`, `GetHover` — all synchronous, pure computation.
- **Reconnect**: If the WebSocket drops and reconnects (possibly to a different server behind a load balancer), the client must call `Initialize` again. Per-connection state is server-local by design.
- **Disconnect**: The hub removes the `LanguageService` instance from memory.
