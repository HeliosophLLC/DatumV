namespace DatumIngest.Web.Dtos.Files;

// Response payload for GET /api/files: every file under the catalog root,
// classified by kind and joined against the manifest so orphans are flagged.
// One row per file — the Project Explorer panel converts the flat list into
// a directory tree client-side.
//
// Mirrors the `system.files` virtual table's row shape (see
// SystemFilesProvider). A separate REST endpoint exists because the panel
// loads once on open + refreshes on CatalogHub events; routing every refresh
// through the query stream would be heavier than this single round trip.
public sealed record FilesDto(IReadOnlyList<FileEntryDto> Files);

// One file under the catalog root.
//
// `Kind` taxonomy (mirrors SystemFilesProvider):
//   data         — `.datum` table file
//   data_sidecar — `.datum-pkindex`, `.datum-cindex-*`, `.datum-fts-*`, `.datum-blob`, `.datum-manifest`
//   udf          — `udfs/<schema>/<name>.sql`
//   procedure    — `procedures/<schema>/<name>.sql`
//   model        — user-authored `models/<name>.sql`
//   manifest     — `.datum-catalog.json`
//   gitignore    — `.gitignore` / `.gitattributes`
//   other        — anything else (README, user-deposited files)
public sealed record FileEntryDto(
    // Catalog-relative path with forward slashes. Empty path is impossible.
    string Path,
    string Kind,
    // Parsed from path when the kind has one; null otherwise.
    string? Schema,
    // Filename stem for sql/datum kinds; null otherwise.
    string? Name,
    long SizeBytes,
    // UTC last-write time.
    DateTimeOffset ModifiedAt,
    // True when `Kind` is a managed kind (udf/procedure/model/data) and no
    // manifest entry references this path — surfaces orphan files left
    // behind by a crash or hand-edit.
    bool IsOrphan);
