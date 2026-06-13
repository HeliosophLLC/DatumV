# Contributing to Documentation

This guide governs the DatumV documentation: how to think about voice and positioning when writing user-facing content, and the mechanical conventions for file structure, templates, and TOC integration.

It is an internal contributor document — not shipped in the in-app docs viewer.

---

## Part 1 — Voice & Positioning

### Positioning one-liner

> DatumV runs ML models on your data — locally, batched, no Python. You describe what you want in SQL, and the engine handles inference, batching, calibration, and I/O across dozens of vision, audio, and text models from a built-in catalog.

Use this verbatim or paraphrase as the lede on any high-level entry point (`README.md`, `docs/getting-started.md`, the in-app shell's "what is this?" panel). Downstream pages can assume the reader has already encountered the headline.

### What DatumV is

- An app for running ML models on your data.
- Self-contained: one binary, no cluster, no service, no API account.
- Model-centric: `CREATE MODEL`, `infer()`, batched inference, calibration, and an inspectable catalog are *core*, not extensions.
- Local-first: data and models live on the user's machine. Inference runs offline.
- SQL is the syntax. It is load-bearing — but it is not the headline.

### What DatumV is NOT

Use these as a "do I have my framing right?" checklist:

- Not a data-prep / ETL tool. (Those workflows are supported; they are not the headline.)
- Not a Python replacement for ML pipelines. Python is not the comparator.
- Not a notebook, workflow orchestrator, or feature store.
- Not a database. The `.datum` file format is for intermediates and caches, not persistence.
- Not an "ML platform" — vague vendor-speak we explicitly avoid.

### Tone

- **Declarative, present tense.** "DatumV runs YOLO." Not "will run," "can run," or "is designed to run."
- **Concrete over abstract.** Lead with a working query before any prose about "primitives" or "abstractions."
- **Every concept earns a runnable example.** Code blocks before tables; tables before paragraphs.
- **No marketing-speak.** "200+ built-in functions" is fine. "Powerful," "blazing-fast," "enterprise-grade" are not.
- **Audience: a SQL-fluent reader who has heard of YOLO but does not write PyTorch.** Don't over-explain SQL. Do explain what a model returns.
- **No roadmap leaks.** When a forward-looking question would otherwise call for one, use the canonical hedge below.

### Roadmap hedge

When a question would naturally invite a "coming soon" answer, use a calibrated non-answer instead of specifics:

> DatumV is under active development. This release covers [X]. Additional capabilities will be announced as they ship.

Avoid in user-facing docs:

- "Phase 1 / Phase 2 / Phase 3" framing.
- "Planned," "future," "coming soon," "TODO," "WIP," "deferred."
- Version numbers like "v1 / v2" — *except* when versioning something on disk (e.g., the `.datum` file format is at v4).

### Vocabulary swaps

| Avoid | Prefer |
|-------|--------|
| ingest / ingestion | load / read |
| ETL | "load → transform → export" if a flow is needed; otherwise drop |
| data prep / preparing data | transform / feature engineering |
| ML platform / ML dataset engine | "app for running ML models" |
| pipeline (as a category) | query / SQL / "workflow" if literal |
| phase 1/2/3, planned, future, coming soon, TODO, WIP, deferred | drop entirely from user-facing docs |
| "v1" / "v2" / "v4" | only when versioning something on disk |
| powerful / blazing-fast / enterprise-grade | drop |
| "model zoo" (overused) | name actual models, or "catalog" / "built-in catalog" |

### Canonical FAQ answers

These exist once, here. Documents that touch a recurring question quote or paraphrase from this section rather than answering independently — so `README.md`, `docs/getting-started.md`, and `docs/models.md` never disagree on positioning.

#### Why not just Python (PyTorch + Hugging Face + pandas)?

Python is the most common way to run ML models today, and it works. DatumV is a different shape:

- **Batched by default.** Inference is a column operation, batched across thousands of rows transparently — no manual `DataLoader` wiring.
- **Queryable.** Filter, join, and group on model outputs without writing glue code.
- **Memory-bounded.** Spill-to-disk for datasets larger than RAM. No OOM kills on big batches.
- **No environment management.** Single binary. No `pip install`, no virtualenv, no CUDA-version roulette.
- **Inspectable.** Every intermediate is a row in a table you can `SELECT` from.
- **Includes visualization primitives.** Render the output of a model without reaching for matplotlib.

#### Why not DuckDB + an inference extension?

DuckDB is the obvious "lightweight columnar SQL engine" alternative, and there are community extensions that bolt inference onto it. The reasons DatumV isn't an extension on top of DuckDB:

1. **Type system.** DuckDB has `BLOB`, fixed-size vectors, `LIST`, and `STRUCT`. It does not have `Image`, `Audio`, `Video`, `PointCloud`, or `Mesh` as semantic types with type-aware operators. As an extension, an image becomes a BLOB and loses everything that makes image pipelines fuse (decode-once, encode-once, transform pushdown).
2. **Model lifecycle.** Extensions can register UDFs but have nowhere to hang model state across queries — no `CREATE MODEL`, no calibration storage, no eviction, no memory-budget participation.
3. **Execution shape.** A model returning `Array<Struct<box: Vector, label: String, score: Float32>>` per row is the common case for vision models. DatumV's planner is built around that shape; in an extension, it has to be threaded through manually.

DuckDB is excellent for "fast SQL over my parquet files." It is a less-good substrate for "engine built around model invocation as a first-class citizen."

#### Why not SQLite or Postgres?

The columnar-execution and type-system arguments above apply more strongly. SQLite has five storage classes (NULL, INTEGER, REAL, TEXT, BLOB) and no analytical execution engine; Postgres has a richer type system but no native media types and no place to hang first-class model lifecycle. Both could be extended to call out to an inference runtime, but the result would be slower (row-based execution) and shallower (models stay external).

#### Why not a VSCode extension?

DatumV is an app, not an editor. It manages downloaded model weights, holds GPU memory across queries, runs headlessly, and is designed to participate in workflows that aren't tied to a code editor. An extension is the right shape when the work is *editing*; DatumV's work is *executing*.

#### How does this compare to Modal / Replicate / Anyscale?

Cloud inference services are great for production. DatumV is built for the phase *before* production — exploration, iteration, and dataset-scale work where you don't want a meter running.

- **Local-first.** Data never leaves your machine.
- **No API credits.** Iterate freely; throw a thousand queries at the catalog without watching a bill.
- **No latency floor.** Local batch inference is faster than per-request round-trips.
- **Same models.** Artifacts you explore with locally are the same ones you'd later ship to a cloud GPU.

#### Does this work offline?

Yes — fully. Once installed, DatumV does not require an internet connection for queries, inference, or workflow execution. Model weights are downloaded once and cached locally; everything else runs on your machine.

This is a significant difference from cloud inference services and from tools like ComfyUI, which is focused on visual generative models. DatumV spans vision, audio, text, embeddings, depth estimation, OCR, and classical CV — and operates over datasets rather than one image at a time.

#### What models can I run? Can I add my own?

The built-in catalog spans object detection, segmentation, classification, depth estimation, OCR, captioning, embeddings, text-to-speech, image generation, and LLMs. See [docs/models.md](docs/models.md) for the current catalog.

You can add your own models with `CREATE MODEL` (see [docs/sql/create-model.md](docs/sql/create-model.md)). The interface is young and growing.

#### Do I need ONNX, or can I use PyTorch / GGUF / safetensors?

ONNX is the supported model format today. A Python bridge for additional formats is under active development; Bark (text-to-speech) currently runs through this bridge as an early experiment.

#### Does training work?

No — DatumV runs inference against pre-trained models. Training is out of scope.

#### What about GPU support?

NVIDIA GPUs (CUDA) and CPU. Other hardware is in development.

---

## Part 2 — Structure & Mechanics

### Directory layout

```
DatumV/
  CONTRIBUTING-DOCS.md    ← you are here
  README.md
  docs/
    toc.yml               ← tree navigation manifest
    figures/              ← static images referenced by docs
    sql/                  ← SQL language reference (one file per clause/concept)
    functions/            ← function reference (one file per category)
    technical/            ← engine-internal references (file format, architecture, C# API)
    <user-facing docs>    ← getting-started.md, examples/, models.md, onnx.md, …
```

- **`docs/sql/`** — one markdown file per SQL clause or concept (e.g. `select.md`, `joins.md`, `type-system.md`). Subsections within a file use `###` headings.
- **`docs/functions/`** — one markdown file per function category (e.g. `string.md`, `temporal.md`). Each function gets its own `###` heading inside the file.
- **`docs/technical/`** — engine internals not aimed at end users: file format, architecture, C# embedding API, execution plans, operators, indexes, statistics, value representation, language server. Linked from user-facing docs only where a casual reader benefits from the depth.
- **`docs/toc.yml`** — defines the sidebar tree navigation. Every user-facing doc page and significant subsection must have an entry. Technical docs are not in `toc.yml` — they are reached via inline links from the docs that need them.

### File template — SQL topic

Every SQL topic file starts with YAML frontmatter:

```yaml
---
title: <Page Title>
---
```

Followed by this structure:

```markdown
<Introduction — what this feature is and when to use it.>

## Syntax

​```sql
<canonical syntax>
​```

### <Variant or Subsection>

<Description.>

​```sql
<Example(s)>
​```

## Execution Model

<Blocking vs streaming, spill behavior — if applicable.>

## See Also

- [Related Topic](../path/to/related.md)
```

### File template — function category

Function category files start with:

```yaml
---
title: <Category Name> Functions
category: <category>
---
```

Followed by a category introduction and per-function sections:

```markdown
<Introduction — what this category covers, common patterns.>

### function_name

`signature` → ReturnType

Description paragraph. Mention edge cases, NULL behavior, type constraints.

​```sql
-- Example usage
SELECT function_name(args) FROM table
​```
```

#### Rules for function sections

- **Heading**: `### function_name` — lowercase, matching the SQL identifier exactly.
- **Signature line**: backtick-wrapped, with `→` arrow for return type. Optional parameters in `[brackets]`.
- **Description**: one or two paragraphs. State NULL behavior, supported types, edge cases. Don't repeat what the signature already says.
- **Examples**: at least one `sql` code block. Show the common case first, edge cases after. Use inline comments (`-- result`) to show expected output.
- **Order**: functions appear in the same order as they are registered in the engine. New functions go at the end of their category file unless there is a logical grouping reason to place them elsewhere.

### Frontmatter

Every user-facing doc file has YAML frontmatter with at least `title`:

| Field | Required | Used by | Description |
|-------|----------|---------|-------------|
| `title` | Yes | Site, language server | Page title for navigation and display |
| `category` | Functions only | Language server | Function category identifier (lowercase) |

### toc.yml conventions

- Top-level entries are `SQL Reference` and `Functions`.
- Each item has `name` (display text) and `href` (relative path from `docs/`).
- Subsections use `items` for nesting, with `href` pointing to anchors (`file.md#heading-slug`).
- Anchor slugs follow GitHub-style rules: lowercase, spaces to hyphens, strip special characters except hyphens.
- **Every new file or significant `###` section must be added to `toc.yml`.**

Example entry with children:

```yaml
- name: TABLESAMPLE
  href: sql/tablesample.md
  items:
    - name: BERNOULLI
      href: sql/tablesample.md#bernoulli
    - name: STRATIFIED
      href: sql/tablesample.md#stratified
```

### Adding a new SQL feature

1. Create `docs/sql/<feature>.md` using the SQL topic template.
2. Add frontmatter with `title`.
3. Write the content: introduction, syntax, subsections, execution model.
4. Add the file (and any `###` subsections) to `toc.yml` under `SQL Reference`.
5. Add cross-references in the `See Also` section of related pages.

### Adding a new function

1. Open the appropriate category file in `docs/functions/`.
2. Add a `### function_name` section following the function template.
3. If this is a new category, create a new file using the function category template and add it to `toc.yml` under `Functions`.
4. Add the function to `toc.yml` only if it warrants its own tree entry (most functions don't — they're discoverable within their category page).

### Style guide

- **Code blocks**: always use the `sql` language tag for SQL examples.
- **Inline code**: use backticks for function names, keywords, column names, type names, and file paths.
- **Tables**: use markdown tables for structured reference data (parameter lists, type mappings, operator tables).
- **Headings**: `##` for major sections within a page, `###` for subsections or individual functions. Don't go deeper than `####`.
- **Links**: use relative paths from the current file. Link to specific anchors when referencing a subsection in another file.
- **No breadcrumb navigation**: the old `[← Back to README]` headers are replaced by the `toc.yml` sidebar tree.
- **NULL behavior**: always document what happens when arguments are NULL.

### Language server integration

These docs are consumed by the language server. The conventions above ensure the parser can:

- Split files at `###` boundaries for per-function hover excerpts.
- Use frontmatter `title` and `category` for indexing.
- Use `toc.yml` for the documentation table of contents API.
- Generate `datum-docs://<key>` links using file paths and anchor slugs.

When editing docs, keep in mind that the **first paragraph after a `###` heading** is used as the hover excerpt (truncated to ~300 characters). Lead with the most useful information.
