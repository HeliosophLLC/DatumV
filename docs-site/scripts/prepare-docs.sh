#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# prepare-docs.sh
#
# Copies documentation from docs/ into docs-site/docs/, prepending
# Jekyll front matter. Also injects benchmark and roadmap content.
#
# Run from the repository root:
#   bash docs-site/scripts/prepare-docs.sh
# ═══════════════════════════════════════════════════════════════════
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SITE_DIR="$REPO_ROOT/docs-site"
DOCS_SRC="$REPO_ROOT/docs"
DOCS_DEST="$SITE_DIR/docs"

# Clean previous builds
rm -rf "$DOCS_DEST"
mkdir -p "$DOCS_DEST"

# ─── Document mapping: filename → title, nav_order, permalink ────
declare -A DOC_TITLES
DOC_TITLES=(
  ["sql.md"]="SQL Reference"
  ["architecture.md"]="Architecture"
  ["api.md"]="Programmatic API"
  ["providers.md"]="Data Providers"
  ["compute.md"]="Compute Service"
  ["indexes.md"]="Source Indexes"
  ["functions.md"]="Functions Reference"
  ["language-server.md"]="Language Server"
  ["statistics.md"]="Statistics & Manifests"
)

declare -A DOC_NAV_ORDER
DOC_NAV_ORDER=(
  ["sql.md"]="1"
  ["architecture.md"]="2"
  ["api.md"]="3"
  ["providers.md"]="4"
  ["compute.md"]="5"
  ["indexes.md"]="6"
  ["functions.md"]="7"
  ["language-server.md"]="8"
  ["statistics.md"]="9"
)

declare -A DOC_PERMALINKS
DOC_PERMALINKS=(
  ["sql.md"]="/docs/sql-reference/"
  ["architecture.md"]="/docs/architecture/"
  ["api.md"]="/docs/programmatic-api/"
  ["providers.md"]="/docs/data-providers/"
  ["compute.md"]="/docs/compute-service/"
  ["indexes.md"]="/docs/source-indexes/"
  ["functions.md"]="/docs/functions-reference/"
  ["language-server.md"]="/docs/language-server/"
  ["statistics.md"]="/docs/statistics-and-manifests/"
)

# ─── Copy each doc with front matter ────────────────────────────
for docfile in "${!DOC_TITLES[@]}"; do
  src="$DOCS_SRC/$docfile"
  if [ ! -f "$src" ]; then
    echo "WARNING: $src not found, skipping."
    continue
  fi

  title="${DOC_TITLES[$docfile]}"
  nav_order="${DOC_NAV_ORDER[$docfile]}"
  permalink="${DOC_PERMALINKS[$docfile]}"
  dest="$DOCS_DEST/$docfile"

  {
    echo "---"
    echo "layout: default"
    echo "title: \"$title\""
    echo "nav_order: $nav_order"
    echo "permalink: $permalink"
    echo "---"
    echo ""
    # Strip the breadcrumb nav line (← Back to README · ...) and rewrite
    # relative .md links to their permalink paths.
    cat "$src" \
      | sed '/^\[← Back to README\]/d' \
      | sed 's|(../README.md)|(/)| g' \
      | sed 's|(sql.md)|(/docs/sql-reference/)|g' \
      | sed 's|(architecture.md)|(/docs/architecture/)|g' \
      | sed 's|(api.md)|(/docs/programmatic-api/)|g' \
      | sed 's|(providers.md)|(/docs/data-providers/)|g' \
      | sed 's|(compute.md)|(/docs/compute-service/)|g' \
      | sed 's|(indexes.md)|(/docs/source-indexes/)|g' \
      | sed 's|(functions.md)|(/docs/functions-reference/)|g' \
      | sed 's|(language-server.md)|(/docs/language-server/)|g' \
      | sed 's|(statistics.md)|(/docs/statistics-and-manifests/)|g'
  } > "$dest"

  echo "  Prepared: $docfile → $title (nav_order: $nav_order)"
done

# ─── Inject benchmark content from README ───────────────────────
echo "  Extracting benchmarks from README.md..."
BENCHMARKS_FILE="$SITE_DIR/benchmarks.md"

# Extract everything between "### Results" and the next "## " heading
BENCH_CONTENT=$(sed -n '/^### Results$/,/^## /{/^## /!p}' "$REPO_ROOT/README.md")

if [ -n "$BENCH_CONTENT" ]; then
  # Replace the placeholder comment with actual content
  TEMP_FILE=$(mktemp)
  while IFS= read -r line; do
    if [[ "$line" == *"BENCHMARKS_CONTENT"* ]]; then
      echo "### Results"
      echo ""
      echo "$BENCH_CONTENT"
    else
      echo "$line"
    fi
  done < "$BENCHMARKS_FILE" > "$TEMP_FILE"
  mv "$TEMP_FILE" "$BENCHMARKS_FILE"
  echo "  Injected benchmark results into benchmarks.md"
else
  echo "  WARNING: Could not extract benchmark results from README.md"
fi

# ─── Inject roadmap content ─────────────────────────────────────
echo "  Injecting roadmap content..."
ROADMAP_FILE="$SITE_DIR/roadmap.md"
ROADMAP_SRC="$REPO_ROOT/ROADMAP.md"

if [ -f "$ROADMAP_SRC" ]; then
  TEMP_FILE=$(mktemp)
  while IFS= read -r line; do
    if [[ "$line" == *"ROADMAP_CONTENT"* ]]; then
      cat "$ROADMAP_SRC"
    else
      echo "$line"
    fi
  done < "$ROADMAP_FILE" > "$TEMP_FILE"
  mv "$TEMP_FILE" "$ROADMAP_FILE"
  echo "  Injected roadmap content into roadmap.md"
else
  echo "  WARNING: ROADMAP.md not found"
fi

echo ""
echo "Documentation preparation complete."
echo "  Docs:      $DOCS_DEST/ (${#DOC_TITLES[@]} files)"
echo "  Benchmarks: $BENCHMARKS_FILE"
echo "  Roadmap:    $ROADMAP_FILE"
