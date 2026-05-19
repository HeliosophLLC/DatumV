using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// Seeks via <c>column BETWEEN low AND high</c> predicates. For each
/// extracted between, probes the corresponding sorted column index with
/// <c>FindRange(low, high)</c>.
/// </summary>
internal sealed class BetweenSeekStrategy : ISeekStrategy
{
    public void Contribute(
        SeekPlanningContext predicates,
        ITableProvider provider,
        Schema schema,
        SeekPlanner planner,
        Arena arena)
    {
        foreach ((string column, DataValue low, DataValue high) in predicates.Betweens)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            planner.SubmitEntries(index.FindRange(low, high));
        }
    }
}
