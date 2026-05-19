# Memory Accountant

DatumV tracks the in-RAM residency of a running query (or procedural batch) through a single per-plan `MemoryAccountant`. Every materializing operator reports the bytes it holds, and every spill decision consults that one counter. This means the system can:

- Detect when a single statement would push a host past its budget and trigger spill *before* the OOM.
- Recognize that two materializing operators in the same plan cumulatively pressure the budget — a CTE holding 1.5 GiB and a downstream GROUP BY building a hash table both see the *same* counter, not 100% of the budget each.
- Stream live residency samples to the UI's status-bar memory chip during query execution and freeze them for post-mortem inspection when the query completes.

This document explains what the accountant tracks, what it doesn't, how the spill budget works, and how to read the live indicator.

## Two bins: Row vs Arena

The accountant exposes two distinct numbers in every sample:

### Row bytes — budgeted

**GC-resident** structures that pin .NET heap memory and can OOM the process. Includes:

- `DataValue[]` cells in operator hash tables, sort buffers, and materialized caches (~32 bytes per cell).
- Dictionary slot overhead (~48 bytes per entry) for hash-based operators.
- Managed payloads (`float[]`, `byte[]`, `SKBitmap`) held by `VariableScope` bindings across procedural-batch query boundaries.
- DML buffers (UPDATE `RowUpdateRequest` lists, DELETE matched-index lists, INSERT VALUES batches).

This is the number the spill budget compares against. When `WouldExceedBudget` returns true, the next operator that calls it spills.

### Arena bytes — informational

Bytes written into the per-query (and per-batch) arenas that hold string payloads, image bytes, struct/array slot data, and other non-inline `DataValue` content. Both anonymous and file-backed arenas are `MemoryMappedFile`-backed, so the OS pages their bytes out under memory pressure — they don't compete with the GC heap that spilling can relieve.

Arena bytes are recorded for diagnostics (the UI shows them in a separate sparkline under the Row bin) but **do not count toward the spill budget**. A query whose payload bytes balloon to 10 GiB but whose hash tables stay small will not spill — and shouldn't, because spilling can't help: the arena is already file-backed.

## The spill budget

Every batch executed via `BatchExecutor.RunWithEventsAsync` runs against a budget. The default is **2 GiB**:

```csharp
public const long DefaultMemoryBudgetBytes = 2L * 1024 * 1024 * 1024;
```

The default applies when no explicit budget is passed; callers that need different behavior pass their own value, or `long.MaxValue` for effectively unbounded execution. Standalone queries that bypass `BatchExecutor` (constructed directly via `catalog.CreateExecutionContext(...)`) can set `memoryBudgetBytes` on the factory call or leave it null for unbounded behavior.

### How operators consult the budget

Materializing operators check the accountant *before* adding to their held state:

```csharp
if (context.Accountant.WouldExceedBudget(perRowBytes))
{
    // Spill instead of adding the next row.
    spillCoordinator.BeginSpilling(...);
}
```

`WouldExceedBudget(delta)` reads the plan-wide `CurrentResidentBytes` and returns `true` if adding `delta` would push the total over `MemoryBudgetBytes`. Operators that already had a spill mechanism (GROUP BY, ORDER BY, DISTINCT, hash join, CTE, UNION DISTINCT / INTERSECT / EXCEPT, GraceHashJoin) route their trigger through this check.

### What spilling looks like per operator

| Operator | Spill behavior at budget |
|---|---|
| **GROUP BY** | New keys go to disk via the spill coordinator's hash-partitioned spiller; existing in-memory groups keep accumulating updates from new rows (the "hot key" side-channel). The in-memory hash table is released at operator dispose. |
| **ORDER BY** (unbounded) | The current chunk is sorted and written as a sorted run; the in-memory buffer + its arena reset; subsequent rows accumulate into the fresh buffer. K-way merge across runs at emit time. |
| **DISTINCT** | The current row remains in the in-memory set + emits; subsequent rows hash-partition to disk; drain phase replays partitions against a partition-local seen set seeded from the in-memory keys. |
| **Hash join** (build side, Grace) | The largest in-memory partition is spilled when the budget triggers. Probe rows for spilled partitions also spill so the join can recurse on (build, probe) pairs that fit. |
| **CTE (materialized)** | All cached batches transfer to the spill coordinator; future input batches stream directly through the spiller. Replay reads back from disk for downstream references. |
| **UNION DISTINCT / INTERSECT / EXCEPT** | Same hash-partitioned spill pattern as DISTINCT, with separate left/right spillers for binary set ops. |
| **FOLD/SCAN** | No spill mechanism. Throws an `ExecutionException` when the budget triggers, rather than silently OOMing. |

