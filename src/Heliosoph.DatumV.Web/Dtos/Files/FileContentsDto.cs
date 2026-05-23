namespace Heliosoph.DatumV.Web.Dtos.Files;

// Catalog root location. Returned by GET /api/files/root; the renderer
// uses this as the default starting directory for the native save dialog
// when the user Ctrl+S's a scratch (unsaved) SQL tab.
public sealed record CatalogRootDto(string CatalogRoot);

// Body of GET /api/files/contents. Mtime lets the client detect external
// modifications on the next read (out-of-scope for v1 of the editor but
// cheap to surface now so we don't need to redo the contract later).
public sealed record FileContentsDto(string Contents, DateTimeOffset ModifiedAt);

// Body of PUT /api/files/contents. Single-shot write — the controller
// creates the file and any missing parent directories. Returns the
// post-write mtime via FileContentsResponseDto.
public sealed record FileContentsRequestDto(string Contents);

// Body of the PUT response — just the new mtime so the client can pin
// its in-memory baseline.
public sealed record FileContentsResponseDto(DateTimeOffset ModifiedAt);

// Body of POST /api/files/rename. Both paths are catalog-relative
// (forward slashes); traversal outside the catalog root is rejected
// with 400, and renames are refused when the destination already
// exists so the controller never silently overwrites user work.
public sealed record FileRenameRequestDto(string FromPath, string ToPath);
