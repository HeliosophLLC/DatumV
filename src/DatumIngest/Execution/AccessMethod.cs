namespace Heliosoph.DatumV.Execution;

/// <summary>
/// Primary data access methods for scan operators. Mutually exclusive.
/// </summary>
public enum AccessMethod
{
    /// <summary>Not a data-access operator.</summary>
    None,

    /// <summary>Full sequential read of all rows.</summary>
    TableScan,

    /// <summary>Sorted index traversal — produces ordered output without a separate sort.</summary>
    IndexScan,
}
