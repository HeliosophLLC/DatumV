namespace DatumIngest.Indexing;

/// <summary>
/// Identifies the type of a section within a v5 unified memory-mapped <c>.datum-index</c> file.
/// Each section is a contiguous block of bytes located via the section directory that
/// immediately follows the fixed-size file header.
/// </summary>
internal enum UnifiedIndexSectionType : byte
{
    /// <summary>Source file fingerprint (size + striped SHA-256 hash) for staleness detection.</summary>
    Fingerprint = 0,

    /// <summary>Maps table indexes to table names within a multi-table index.</summary>
    TableDirectory = 1,

    /// <summary>Cached schema (column names, kinds, nullability) and total row count.</summary>
    Schema = 2,

    /// <summary>Per-chunk boundaries and per-column min/max/null/cardinality zone maps.</summary>
    ChunkDirectory = 3,

    /// <summary>Per-column, per-chunk bloom filters with uniform-size bitsets for O(1) access.</summary>
    BloomFilters = 4,

    // SortedIndexes (5) and BTreePages (6) retired in v8 (PR13d) — per-column
    // acceleration moved to companion `.datum-bptree-{col}` page-COW files
    // owned by the table provider. Numeric tags 5 and 6 are reserved (do not
    // reuse) so older inspectors fail loudly instead of silently mis-decoding.

    /// <summary>Per-column bitmap indexes for low-cardinality columns with compressed per-value, per-chunk bitsets.</summary>
    BitmapIndexes = 7,
}
