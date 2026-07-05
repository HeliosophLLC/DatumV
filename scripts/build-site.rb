#!/usr/bin/env ruby
# frozen_string_literal: true

# Stages browsable content into the Jekyll site under /site.
#
# Reads three corpora from the repo root and emits, for each:
#   * one rendered page per item (docs page / model card / dataset card),
#   * a search index (JSON) consumed client-side by MiniSearch,
#   * listing data under site/_data/generated for the browse pages,
#   * copies of every referenced image asset.
#
# Everything written here lives under paths that site/.gitignore excludes, so
# the output is disposable — regenerated on every local build and in CI before
# `jekyll build`. Run from anywhere: `ruby scripts/build-site.rb`.

require "json"
require "yaml"
require "fileutils"

ROOT  = File.expand_path("..", __dir__)
SITE  = File.join(ROOT, "site")

DOCS_SRC     = File.join(ROOT, "docs")
MODELS_SRC   = File.join(ROOT, "models")
DATASETS_SRC = File.join(ROOT, "datasets")

TASK_REGISTRY = File.join(ROOT, "src", "Heliosoph.DatumV", "Catalog", "Registries", "TaskTypeRegistry.cs")

# Family display order + labels, mirroring the in-app task sidebar.
FAMILY_ORDER = %w[ComputerVision NaturalLanguageProcessing Audio Multimodal Tabular].freeze
FAMILY_LABELS = {
  "ComputerVision" => "Computer Vision",
  "NaturalLanguageProcessing" => "Natural Language",
  "Audio" => "Audio",
  "Multimodal" => "Multimodal",
  "Tabular" => "Tabular",
}.freeze

DOCS_OUT     = File.join(SITE, "docs")
MODELS_OUT   = File.join(SITE, "models")
DATASETS_OUT = File.join(SITE, "datasets")
DATA_OUT     = File.join(SITE, "_data", "generated")
SEARCH_OUT   = File.join(SITE, "assets", "search")

IMAGE_EXT = %w[.png .jpg .jpeg .gif .svg .webp .bmp].freeze

# ---------------------------------------------------------------------------
# Small helpers
# ---------------------------------------------------------------------------

def slugify(text)
  text.to_s.downcase.gsub(/[^a-z0-9]+/, "-").gsub(/\A-+|-+\z/, "")
end

# Keep slugs globally unique within a section.
def unique_slug(base, used)
  slug = base.empty? ? "item" : base
  candidate = slug
  n = 2
  while used.include?(candidate)
    candidate = "#{slug}-#{n}"
    n += 1
  end
  used << candidate
  candidate
end

def title_case(name)
  name.tr("-_", "  ").split.map { |w| w[0] ? w[0].upcase + w[1..] : w }.join(" ")
end

# Split a YAML front-matter block off the top of a markdown string. Returns
# [frontmatter_hash_or_nil, body].
def split_frontmatter(text)
  return [nil, text] unless text.start_with?("---")

  m = text.match(/\A---\r?\n(.*?)\r?\n---\r?\n?(.*)\z/m)
  return [nil, text] unless m

  meta =
    begin
      YAML.safe_load(m[1]) || {}
    rescue StandardError
      {}
    end
  [meta, m[2]]
end