### Released-on-disposal

Once an operator's held state is no longer in memory (spilled or operator complete), the accountant counter drops back down. The chip's sparkline reflects this — a GROUP BY building toward the budget shows the line climbing, then plateauing when spill kicks in, then dropping when the operator finishes emitting.

## The live indicator

Memory samples flow from the server to the Web UI's status-bar memory chip during query execution. Two emission paths combine into one stream:

- **Sidecar timer.** A background `PeriodicTimer` in `BatchExecutor.RunWithEventsAsync` ticks once per second and emits a `memory_sample` event regardless of row cadence. This is what populates the chip during long operators that don't yield rows for minutes (large GROUP BY accumulation, ORDER BY external sort).
- **Cell boundary samples.** Immediate samples fire at cell start (so the chip appears before the first 1Hz tick) and at cell completion (so the post-mortem value freezes the final state).

Both paths serialize through one `SemaphoreSlim` inside the executor so events arrive on the wire in emission order alongside row events.

### Reading the chip

The chip lives in the bottom-right area of the results pane's status bar:

```
✓ Done                42 MB ▁▂▃▅▄ 21% │ 00:01.234 │ 1,234 rows
```

- The **byte number** is `CurrentResidentBytes` (the Row bin only).
- The **sparkline** shows the last samples' Row bytes — useful for spotting growing-vs-stable-vs-just-spilled patterns.
- The **percentage** is `CurrentResidentBytes / MemoryBudgetBytes`. Hidden when no budget is set.
- Color shifts: muted at <80%, amber at ≥80%, red at ≥100%. After the query completes the chip dims but stays clickable.

Clicking the chip opens a popover with both sparklines (Row + Arena), the budget threshold dashed in red, and a current / peak / budget triplet:

```
┌─ Memory ──────────────────────────────────┐
│   ROW (budgeted)                          │
│  ▁▂▂▃▅▇█████████████████████ ← budget     │
│   ARENA (mmap, OS-paged)                  │
│  ▁▁▂▃▃▄▅▅▆▇▇█████████████████             │
│  current 184 MB   peak 192 MB   budget 200 MB │
└───────────────────────────────────────────┘
```

The Arena sparkline scales to its own data range (no shared y-axis with Row), since arena residency is informational and isn't comparable to a budget.

## How operators participate

Operators report into the accountant via two methods on `ExecutionContext.Accountant`:

```csharp
context.Accountant.NotifyMaterialized(deltaBytes);  // held state grew
context.Accountant.NotifyReleased(deltaBytes);      // held state shrank
```

Each call uses a structural per-row delta — a constant the operator computes from its schema (column count, key count, accumulator count). Example from `KeyedHashAggregator`:

```csharp
_perGroupBytes =
    DataValueOverheadBytes * groupByKeyCount    // 32 × keyCount
    + PerGroupOverheadBytes                     // 64 (Dict slot + GroupState)
    + PerAccumulatorBytes * aggregateCount;     // 32 × aggregateCount
```

