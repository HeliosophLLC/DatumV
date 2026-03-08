# Memory accounting and profiling

## Goal

Replace per-operator `MemoryEstimator` instances with a single batch- or query-scoped `MemoryAccountant` that every memory-consuming site reports to. Add a 1Hz `MemoryProfile` of (Row, Arena) byte counts that is both readable live (for a streaming memory-pressure indicator) and persisted as the post-mortem graph of a query's memory behavior.

## Why

Today every materializing operator (CTE, GroupBy, OrderBy, HashJoin build, Distinct, the five SetOps, Grace hash join's three phases) constructs its own `MemoryEstimator` and compares its **local** total against `context.MemoryBudgetBytes`. Three of them stacked in a plan each independently think the full budget is theirs alone — total residency reaches 240% of budget while every local check says "fine," and the query OOMs.

The intended behavior: if a GroupBy operator enters and the plan is already at 90% of budget because an upstream CTE is holding state, GroupBy should spill on its very first batch. That requires one shared residency counter every operator consults.

A second motivation: users running queries should be able to see live and post-hoc what their query actually consumed. Streaming a sparkline next to the result pane (and emitting the full sample series at query end) makes memory behavior diagnosable without an EXPLAIN ANALYZE detour.

## Non-goals

- Arena byte accounting. Anonymous and file-backed arenas are both `MemoryMappedFile`-backed; the OS can page them out under pressure. They don't compete with the GC-pinned heap that spilling can relieve. Tracked in the profile (the Arena bin) for diagnostics, not budgeted.
- Per-operator attribution stack on samples. Deferred to a follow-up — adds operator push/pop on every `ExecuteAsync` entry; the v1 graph shows totals only.
- Multi-arena enumeration in the Arena bin. v1 samples `context.Store.Position` only — operator-local arenas (`bufferArena` in OrderBy, SpillPartition's pool) aren't surfaced. Deferred until spill diagnostics demand it.
- Ring-buffer cap on the sample list for continuous queries. Deferred — at 24 bytes/sample × 3600 samples/hour = ~85 KB/hour, growth is bounded enough for v1.
- `Arena.AddReference()` / `Release()` refcounting (Phase 4 in the prior memo). Orthogonal.
- Cross-query process-wide budget. Phase 5 in the prior memo; orthogonal.

## Design

### `MemoryAccountant` — the unit of accounting

```csharp
public sealed class MemoryAccountant : IDisposable
{
    private long _residentBytes;
    public long? MemoryBudgetBytes { get; }
    public MemoryProfile Profile { get; }

    public long CurrentResidentBytes => Interlocked.Read(ref _residentBytes);
    public bool WouldExceedBudget(long delta = 0) =>
        MemoryBudgetBytes is long b && CurrentResidentBytes + delta > b;
    public void NotifyMaterialized(long delta) => Interlocked.Add(ref _residentBytes, delta);
    public void NotifyReleased(long delta)     => Interlocked.Add(ref _residentBytes, -delta);
    public void Dispose() { /* stop timer; leak-detect in debug */ }
}
```

`MemoryProfile`:

```csharp
public sealed class MemoryProfile
{
    private readonly List<MemorySample> _samples = [];
    private readonly Lock _lock = new();
    private MemorySample _latest;

    public void Append(long elapsedMs, long rowBytes, long arenaBytes);
    public MemorySample Latest => Volatile.Read(ref _latest);   // lock-free for UI polling
    public IReadOnlyList<MemorySample> Snapshot();              // copy under lock
}

public readonly record struct MemorySample(long ElapsedMs, long RowBytes, long ArenaBytes);
```

A `System.Threading.Timer` in the accountant ctor fires every 1000 ms, calling `Profile.Append(stopwatch.ElapsedMs, _residentBytes, store.Position)`. `Timer` is stopped on `Dispose`.

### Ownership

| Scope | Owns or borrows? | Lifetime |
|---|---|---|
| `BatchContext` (procedural batch) | Owns | Created at batch start, disposed at batch end. All queries + DML + `VariableScope` within the batch share. |
| `ExecutionContext` (standalone query, no batch) | Owns | Disposed with the context. |
| `ExecutionContext` (within a batch) | Borrows from `BatchContext` | Copy ctor / `WithOuterRow` / RowLimit-strip children also borrow. |
| `VariableScope` | Borrows from owning batch/query | New instrumentation site. |
| DML executors (`InsertExecutor`, `UpdateExecutor`, `DeleteExecutor`) | Borrows | Threaded through entry point. Standalone DML constructs a one-shot accountant; embedded DML shares the surrounding batch's. |

Two `ExecutionContext` ctors: primary (creates its own accountant) and accountant-borrowing (takes an existing one). A `_ownsAccountant` flag tells `Dispose` whether to dispose the accountant — same pattern as the `Store` arena's `AddReference` baseline.

### Per-operator deltas — structural arithmetic, no sampling

`MemoryEstimator`'s sampling machinery is gone. Each operator computes its own per-row delta from the structure it owns:

| Operator | Per-row delta |
|---|---|
| GroupBy `KeyedHashAggregator` | `20 * fieldCount` (DataValue refs) + `~48` (Dictionary slot) + `aggregateStateSize` |
| OrderBy heap/buffer insert | `20 * fieldCount` + key-array overhead |
| Distinct hash set insert | `20 * fieldCount` + `~40` (HashSet slot) |
| HashJoin build side | `20 * fieldCount` + dict overhead |
| CTE materialized cache | `20 * fieldCount` (rows in `List<Row>`) |
| SetOps materialized | depends on hash vs sort phase |
| Grace hash join's three phases | each phase computes its own per-row cost |

Spill checks become `if (context.WouldExceedBudget(forecastForNextBatch)) Spill();`. The "already over at entry" case is automatic: at operator entry, `CurrentResidentBytes` may already exceed budget — first check fires, operator spills its first batch.

### `VariableScope` instrumentation

The post-374a8996 design holds procedural `DECLARE`'d values as `ValueRef`s on `VariableScope` frames. Managed payloads (`float[]`, `byte[]`, `SKBitmap`) live across query boundaries within a procedural batch — 11 MB tensors stay resident from one `FOR` iteration to the next.

New hook points:

```csharp
public sealed class VariableScope
{
    private readonly MemoryAccountant _accountant;  // borrowed

    public void Declare(string name, ValueRef value, ...) {
        // ...existing code...
        _accountant.NotifyMaterialized(ManagedPayloadBytes(value));
    }
    public void Set(string name, ValueRef value) {
        // walk frames, find existing
        _accountant.NotifyReleased(ManagedPayloadBytes(existing.Value));
        _accountant.NotifyMaterialized(ManagedPayloadBytes(value));
    }
    public void PopFrame() {
        // sum frame bindings' payload bytes, NotifyReleased once
    }
}
```

`ManagedPayloadBytes(ValueRef)` discriminates ValueRef shapes:
- Inline scalar / arena-backed: `0` (arena bytes are file-backed; inline lives inside the ValueRef)
- `ValueRef.Materialized` holding `float[]` / `byte[]` / `int[]`: `array.Length * sizeof(element)`
- `ValueRef.Materialized` holding `SKBitmap`: `bitmap.ByteCount`
- Other reference payloads: case-by-case (image / audio / video / model state)

### DML plumbing

Three sub-cases:
1. **DML invoked from a procedural batch** — `BatchContext` is in scope; pass its `MemoryAccountant` through. `VariableScope` notifications flow into the same accountant naturally.
2. **DML invoked as a top-level statement** — construct a one-shot `MemoryAccountant` analogous to the standalone-query case; or use `MemoryAccountant.NoOp` if the statement is bounded enough that we don't care.
3. **DML with an embedded SELECT** (`INSERT INTO t SELECT ...`, `UPDATE t SET ... WHERE ...`) — the SELECT side already goes through `ExecutionContext`; the DML wrapper shares its accountant with the embedded context.

Held state to watch:
- `INSERT INTO ... SELECT`: streaming, held state lives in the SELECT operators (already accounted).
- `UPDATE` / `DELETE`: if they buffer the row-set before applying, that buffer is held state — needs `Notify*`.

## Effort

| # | PR | Files touched | LOC | Effort | Confidence |
|---|---|---|---|---|---|
| 1 | **Accounting foundation** — `MemoryAccountant` + `MemoryProfile` + 1Hz sampling + wire through `BatchContext` and `ExecutionContext` + retrofit `using` at ownership sites | New `MemoryAccountant.cs`, new `MemoryProfile.cs`, `ExecutionContext.cs`, `BatchContext.cs`, `QueryPlan.cs`, `ServiceTestBase.cs`, ~30 test files, unit tests | +300 / -10 | 7–10 hrs | High |
| 2 | `VariableScope` instrumentation + `ManagedPayloadBytes` helper | `VariableScope.cs`, `ValueRef.cs`, new test | +120 / -10 | 3–4 hrs | Medium |
| 3 | DML accountant plumbing | `InsertExecutor.cs`, `UpdateExecutor.cs`, `DeleteExecutor.cs` (+ tests) | +80 / -10 | 3–4 hrs | Medium |
| 4 | Delete `MemoryEstimator`, migrate `KeyedHashAggregator` (GroupBy) as exemplar + regression test | `MemoryEstimator.cs` (delete), `KeyedHashAggregator.cs`, 1 new test | +120 / -200 | 4–6 hrs | Medium |
| 5 | Migrate `OrderByOperator` + `DistinctOperator` + `FoldScanOperator` | 3 operator files + tests | +80 / -150 | 3–4 hrs | High |
| 6 | Migrate `SetOperationOperator` (5 sites) + CTE + RecursiveCTE | 3 files + tests | +120 / -250 | 4–5 hrs | Medium |
| 7 | Migrate `GraceHashJoinExecutor` (3 phases) + `BuildSideMaterializer` | 2 files + tests | +90 / -180 | 3–4 hrs | Medium |
| 8 | Web UI live memory indicator | `ClientApp/state/queryMemory.ts`, sparkline component, locale strings | +200 / -10 | 4–6 hrs | Medium |

**Totals**: 31–43 hours / 5–8 focused half-days / ~1.5–2.5 weeks at one-PR-a-day cadence.

**Critical path**: PR 1 (the foundation) blocks everything else. Once it lands, PRs 2 (VariableScope), 3 (DML), 4–7 (operator migrations), and 8 (UI) can all proceed in parallel. PRs 4–7 share a migration pattern, so landing PR 4 first as the exemplar is recommended even if subsequent ones run concurrently.

## Regression test for the purpose

A query plan with two materializing operators stacked (e.g. CTE → GroupBy) under a budget large enough for either alone but not both. Pre-change: neither spills, OOM. Post-change: the second operator to enter the materializing phase sees `WouldExceedBudget` and spills immediately. Lands with PR 4.

A second test covers VariableScope: a procedural loop that `DECLARE`s a large managed payload per iteration, asserting `accountant.CurrentResidentBytes` rises on declare and falls on `PopFrame`. Lands with PR 2.

A third covers correlated subqueries: an outer materializing operator + a correlated subquery — both count against the same budget. Lands with PR 5 or 6.

## Risks

- **Per-operator structural overhead constants** (`DataValueOverheadBytes = 20`, dict-entry ~48) are educated estimates. If the .NET internals change shape or operators use non-`Dictionary` structures (e.g. custom hash tables), the deltas drift. Tunable per-operator since each computes its own delta.
- **Race window on spill decisions**: two threads in a parallel operator both see "under budget" simultaneously, both materialize past it. Small overshoot, documented assumption.
- **Test fan-out for `using` retrofit**: ~30 test files call `CreateExecutionContext()`. Mechanical change but every site needs eyeballing to confirm `await` boundaries don't dispose mid-collect.
- **`ManagedPayloadBytes` sizing for `SKBitmap` and custom payloads**: case-by-case. New ValueRef payload types need to extend the helper or their bytes go uncounted.

## Deferred follow-ups

- Per-operator attribution stack on samples (~2 hrs to add, retrofitting across operators).
- Multi-arena enumeration for the Arena bin (~3 hrs).
- Ring-buffer cap on `MemoryProfile._samples` for continuous queries (~1 hr).
- `system.query_memory_profile` virtual table joined to `system.query_history` (~6 hrs — needs DDL + virtual-table impl).
- `Arena.AddReference()` / `Release()` refcounting (Phase 4 in the prior memo, ~1 week, orthogonal).
- Cross-query process-wide budget (Phase 5 in the prior memo, orthogonal).
