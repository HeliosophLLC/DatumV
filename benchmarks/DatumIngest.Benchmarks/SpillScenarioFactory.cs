using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Benchmarks;

/// <summary>
/// Keyed data-value tier under test in the spill benchmarks. Drives the
/// per-row encoding the key column lands in (inline / arena / sidecar) so the
/// per-comparison cost — which the round-2 hash optimisation targets — is
/// directly attributable to the tier.
/// </summary>
public enum KeyTier
{
    /// <summary>
    /// Short ASCII string that fits inside the 16-byte inline DataValue
    /// payload. Hash is computed on the fly from the inline bytes (single-stripe
    /// XxHash64, effectively free). Baseline: best-case Equals path.
    /// </summary>
    InlineShortString,

    /// <summary>
    /// Medium-length ASCII string (~64 bytes) stamped into an arena. Hash is
    /// cached in <c>_p4+_p5</c>, so Equals goes through the cached-hash fast
    /// path today. Baseline: should already perform well.
    /// </summary>
    ArenaMediumString,

    /// <summary>
    /// 256-element <c>float[]</c> stored in an arena via
    /// <see cref="DataValue.FromArenaArray{T}"/>. No cached hash today; the
    /// round-2 redesign will stamp a 32-bit hash that this tier directly
    /// benefits from.
    /// </summary>
    ArenaVector,

    /// <summary>
    /// Large string written to a sidecar blob source (~16 KB). Comparison
    /// today fetches the bytes; round 2 should short-circuit on hash mismatch.
    /// <b>Not yet wired</b>; placeholder for the follow-up sidecar harness.
    /// </summary>
    SidecarLargeString,

    /// <summary>
    /// PNG image (256×256 RGB) written to a sidecar. Headline tier for the
    /// round-2 win: today every Equals fetches bytes; round 2 should resolve
    /// most comparisons via the inline 32-bit hash slot in <c>_p6</c>.
    /// <b>Not yet wired</b>; placeholder for the follow-up sidecar harness.
    /// </summary>
    SidecarImage,
}

/// <summary>
/// Builds in-memory tables of synthesized rows for the spill benchmarks.
/// </summary>
/// <remarks>
/// Each table has a single key column whose encoding is driven by
/// <see cref="KeyTier"/>, plus a tiny scalar payload column so the row width
/// isn't pathological. Cardinality defaults to ~10% distinct keys so most
/// rows hit existing groups in GroupBy/Distinct (which exercises the
/// dictionary equality hot path the hash gate targets).
/// </remarks>
internal static class SpillScenarioFactory
{
    private const string KeyColumnName = "k";
    private const string PayloadColumnName = "v";

    private static readonly string[] Columns = [KeyColumnName, PayloadColumnName];

    /// <summary>
    /// Materialises a single-table dataset. The two output columns are
    /// <c>k</c> (key, tier-encoded) and <c>v</c> (Int32 payload, distinct per row).
    /// </summary>
    public static (TableCatalog Catalog, string TableName) BuildSingleTable(
        Pool pool,
        KeyTier tier,
        int rowCount,
        int distinctKeys,
        int seed = 12345,
        string tableName = "data")
    {
        object?[][] rows = GenerateRows(tier, rowCount, distinctKeys, seed);
        TableCatalog catalog = new(pool);
        catalog.Add(new InMemoryTableProvider(pool, tableName, Columns, rows));
        return (catalog, tableName);
    }

    /// <summary>
    /// Materialises two tables suitable for HashJoin / Intersect benchmarks.
    /// Both share the key column name <c>k</c> with overlapping but not
    /// identical key sets so the join produces a non-empty, non-degenerate
    /// result. <c>distinctKeys</c> controls the left side; right has
    /// <c>distinctKeys/2</c> with the same encoding.
    /// </summary>
    public static (TableCatalog Catalog, string LeftName, string RightName) BuildJoinTables(
        Pool pool,
        KeyTier tier,
        int rowCount,
        int distinctKeys,
        int seed = 12345)
    {
        object?[][] leftRows = GenerateRows(tier, rowCount, distinctKeys, seed);
        object?[][] rightRows = GenerateRows(tier, rowCount, Math.Max(2, distinctKeys / 2), seed + 1);
        TableCatalog catalog = new(pool);
        catalog.Add(new InMemoryTableProvider(pool, "lhs", Columns, leftRows));
        catalog.Add(new InMemoryTableProvider(pool, "rhs", Columns, rightRows));
        return (catalog, "lhs", "rhs");
    }

    /// <summary>
    /// Produces raw-CLR <c>object?[]</c> rows. The provider re-stamps these into
    /// the scan-time arena when <c>ScanOperator</c> reads them, so each scan
    /// resolves against the right store — avoiding the cross-arena DataValue
    /// hazard that <see cref="InMemoryTableProvider"/> guards against.
    /// </summary>
    private static object?[][] GenerateRows(KeyTier tier, int rowCount, int distinctKeys, int seed)
    {
        if (distinctKeys < 1) distinctKeys = 1;
        if (distinctKeys > rowCount) distinctKeys = rowCount;

        object[] keyPool = BuildKeyPool(tier, distinctKeys);

        Random rng = new(seed);
        object?[][] rows = new object?[rowCount][];
        for (int i = 0; i < rowCount; i++)
        {
            rows[i] = [keyPool[rng.Next(distinctKeys)], i];
        }

        return rows;
    }

    /// <summary>
    /// Builds the per-tier distinct-key pool as raw CLR objects. The provider
    /// converts these to <c>DataValue</c>s with the right encoding (inline
    /// short string → inline tier, longer string → arena tier, <c>float[]</c>
    /// → Float32 arena array).
    /// </summary>
    private static object[] BuildKeyPool(KeyTier tier, int distinctKeys)
    {
        object[] pool = new object[distinctKeys];

        switch (tier)
        {
            case KeyTier.InlineShortString:
                for (int i = 0; i < distinctKeys; i++)
                {
                    // ≤16 byte UTF-8 → provider stores inline.
                    pool[i] = $"k{i:D4}";
                }
                break;

            case KeyTier.ArenaMediumString:
                for (int i = 0; i < distinctKeys; i++)
                {
                    // ~64-byte string → provider arena-stamps it with cached hash.
                    pool[i] = "key-" + new string('x', 56) + $"-{i:D4}";
                }
                break;

            case KeyTier.ArenaVector:
                for (int i = 0; i < distinctKeys; i++)
                {
                    // Distinct content per key — only the first slot varies
                    // so the rest of the buffer pays a real byte-compare cost.
                    float[] vector = new float[256];
                    vector[0] = i;
                    pool[i] = vector;
                }
                break;

            case KeyTier.SidecarLargeString:
            case KeyTier.SidecarImage:
                throw new NotImplementedException(
                    $"KeyTier.{tier} requires a sidecar-backed IBlobSource harness; " +
                    "wire SidecarRegistry + file-backed catalog before enabling.");

            default:
                throw new ArgumentOutOfRangeException(nameof(tier), tier, "Unknown KeyTier.");
        }

        return pool;
    }
}
