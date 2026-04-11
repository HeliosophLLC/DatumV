using DatumIngest.Catalog;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.Scans;

/// <summary>
/// One strategy for collecting absolute row positions from index-seekable
/// predicates inside a <see cref="ScanOperator"/> filter expression
/// (equality, BETWEEN, IN, composite-index leftmost-prefix). The strategies
/// run in series; each one submits zero or more candidate position lists to
/// the shared <see cref="SeekPlanner"/>, which retains the fewest-positions
/// winner for the executor to seek against.
/// </summary>
/// <remarks>
/// All strategies share a <see cref="SeekPlanningContext"/> that holds the
/// predicates extracted once via <see cref="PredicatePruningAnalyzer"/>;
/// strategies are stateless and read-only over that context.
/// </remarks>
internal interface ISeekStrategy
{
    /// <summary>
    /// Walks the supplied predicates and submits any matching candidate
    /// position lists to <paramref name="planner"/>.
    /// </summary>
    void Contribute(
        SeekPlanningContext predicates,
        ITableProvider provider,
        Schema schema,
        SeekPlanner planner,
        Arena arena);
}

/// <summary>
/// Pre-extracted top-level AND-chain predicates that all
/// <see cref="ISeekStrategy"/> implementations operate on. Built once per
/// <see cref="ScanOperator"/> execution so the expression tree is walked
/// only three times (one per predicate shape) regardless of how many
/// strategies consume the result.
/// </summary>
internal sealed class SeekPlanningContext
{
    public List<(string Column, DataValue Value)> Equalities { get; } = new();
    public List<(string Column, DataValue Low, DataValue High)> Betweens { get; } = new();
    public List<(string Column, List<DataValue> Values)> Ins { get; } = new();

    public SeekPlanningContext(Expression filter, Arena arena)
    {
        PredicatePruningAnalyzer.ExtractEqualities(filter, Equalities, arena);
        PredicatePruningAnalyzer.ExtractBetweens(filter, Betweens, arena);
        PredicatePruningAnalyzer.ExtractIns(filter, Ins, arena);
    }
}
