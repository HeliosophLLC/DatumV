# DatumV static site

A Jekyll site published to GitHub Pages that makes three corpora browsable and
searchable:

- **Docs** — every markdown file under repo `/docs` (including `technical/`),
  with images, a folder tree, and full-text search.
- **Models** — `/models/catalog.json` + each entry's card markdown and hero
  image, with task facets and search.
- **Datasets** — `/datasets/catalog.json` + card markdown and hero images, with
  modality/task facets and search.

## How it works

Content lives at the repo root, not in this folder. Before every build,
[`scripts/build-site.rb`](../scripts/build-site.rb) *stages* that content into
this tree:

- one rendered page per doc / model / dataset (`docs/`, `models/`, `datasets/`),
- listing data under `_data/generated/`,
- per-section search indexes under `assets/search/` (consumed client-side by
  the vendored MiniSearch in `assets/js/vendor/`).

All staged output is git-ignored (see [`.gitignore`](.gitignore)) — only the
layouts, includes, styles, listing pages, and the staging script are committed.
The hand-authored listing pages (`docs/index.html`, `models/index.html`,
`datasets/index.html`) and the landing (`index.html`) share those directories
and are explicitly re-included.

## Preview locally

```sh
# from the repo root
ruby scripts/build-site.rb          # stage content into site/
cd site
bundle install                      # first time only
bundle exec jekyll serve            # http://localhost:4000/DatumV/
```

Re-run `ruby scripts/build-site.rb` whenever the source docs or catalogs change.

## Deploy

Pushes to `main` that touch `site/`, `docs/`, the catalogs, or the staging
script trigger [`.github/workflows/pages.yml`](../.github/workflows/pages.yml),
which stages, builds, and deploys to GitHub Pages. Enable it once under
**Settings → Pages → Build and deployment → Source: GitHub Actions**.

## Base path

The site is served under `/DatumV` (`baseurl` in `_config.yml`). Internal links
between staged pages are kept relative so they survive a baseurl change; only
assets and includes go through the `relative_url` filter. If you add a custom
domain, set `baseurl: ""`.

## The landing page

`index.html` is a starter face — replace its `<section class="hero">` markup
with your own design. The header/footer chrome and the three section cards are
already wired to the live browsers.
