# Bundle signatures — content-hash verification for model files

## Goal

Detect when a model file on disk doesn't match the bundle the engine (or the SQL author) was expecting. Catches three real failure modes:

1. **Wrong file downloaded** — user grabbed a different quantization, a different revision, or a similarly-named-but-different model. Loads "successfully" with subtly wrong outputs.
2. **File tampered with** — supply-chain attack, on-disk corruption, sync clobber. Same as above but adversarial.
3. **Built-in pinning drift** — engine declares "Mistral 7B v0.3 expects this hash"; user has v0.2 lingering from a previous install. Refuse and surface the mismatch.

The user's framing: *"if the headline is the app for running ML models at scale, the platform should at minimum tell me whether the bytes I think I'm running are the bytes I'm actually running."* It's the same shape of guarantee as `git status` against a tracked tree.

## Non-goals (for the first cut)

- Signing models cryptographically (PGP, sigstore). That's a layer above content hashing — once we have hashes flowing, signing is "verify the hash against a public key." Defer until there's a concrete distribution story.
- Per-tensor / per-layer hashing. The whole-bundle hash is the smallest unit users actually care about ("does my softmax.onnx match the one I expected?").
- Network-side hash distribution (model registry that hands out canonical hashes). Out of scope; this plan is the on-disk verification layer.

## Surface

### `system.models` columns (added)

| column | type | notes |
|---|---|---|
| `bundle_hash` | `String` | SHA-256 hex of the on-disk bundle. Computed at scan time, cached per `(path, mtime, size)`. NULL for synthetic backends with no file. |
| `expected_hash` | `String` | SHA-256 declared by the catalog entry (built-ins) or the CREATE MODEL `WITH (sha256 = '...')` clause (declared models). NULL when no expectation declared. |
| `signature_status` | `String` | One of: `verified` (hashes match), `mismatch` (declared but differs — refuses to load), `unsigned` (no `expected_hash` declared — loads, no guarantee), `error` (couldn't read the file to hash). |

### CREATE MODEL extension

```sql
CREATE MODEL classify(@img IMAGE) RETURNS INT32
USING 'classify.onnx'
WITH (sha256 = 'a3f2c891...')
AS BEGIN RETURN infer(@img) END
```

The `WITH (...)` clause is optional. Omitted = `unsigned`; present + mismatch at load = refuse with a clear error pointing at expected vs actual.

### Built-in `ModelCatalogEntry` extension

Add an `ExpectedSha256` field (nullable). Engine-baked entries declare the hash they were built against; mismatch at load refuses with a clear error.

## Hash semantics

For a single-session bundle (today's only shape), the hash is just SHA-256 of the file bytes.

For multi-session bundles (Florence-2 etc., when they land), the hash is SHA-256 over a canonical concatenation: each session sorted by name, `name + "\0" + file_bytes`, joined. This makes adding/removing/renaming a session change the hash, which is the intended behavior.

A Merkle tree (per-file hash + tree root) would let us surface per-file fingerprints in `system.models` too. Defer until the multi-session case is real and someone wants the granularity.

## Caching

Hashing a 5 GB model file is ~10–30 seconds. The cost is unacceptable on every `SELECT * FROM system.models` scan.

Cache key: `(absolute_path, file_mtime_ticks, file_size_bytes)`. Cache value: SHA-256 hex string. Cache scope: per-process, in-memory dictionary. Eviction: LRU with a generous cap (a few MB of cache holds thousands of entries — every model anyone might query against). Persist to `.datum-cache/bundle-hashes.json` on shutdown so a process restart doesn't repay the cost.

A scan against an unchanged file: dictionary lookup, no IO. A scan after the file changed: mtime mismatch invalidates, rehash. A scan against a missing file: `signature_status = error`, hash is NULL.

## Implementation sketch

1. **`BundleHasher`** — new type in `src/DatumIngest/Inference/`. Single-method API: `Task<string?> ComputeAsync(string path, CancellationToken ct)`. Internal cache + persistence.
2. **`ModelCatalogEntry.ExpectedSha256`** (nullable) — built-in entries declare what they were built against. Engine bakes hashes during model-zoo curation; updating a built-in's bundle bumps the hash too.
3. **`CreateModelStatement.OptionsClause`** — parser surface for `WITH (sha256 = '...')`. Generic `WITH (...)` shape so future options (timeout, version pin, etc.) don't need re-grammar work.
4. **`ApplyCreateModelAsync`** — after file-existence check, compute hash and compare to declared expectation. Mismatch throws before `LoadBundleAsync`; success stores both expected + actual on the descriptor.
5. **`ModelsTableProvider.FillRow`** — surface the three new columns. For built-ins, expected = entry's declared hash; for declared models, expected = descriptor's WITH-clause hash. Status derives from the comparison.

## Open questions

- **Hash cache location** — `.datum-cache/` next to the user's catalog, or `~/.datum/cache/` in the user's home? Per-catalog is cleaner for sandbox semantics; per-user shares the cost across catalogs that reference the same shared model file.
- **Multi-bundle semantics for built-ins** — a `ModelCatalogEntry` with multiple files (LLM + tokenizer + config). One hash over the canonical concat, or per-file hashes surfaced as `Array<Struct{file, hash}>`? Lean toward one hash for now; promote to per-file when the audit story demands it.
- **Hash-on-load vs hash-lazily** — load-time always-verify catches drift early; lazy-on-scan is cheaper but lets a mismatch run silently until someone queries `system.models`. Recommend load-time when `expected_hash` is declared (the user opted in), lazy when not (no expectation = no obligation to verify).
- **CREATE OR REPLACE MODEL** — does the new declaration's hash supersede, or is it a stricter check (refuse REPLACE if hash conflicts with prior bound bundle)? Probably supersedes; replacement is a deliberate user action.
- **`infer_compatibility` interaction** — should the toolkit's `infer_compatibility(path)` TVF report the file's hash as one of its rows? Probably yes; it's free once the hasher exists.
