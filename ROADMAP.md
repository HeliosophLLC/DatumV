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
- **Cost-based optimizer**: Replace greedy join heuristic with cost model
- ~~**Statistics-based partition pruning**: Skip row groups whose min/max statistics prove a predicate unsatisfiable~~ ✅
- **Bloom filter acceleration**: Use Parquet bloom filters to skip partitions for equality predicates
- **Remote data sources**: HTTP/S3/Azure Blob providers
- **Schema caching**: Skip re-inference on repeated queries
- **Data validation**: CHECK constraints / VALIDATE clause for data quality gates
