# DatumIngest Roadmap

The following features are architecturally accounted for but deferred from V1:

- **GROUP BY / Aggregation**: COUNT, SUM, AVG, MIN, MAX, GROUP BY, HAVING
- **Spill-to-disk joins**: Grace hash join for datasets too large for memory
- **Adaptive batch sizing**: Auto-tune based on row size estimates and available memory
- **Excel provider**: Read .xlsx files (ITableProvider interface is ready)
- **UNION / INTERSECT / EXCEPT**: Set operations between query results
- **Window functions**: ROW_NUMBER, RANK, LAG, LEAD with OVER/PARTITION BY
- **User-defined functions**: Plugin DLL support via FunctionRegistry
- **Pipe mode**: Stream results to stdout as CSV/JSON/NDJSON
- ~~**Top-N bounded sort**: Bounded priority queue in `OrderByOperator` for ORDER BY + LIMIT without full materialization~~ ✅
- ~~**WHERE index seek**: Use sorted value indexes to fetch matching rows directly via `ISeekableTableProvider` instead of streaming and discarding — point lookups for high-selectivity equality predicates~~ ✅
- ~~**Range predicate index pruning**: Extend sorted index chunk pruning to handle `<`, `<=`, `>`, `>=`, `BETWEEN`, and `IN` (currently equality only)~~ ✅
- ~~**Seekable Parquet provider**: Implement `ISeekableTableProvider` on Parquet (row-group-level) to unlock index scan and WHERE index seek~~ ✅
- ~~**Seekable HDF5 provider**: Implement `ISeekableTableProvider` on HDF5 using PureHDF `HyperslabSelection` for partial dataset reads~~ ✅
- **Cost-based optimizer**: Replace greedy join heuristic with cost model
- ~~**Statistics-based partition pruning**: Skip row groups whose min/max statistics prove a predicate unsatisfiable~~ ✅
- ~~**Bloom filter acceleration**: Use Parquet bloom filters to skip partitions for equality predicates~~ ✅
- **Remote data sources**: HTTP/S3/Azure Blob providers
- ~~**Schema caching**: Skip re-inference on repeated queries~~ ✅
- **Data validation**: CHECK constraints / VALIDATE clause for data quality gates
- ~~**Language server (WASM)**: SQL autocomplete, diagnostics, hover via Blazor WebAssembly~~ ✅
- ~~**Language server (SignalR)**: SignalR hub for server-side language intelligence~~ ✅
- **Language server — VS Code LSP extension**: Wrap LanguageServer core in LSP JSON-RPC over stdio
- ~~**Language server — multi-error diagnostics**: Error-recovering parser for multiple parse errors per document~~ ✅
- **Language server — semantic diagnostics**: ~~Unknown table/column warnings~~, type mismatch detection ✅ (partial)
- ~~**Language server — WASM size optimization**: Extract `DatumIngest.Parsing` with manifest POCOs to eliminate all transitive heavy dependencies from LanguageServer/Wasm~~ ✅