Operators never sample row contents to estimate row size — sampling was the previous design and proved more expensive than the structural approximation it replaced, with no real accuracy gain (the dominant cost is structural overhead, not the row's data bytes which mostly live in the arena anyway).

Release happens at operator scope end. For most operators that's the `finally` block of `ExecuteAsync`; for GroupBy it's after the emit + drain phases have finished and the hash table arrays return to the pool.

## Procedural batches and VariableScope

A procedural batch (`BatchExecutor.ExecuteAsync` on a multi-statement script) holds one accountant for the lifetime of the batch — *not* one per query inside it. Every query the batch runs constructs an `ExecutionContext` that borrows the batch's accountant rather than constructing its own.

This means a DECLARE'd payload from one statement counts against the budget when the next statement runs:

```sql
DECLARE @tensor Float32[] = (SELECT embedding FROM big_table LIMIT 1);  -- 8 MiB
SELECT array_dot(@tensor, embedding) FROM other_table;  -- @tensor still counts
```

The 8 MiB tensor is materialized by the DECLARE and held on `VariableScope` until the procedural block ends. The SELECT in the next line runs against a context whose accountant already shows 8 MiB resident. A downstream GROUP BY in that SELECT sees the 8 MiB on the counter and spills 8 MiB sooner than it would in isolation.

`VariableScope` reports binding payload bytes via `ValueRef.ManagedPayloadBytes`:

- Inline scalars (int, bool, float, etc.): **0** bytes (no managed payload).
- Strings: `2 × length` bytes.
- Primitive arrays (`float[]`, `byte[]`, etc.): `length × sizeof(element)`.
- `SKBitmap`: `bitmap.ByteCount`.
- Struct fields / array elements (`ValueRef[]`): recursive sum.

The release happens when `PopFrame()` runs at the end of a `BEGIN … END` block, or when the entire batch's `VariableScope` is disposed.

## DML

INSERT, UPDATE, and DELETE executors track their buffered held state too. They take an optional `ExecutionContext?` parameter — when supplied, the executor reports its buffer growth into the batch's accountant. When called as a top-level statement outside a batch (the shell, ad-hoc Web request), each constructs a per-call accountant to satisfy the non-null requirement.

| DML | What's accounted |
|---|---|
| **INSERT VALUES** | One-shot row batch covering the VALUES list. Charged on rent, released after `session.WriteAsync` flushes. |
| **INSERT … SELECT** | Streams batch-by-batch through the source plan; the source-side `ExecutionContext` accounts its own pipeline state. The INSERT path itself holds no significant buffer. |
| **UPDATE** (simple) | A list of `RowUpdateRequest` objects accumulated during the scan + a per-request `Dictionary<int, DataValue>` mapping touched columns to new values. Released after `provider.UpdateRowsAsync` completes. |
| **UPDATE … FROM** | A list of stabilized source rows (the right side of the nested-loop join) + a `Dictionary<long, Dictionary<int, DataValue>>` of matched-rows-to-update. Both released at end. |
| **DELETE** | A list of matched live-row indices (8 bytes each). Released after `provider.DeleteRows` returns. |

DML doesn't currently spill — when buffering exceeds the budget, the UPDATE or DELETE proceeds anyway (a future enhancement could spill the request list). The accounting still happens, so the budget is visible to upstream / downstream operators stacked under the same batch.

## What isn't tracked

The accountant deliberately excludes some byte sources to keep the model honest:

- **Arena payload bytes.** As described above: mmap-backed, OS-paged, can't be spilled.
- **`SpillPartition` internal arenas.** Spill operators stage rows through their own `_arenaPool` arena before flushing to disk. Those bytes are transient (per-batch) and rolled into the spiller's consolidated arena, which is file-backed. Not budgeted.
- **Operator-local `bufferArena` instances** (e.g. OrderBy's sort buffer). These hold the stable copies of buffered rows during sort + spill, and are file-backed too. The DataValue cells referencing them *are* budgeted (the 32 bytes × fieldCount × rowCount); the bytes the cells point to in the arena are not.
- **Pool-rented `Row` and `DataValue[]` arrays.** These are GC-resident but amortized via the pool. A held row reports its DataValue overhead (the 32 × fieldCount) but doesn't separately account for the array's `byte[]` storage — that's pool infrastructure.
- **Per-cell streaming response buffers** (the NDJSON writer's buffer in the Web stream service). Negligible and per-batch.

If a future operator needs to budget bytes that fall outside this model, it should add explicit `Notify*` calls at the materialization point and document the per-row cost. The `DistinctAccumulatorDecorator` is one example — it tracks HashSet slot overhead for DISTINCT-modified aggregates using the same structural estimate (`32 + 48` per entry).

## Configuration

The budget is set at one layer:

- **`TableCatalog.CreateExecutionContext` factory.** `catalog.CreateExecutionContext(memoryBudgetBytes: ...)` when constructing a context outside a batch. Ignored if an existing accountant is passed via the `accountant` parameter — the accountant carries its own budget.

`MemoryAccountant.MemoryBudgetBytes` is null when no budget is configured. In that case `WouldExceedBudget` always returns false (no spill triggering from this path), but `NotifyMaterialized` / `NotifyReleased` still update the counter so the live profile remains accurate.

## Disposing the accountant

`MemoryAccountant` implements `IDisposable` and stops its 1Hz sampling timer on disposal. The owning scope disposes the accountant when it itself is disposed. Derived contexts (`context.Derive(...)`, `context.WithRowLimit(...)`) borrow the parent's accountant and skip the dispose — only the owner cleans up.

In tests that construct `MemoryAccountant` directly, the `using` pattern ensures the timer (if started via `StartProfiling`) stops cleanly. Accountants that never start profiling have no Timer to clean and can be left to the GC.

## See also

- [Execution Plans](execution-plans.md) — operator-level explanations and EXPLAIN output.
- [Architecture](architecture.md) — high-level engine overview.
- [SQL: GROUP BY](../sql/group-by.md), [Set Operations](../sql/set-operations.md), [CTEs](../sql/cte.md) — the materializing operators that spill against this budget.