def extract_headings(body)
  body.each_line.filter_map do |line|
    mm = line.match(/\A\#{1,6}\s+(.+?)\s*\z/)
    mm && mm[1]
  end.join("\n")
end

# Reduce markdown to searchable plain-ish text: drop images, unwrap links to
# their label, strip fences/backticks/emphasis. Good enough for indexing.
def to_plain(body)
  body
    .gsub(/```.*?```/m) { |b| b.gsub(/```[^\n]*/, "") }        # keep code text, drop fences
    .gsub(/!\[[^\]]*\]\([^)]*\)/, " ")                          # images -> nothing
    .gsub(/\[([^\]]*)\]\([^)]*\)/, '\1')                        # links -> label
    .gsub(/[`*_>#|]/, " ")
    .gsub(/\s+/, " ")
    .strip
end

# Emit a page: YAML front matter + a raw-guarded body so SQL braces in the
# content are never mistaken for Liquid tags.
def write_page(path, front, body)
  FileUtils.mkdir_p(File.dirname(path))
  File.write(path, "#{front.to_yaml}---\n{% raw %}\n#{body.strip}\n{% endraw %}\n")
end

def write_json(path, data)
  FileUtils.mkdir_p(File.dirname(path))
  File.write(path, JSON.pretty_generate(data))
end

# Wipe a generated directory but preserve committed listing pages.
def clean_generated(dir, keep: %w[index.html])
  return unless Dir.exist?(dir)

  Dir.each_child(dir) do |name|
    next if keep.include?(name)

    FileUtils.rm_rf(File.join(dir, name))
  end
end

# Copy relative images referenced by a card into the section's asset folder and
# rewrite the markdown to point at the copied files. Returns the rewritten body.
def stage_card_images(body, card_dir, assets_out_dir, assets_rel_prefix)
  body.gsub(/!\[([^\]]*)\]\(([^)]+)\)/) do
    alt = Regexp.last_match(1)
    target = Regexp.last_match(2).strip
    if target.start_with?("http://", "https://", "//", "/", "data:")
      "![#{alt}](#{target})"
    else
      clean = target.split(/[?#]/).first
      src = File.expand_path(clean, card_dir)
      if File.file?(src)
        base = File.basename(src)
        FileUtils.mkdir_p(assets_out_dir)
        FileUtils.cp(src, File.join(assets_out_dir, base))
        "![#{alt}](#{assets_rel_prefix}/#{base})"
      else
        "![#{alt}](#{target})"
      end
    end
  end
end

# Parse the C# TaskTypeRegistry so the site's family colours track the app's
# source of truth (each `new("Name", TaskFamily.X, …)` line). Returns
# { taskName => family }. Empty if the registry can't be found.
def load_task_families
  return {} unless File.file?(TASK_REGISTRY)

  map = {}
  File.read(TASK_REGISTRY, encoding: "utf-8").scan(/new\("([A-Za-z0-9]+)",\s*TaskFamily\.(\w+)/) do
    map[Regexp.last_match(1)] = Regexp.last_match(2)
  end
  map
end

# Order a set of task names into family groups for the facet sidebar.
def group_tasks_by_family(task_names, families)
  by_family = Hash.new { |h, k| h[k] = [] }
  task_names.each { |t| by_family[families[t] || "Other"] << t }
  FAMILY_ORDER.filter_map do |fam|
    next if by_family[fam].empty?

    { "family" => fam, "label" => FAMILY_LABELS[fam] || fam, "tasks" => by_family[fam].sort }
  end
end

# ---------------------------------------------------------------------------
# Docs
# ---------------------------------------------------------------------------

def stage_docs
  clean_generated(DOCS_OUT)

  index = []
  files = Dir.glob(File.join(DOCS_SRC, "**", "*.md")).sort

  files.each do |abs|
    rel = abs.sub("#{DOCS_SRC}/", "")          # e.g. sql/select.md
    rel_noext = rel.sub(/\.md\z/, "")
    meta, body = split_frontmatter(File.read(abs, encoding: "utf-8"))
    name = File.basename(rel, ".md")
    title = (meta && meta["title"]) || title_case(name)
    folders = File.dirname(rel) == "." ? [] : File.dirname(rel).split("/")

    # Intra-corpus links point at .md files; rewrite to the rendered .html so
    # relative navigation resolves against the mirrored output tree. Anchors
    # and query strings are preserved; external links are left alone.
    rendered = body.gsub(/\]\(([^)]+\.md)((?:#|\?)[^)]*)?\)/) do
      link = Regexp.last_match(1)
      tail = Regexp.last_match(2)
      if link.start_with?("http://", "https://", "//")
        "](#{link}#{tail})"
      else
        "](#{link.sub(/\.md\z/, ".html")}#{tail})"
      end
    end

    front = {
      "layout" => "doc",
      "section" => "docs",
      "title" => title,
      "permalink" => "/docs/#{rel_noext}.html",
      "doc_path" => rel_noext,
      "folders" => folders,
    }
    write_page(File.join(DOCS_OUT, rel), front, rendered)

    index << {
      "id" => rel_noext,
      "name" => name,
      "title" => title,
      "folders" => folders,
      "headings" => extract_headings(body),
      "content" => to_plain(body),
      "url" => "/docs/#{rel_noext}.html",
    }
  end

  # Copy every non-markdown asset (figures, screenshots) preserving the tree so
  # relative image references in the docs resolve unchanged.
  Dir.glob(File.join(DOCS_SRC, "**", "*")).each do |abs|
    next unless File.file?(abs)
    next if abs.end_with?(".md")
    next unless IMAGE_EXT.include?(File.extname(abs).downcase)

    rel = abs.sub("#{DOCS_SRC}/", "")
    dest = File.join(DOCS_OUT, rel)
    FileUtils.mkdir_p(File.dirname(dest))
    FileUtils.cp(abs, dest)
  end

  write_json(File.join(SEARCH_OUT, "docs.json"), index)
  write_json(File.join(DATA_OUT, "docs_tree.json"), build_docs_tree(index))
  puts "docs:     #{index.size} pages"
end

# Fold the flat doc list into a nested folder tree for the browse sidebar.
def build_docs_tree(index)
  root = { "name" => "", "path" => "", "children" => {} }

  index.sort_by { |d| d["id"] }.each do |doc|
    node = root
    doc["folders"].each do |folder|
      node["children"][folder] ||= {
        "type" => "folder", "name" => folder,
        "path" => [node["path"], folder].reject(&:empty?).join("/"),
        "children" => {},
      }
      node = node["children"][folder]
    end
    node["children"][doc["name"]] = {
      "type" => "file", "name" => doc["name"],
      "title" => doc["title"], "url" => doc["url"], "path" => doc["id"],
    }
  end

  materialize = lambda do |node|
    kids = node["children"].values.map do |child|
      child.key?("children") ? materialize.call(child) : child
    end
    # Folders first, then files; each alphabetical.
    kids.sort_by! { |c| [c["type"] == "folder" ? 0 : 1, c["name"].downcase] }
    { "type" => "folder", "name" => node["name"], "path" => node["path"], "children" => kids }
  end

  materialize.call(root)["children"]
end

# ---------------------------------------------------------------------------
# Catalog sections (models + datasets share almost all machinery)
# ---------------------------------------------------------------------------

def stage_catalog(kind:, src_dir:, out_dir:, entries_key:, tasks_field:, families:)
  clean_generated(out_dir)

  catalog = JSON.parse(File.read(File.join(src_dir, "catalog.json"), encoding: "utf-8"))
  entries = catalog[entries_key] || []
  used_slugs = []
  search = []
  listing = []

  entries.each do |entry|
    name = entry["name"] || "Untitled"
    slug = unique_slug(slugify(name), used_slugs)
    assets_out = File.join(out_dir, "assets", slug)

    # Card markdown (optional).
    card_body = ""
    if entry["cardFile"]
      card_abs = File.join(src_dir, entry["cardFile"])
      if File.file?(card_abs)
        _meta, raw = split_frontmatter(File.read(card_abs, encoding: "utf-8"))
        card_body = stage_card_images(raw, File.dirname(card_abs), assets_out, "assets/#{slug}")
      end
    end

    # Hero image (optional) — copied even when the card doesn't embed it, for
    # the detail header and the listing thumbnail.
    hero_url = nil
    if entry["heroImageFile"]
      hero_abs = File.join(src_dir, entry["heroImageFile"])
      if File.file?(hero_abs)
        base = "hero#{File.extname(hero_abs)}"
        FileUtils.mkdir_p(assets_out)
        FileUtils.cp(hero_abs, File.join(assets_out, base))
        hero_url = "/#{kind}/assets/#{slug}/#{base}"
      end
    end

    tasks = entry[tasks_field] || []
    tags = entry["tags"] || []
    modalities = entry["modalities"] || []
    variants = (entry["variants"] || []).map do |v|
      {
        "id" => v["id"],
        "displayName" => v["displayName"] || v["id"],
        "summary" => v["summary"],
        "approxSizeMb" => v["approxSizeMb"],
        "hardware" => v["hardware"],
      }.compact
    end

    front = {
      "layout" => kind == "models" ? "model" : "dataset",
      "section" => kind,
      "title" => name,
      "permalink" => "/#{kind}/#{slug}.html",
      "slug" => slug,
      "summary" => entry["summary"],
      "description" => entry["description"],
      "tasks" => tasks,
      "tags" => tags,
      "modalities" => modalities,
      "license_ids" => entry["licenseIds"] || [],
      "attributions" => entry["attributions"] || [],
      "variants" => variants,
      "hero" => hero_url,
    }.compact
    write_page(File.join(out_dir, "#{slug}.md"), front, card_body)

    search << {
      "id" => slug,
      "name" => name,
      "summary" => entry["summary"],
      "description" => entry["description"],
      "tasks" => tasks.join(" "),
      "tags" => tags.join(" "),
      "modalities" => modalities.join(" "),
      "content" => to_plain(card_body),
      "url" => "/#{kind}/#{slug}.html",
    }.compact

    listing << {
      "slug" => slug,
      "name" => name,
      "summary" => entry["summary"],
      "tasks" => tasks,
      "tags" => tags,
      "modalities" => modalities,
      "variantCount" => variants.size,
      "hero" => hero_url,
      "url" => "/#{kind}/#{slug}.html",
    }.compact
  end

  all_tasks = listing.flat_map { |e| e["tasks"] || [] }.uniq
  write_json(File.join(SEARCH_OUT, "#{kind}.json"), search)
  write_json(File.join(DATA_OUT, "#{kind}.json"), {
    "entries" => listing,
    "tasks" => all_tasks.sort,
    "taskGroups" => group_tasks_by_family(all_tasks, families),
    "modalities" => listing.flat_map { |e| e["modalities"] || [] }.uniq.sort,
  })
  puts "#{kind.ljust(9)} #{listing.size} entries"
end

# ---------------------------------------------------------------------------

FileUtils.mkdir_p([DOCS_OUT, MODELS_OUT, DATASETS_OUT, DATA_OUT, SEARCH_OUT])
task_families = load_task_families
write_json(File.join(DATA_OUT, "task_families.json"), task_families)
puts "families: #{task_families.size} task→family mappings"
stage_docs
stage_catalog(kind: "models",   src_dir: MODELS_SRC,   out_dir: MODELS_OUT,
              entries_key: "entries",  tasks_field: "tasks", families: task_families)
stage_catalog(kind: "datasets", src_dir: DATASETS_SRC, out_dir: DATASETS_OUT,
              entries_key: "datasets", tasks_field: "suitableForTasks", families: task_families)
puts "staged into #{SITE}"
