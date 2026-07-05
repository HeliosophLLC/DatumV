/* Client-side search for the DatumV site. One module, two modes:
 *
 *   sidebar (docs)  — ranked hit list in the sidebar; hides the folder tree
 *                     while a query is active.
 *   filter (models/ — text search + facet chips filter the card grid in
 *   datasets)         place, reordering by relevance.
 *
 * MiniSearch (vendored UMD) provides the index; field boosts mirror the
 * in-app views (name/title heaviest, then headings/tags, then body).
 */
(function () {
  "use strict";

  var MiniSearch = window.MiniSearch;
  if (!MiniSearch) return;

  var HL_START = "";
  var HL_END = "";

  function escapeHtml(s) {
    return String(s).replace(/[&<>"]/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" }[c];
    });
  }

  // Turn sentinel-wrapped text into <mark> spans (input already escaped).
  function renderMarks(text) {
    return escapeHtml(text)
      .split(HL_START).join("<mark>")
      .split(HL_END).join("</mark>");
  }

  function snippet(content, terms) {
    if (!content) return "";
    var lower = content.toLowerCase();
    var at = -1;
    for (var i = 0; i < terms.length; i++) {
      var idx = lower.indexOf(terms[i].toLowerCase());
      if (idx >= 0 && (at < 0 || idx < at)) at = idx;
    }
    var start = at < 0 ? 0 : Math.max(0, at - 50);
    var slice = content.slice(start, start + 180);
    if (start > 0) slice = "… " + slice;
    // Wrap term hits with sentinels, then render.
    terms.forEach(function (term) {
      if (!term) return;
      var re = new RegExp("(" + term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&") + ")", "ig");
      slice = slice.replace(re, HL_START + "$1" + HL_END);
    });
    return slice;
  }

  var FIELDS = {
    docs: { fields: ["title", "name", "headings", "content"], boost: { title: 5, name: 5, headings: 3, content: 1 }, store: ["title", "url", "folders", "content"] },
    models: { fields: ["name", "summary", "tasks", "tags", "content", "description"], boost: { name: 5, tasks: 3, tags: 3, summary: 2, content: 1 }, store: ["name", "url"] },
    datasets: { fields: ["name", "summary", "modalities", "tasks", "content", "description"], boost: { name: 5, modalities: 3, tasks: 3, summary: 2, content: 1 }, store: ["name", "url"] },
  };

  function buildIndex(section, docs) {
    var cfg = FIELDS[section] || FIELDS.docs;
    var mini = new MiniSearch({
      fields: cfg.fields,
      storeFields: cfg.store,
      searchOptions: { boost: cfg.boost, prefix: true, fuzzy: 0.15, combineWith: "AND" },
    });
    mini.addAll(docs);
    return mini;
  }

  function initSidebar(root, section, indexUrl) {
    var input = root.querySelector("[data-search-input]");
    var results = root.querySelector("[data-search-results]");
    var tree = root.querySelector("[data-tree]");
    if (!input || !results) return;

    var mini = null;
    fetch(indexUrl).then(function (r) { return r.json(); }).then(function (docs) {
      mini = buildIndex(section, docs);
      if (input.value.trim()) run();
    });

    function run() {
      var q = input.value.trim();
      if (!q || !mini) {
        results.hidden = true;
        results.innerHTML = "";
        if (tree) tree.hidden = false;
        return;
      }
      var hits = mini.search(q).slice(0, 40);
      var terms = hits.length ? hits[0].terms : [q];
      results.innerHTML = hits.length
        ? hits.map(function (h) {
            return (
              '<a class="hit" href="' + h.url + '">' +
              '<span class="hit-title">' + escapeHtml(h.title) + "</span>" +
              (h.folders && h.folders.length ? '<span class="hit-path"> ' + escapeHtml(h.folders.join("/")) + "</span>" : "") +
              '<span class="hit-snippet">' + renderMarks(snippet(h.content || "", terms)) + "</span>" +
              "</a>"
            );
          }).join("")
        : '<p class="search-empty">No matches.</p>';
      results.hidden = false;
      if (tree) tree.hidden = true;
    }

    input.addEventListener("input", run);
    input.addEventListener("keydown", function (e) {
      if (e.key === "Escape") { input.value = ""; run(); }
    });
  }

  function initFilter(root, section, indexUrl) {
    var input = root.querySelector("[data-search-input]");
    var grid = root.querySelector("[data-grid]");
    var countNote = root.querySelector("[data-count]");
    if (!grid) return;

    var cards = Array.prototype.slice.call(grid.querySelectorAll("[data-slug]"));
    var facets = {}; // group -> Set of active values
    var mini = null;

    if (input && indexUrl) {
      fetch(indexUrl).then(function (r) { return r.json(); }).then(function (docs) {
        mini = buildIndex(section, docs);
        apply();
      });
    }

    function matchesFacets(card) {
      for (var group in facets) {
        if (!facets[group] || facets[group].size === 0) continue;
        var values = (card.getAttribute("data-" + group) || "").split(" ");
        var ok = false;
        facets[group].forEach(function (v) { if (values.indexOf(v) >= 0) ok = true; });
        if (!ok) return false;
      }
      return true;
    }

    function apply() {
      var q = input ? input.value.trim() : "";
      var rank = null;
      if (q && mini) {
        rank = {};
        mini.search(q).forEach(function (h, i) { rank[h.id] = i; });
      }
      var shown = 0;
      cards.forEach(function (card) {
        var slug = card.getAttribute("data-slug");
        var textOk = !rank || rank[slug] !== undefined;
        var facetOk = matchesFacets(card);
        var visible = textOk && facetOk;
        card.style.display = visible ? "" : "none";
        card.style.order = rank && rank[slug] !== undefined ? rank[slug] : 0;
        if (visible) shown++;
      });
      if (countNote) countNote.textContent = shown + (shown === 1 ? " result" : " results");
    }

    if (input) {
      input.addEventListener("input", apply);
      input.addEventListener("keydown", function (e) {
        if (e.key === "Escape") { input.value = ""; apply(); }
      });
    }

    root.querySelectorAll("[data-facet-group]").forEach(function (btn) {
      var group = btn.getAttribute("data-facet-group");
      var value = btn.getAttribute("data-facet-value");
      facets[group] = facets[group] || new Set();
      btn.addEventListener("click", function () {
        var pressed = btn.getAttribute("aria-pressed") === "true";
        btn.setAttribute("aria-pressed", pressed ? "false" : "true");
        if (pressed) facets[group].delete(value); else facets[group].add(value);
        apply();
      });
    });

    apply();
  }

  function boot() {
    document.querySelectorAll("[data-search-sidebar]").forEach(function (root) {
      initSidebar(root, root.getAttribute("data-search-sidebar"), root.getAttribute("data-index"));
    });
    document.querySelectorAll("[data-search-filter]").forEach(function (root) {
      initFilter(root, root.getAttribute("data-search-filter"), root.getAttribute("data-index"));
    });
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
