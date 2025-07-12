# Language Server

[← Back to README](../README.md) · [SQL Reference](sql.md) · [Functions](functions.md) · [Providers](providers.md) · [Statistics & Manifest](statistics.md) · [Source Indexes](indexes.md) · [Architecture](architecture.md) · [Programmatic API](api.md)

DatumIngest includes a SQL language server that provides autocomplete, diagnostics, and hover for the DatumIngest SQL dialect. It runs in the browser via Blazor WebAssembly and powers Monaco editor integration.

## Architecture

```
┌──────────────────────────┐
│     Monaco Editor        │
│  (CompletionProvider,    │
│   DiagnosticsAdapter,    │
│   HoverProvider)         │
└──────────┬───────────────┘
           │ [JSInvokable]
┌──────────▼───────────────┐
│  DatumIngest.Wasm        │
│  LanguageServerInterop   │
│  (Blazor WebAssembly)    │
└──────────┬───────────────┘
           │
┌──────────▼───────────────┐
│  DatumIngest.LanguageServer │
│  LanguageService (facade)   │
│  ┌─────────────────────┐    │
│  │ CompletionProvider   │    │
│  │ DiagnosticsProvider  │    │
│  │ HoverProvider        │    │
│  └─────────────────────┘    │
└──────────┬───────────────┘
           │
┌──────────▼───────────────┐
│  Schema Manifest (JSON)  │
│  Pre-built via CLI:      │
│  datumingest manifest-schema │
└──────────────────────────┘
```

The design is transport-agnostic: `LanguageService` can be called directly from WASM interop or wrapped in an LSP JSON-RPC server for VS Code integration.

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
      "parameters": [{ "name": "value", "kind": "Scalar" }],
      "returnType": "Scalar",
      "description": "Element-wise absolute value: abs(x) = |x|."
    },
    {
      "name": "unnest",
      "parameters": [{ "name": "array_column", "kind": "Vector" }],
      "returnType": "Scalar",
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
| `AfterFrom` / `AfterJoin` | After `FROM` or `JOIN` | Tables, table-valued functions |
| `AfterWhere` / `AfterOn` | After `WHERE` or `ON` | Columns, functions, `AND`, `OR`, `NOT`, etc. |
| `AfterOrderBy` | After `ORDER BY` | Columns, `ASC`, `DESC` |
| `AfterDot` | After `alias.` | Columns from the qualified table |
| `InFunctionArguments` | Inside `func(` | Columns, scalar functions |
| `AfterInto` / `AfterAs` | After `INTO` or `AS` | None (file paths / user-defined aliases) |

Prefix filtering narrows suggestions as the user types. For example, typing `na` after `SELECT` shows only items matching that prefix.

## Diagnostics

Diagnostics uses the full SQL parser (`SqlParser.Parse`). Parse errors are converted to 0-based line/column positions aligned with the LSP Diagnostic specification.

Currently returns at most one error (the first parse failure). Future work may add semantic diagnostics (unknown table/column references).

## Hover

Hovering over a token shows contextual documentation:

- **Keywords** — Brief description of the SQL clause
- **Functions** — Full signature with parameter types and description
- **Tables** — Column list with types and nullability
- **Columns** — Data kind, nullability, and source table

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
| `DatumIngest.Wasm` | Blazor WebAssembly host with `[JSInvokable]` interop surface. |

The core library is reusable — it can also be wrapped in an LSP server for VS Code extension support without any Blazor dependency.
