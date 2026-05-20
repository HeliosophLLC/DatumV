using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Indexing;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Execution.Operators.Scans;

/// <summary>
/// Seeks via <c>column IN (v1, v2, ...)</c> predicates. For each extracted
/// IN, probes the corresponding sorted column index once per value and
/// unions the position lists into a single candidate.
/// </summary>
internal sealed class InSeekStrategy : ISeekStrategy
{
    public void Contribute(
        SeekPlanningContext predicates,
        ITableProvider provider,
        Schema schema,
        SeekPlanner planner,
        Arena arena)
    {
        foreach ((string column, List<DataValue> values) in predicates.Ins)
        {
            if (!provider.TryGetColumnIndex(column, out IColumnIndex? index))
            {
                continue;
            }

            List<long> positions = new();
            foreach (DataValue value in values)
            {
                positions.AddRange(planner.BuildPositions(index.FindExact(value)));
            }

            planner.Submit(positions);
        }
    }
}
