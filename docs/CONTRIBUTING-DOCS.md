# Documentation Conventions

This guide defines how documentation is structured, formatted, and maintained
in the `docs/` directory. Read this before adding or modifying any doc page.

## Directory Layout

```
docs/
  CONTRIBUTING-DOCS.md    ← you are here
  toc.yml                 ← tree navigation manifest
  sql/                    ← SQL language reference (one file per clause/concept)
  functions/              ← function reference (one file per category)
```

- **`sql/`** — one markdown file per SQL clause or concept (e.g. `select.md`,
  `joins.md`, `type-system.md`). Subsections within a file use `###` headings.
- **`functions/`** — one markdown file per function category (e.g. `string.md`,
  `temporal.md`). Each function gets its own `###` heading inside the file.
- **`toc.yml`** — defines the sidebar tree navigation. Every doc page and
  significant subsection must have an entry here.

## File Template — SQL Topic

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

<Blocking vs streaming, spill behavior, QU cost — if applicable.>

## See Also

- [Related Topic](../path/to/related.md)
```

## File Template — Function Category

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

`signature` → ReturnType | QU: N

Description paragraph. Mention edge cases, NULL behavior, type constraints.

​```sql
-- Example usage
SELECT function_name(args) FROM table
​```
```

### Rules for function sections

- **Heading**: `### function_name` — lowercase, matching the SQL identifier
  exactly.
- **Signature line**: backtick-wrapped, with `→` arrow for return type, `|`
  separator for QU cost. Optional parameters in `[brackets]`.
- **Description**: one or two paragraphs. State NULL behavior, supported types,
  edge cases. Don't repeat what the signature already says.
- **Examples**: at least one `sql` code block. Show the common case first,
  edge cases after. Use inline comments (`-- result`) to show expected output.
- **Order**: functions appear in the same order as they are registered in the
  engine. New functions go at the end of their category file unless there's a
  logical grouping reason to place them elsewhere.

## Frontmatter

Every doc file must have YAML frontmatter with at least `title`:

| Field | Required | Used by | Description |
|-------|----------|---------|-------------|
| `title` | Yes | Site, language server | Page title for navigation and display |
| `category` | Functions only | Language server | Function category identifier (lowercase) |

## toc.yml Conventions

- Top-level entries are `SQL Reference` and `Functions`.
- Each item has `name` (display text) and `href` (relative path from `docs/`).
- Subsections use `items` for nesting, with `href` pointing to anchors
  (`file.md#heading-slug`).
- Anchor slugs follow GitHub-style rules: lowercase, spaces to hyphens,
  strip special characters except hyphens.
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

## Adding a New SQL Feature

1. Create `docs/sql/<feature>.md` using the SQL topic template.
2. Add frontmatter with `title`.
3. Write the content: introduction, syntax, subsections, execution model.
4. Add the file (and any `###` subsections) to `toc.yml` under `SQL Reference`.
5. Add cross-references in the `See Also` section of related pages.

## Adding a New Function

1. Open the appropriate category file in `docs/functions/`.
2. Add a `### function_name` section following the function template.
3. If this is a new category, create a new file using the function category
   template and add it to `toc.yml` under `Functions`.
4. Add the function to `toc.yml` only if it warrants its own tree entry
   (most functions don't — they're discoverable within their category page).

## Style Guide

- **Code blocks**: always use the `sql` language tag.
- **Inline code**: use backticks for function names, keywords, column names,
  type names, and file paths.
- **Tables**: use markdown tables for structured reference data (parameter
  lists, type mappings, operator tables).
- **Headings**: `##` for major sections within a page, `###` for subsections
  or individual functions. Don't go deeper than `####`.
- **Links**: use relative paths from the current file. Link to specific
  anchors when referencing a subsection in another file.
- **No breadcrumb navigation**: the old `[← Back to README]` headers are
  replaced by the `toc.yml` sidebar tree.
- **NULL behavior**: always document what happens when arguments are NULL.
- **QU cost**: always include for functions. Use the format `QU: N` or
  `QU: N + ⌊px/100K⌋` for resolution-aware costs.

## Language Server Integration

These docs are consumed by the language server (stage 2). The conventions
above ensure the parser can:

- Split files at `###` boundaries for per-function hover excerpts.
- Use frontmatter `title` and `category` for indexing.
- Use `toc.yml` for the documentation table of contents API.
- Generate `datum-docs://<key>` links using file paths and anchor slugs.

When editing docs, keep in mind that the **first paragraph after a `###`
heading** is used as the hover excerpt (truncated to ~300 characters). Lead
with the most useful information.
