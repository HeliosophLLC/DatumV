using DatumIngest.Execution;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Interface for window (analytical) SQL functions that compute a value
/// for each row based on a partition of rows and optional ordering.
/// Unlike <see cref="IAggregateFunction"/>, window functions produce
/// one output value per input row rather than collapsing groups.
/// </summary>
public interface IWindowFunction
{
    /// <summary>The SQL function name (case-insensitive matching).</summary>
    string Name { get; }

    /// <summary>
    /// Validates the argument types and returns the result <see cref="DataKind"/>.
    /// </summary>
    /// <param name="argumentKinds">The kinds of the arguments being passed.</param>
    /// <returns>The kind of the result value.</returns>
    /// <exception cref="ArgumentException">The argument types are not valid for this function.</exception>
    DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>
    /// Creates a new computation instance for computing window function
    /// results over a partition of rows.
    /// </summary>
    IWindowComputation CreateComputation();
}

/// <summary>
/// Computes the results of a window function across all rows in a single partition.
/// Created by <see cref="IWindowFunction.CreateComputation"/> for each partition.
/// The entire sorted partition is provided so the computation can access any row
/// (necessary for ranking, offset, and framed aggregate functions).
/// </summary>
public interface IWindowComputation
{
    /// <summary>
    /// Computes the window function result for every row in a sorted partition.
    /// </summary>
    /// <param name="partitionRows">The rows in this partition, sorted by the window ORDER BY.</param>
    /// <param name="argumentExpressions">The function argument expressions to evaluate per row.</param>
    /// <param name="evaluator">The expression evaluator for resolving argument expressions against rows.</param>
    /// <param name="orderByItems">The ORDER BY items from the window specification, used by ranking functions to detect ties.</param>
    /// <param name="frame">The resolved window frame, or <see langword="null"/> for whole-partition semantics.</param>
    /// <param name="results">
    /// Pre-allocated array of the same length as <paramref name="partitionRows"/>.
    /// The computation must write exactly one <see cref="DataValue"/> per row.
    /// </param>
    /// <param name="nullHandling">
    /// Whether to skip or include NULLs when locating the target value.
    /// Used by value window functions (FIRST_VALUE, LAST_VALUE, NTH_VALUE).
    /// </param>
    /// <param name="fromLast">
    /// When <see langword="true"/>, NTH_VALUE counts from the end of the frame
    /// instead of the beginning.
    /// </param>
    /// <param name="cancellationToken">Cooperative cancellation token.</param>
    ValueTask ComputeAsync(
        IReadOnlyList<Row> partitionRows,
        IReadOnlyList<Expression> argumentExpressions,
        ExpressionEvaluator evaluator,
        IReadOnlyList<OrderByItem>? orderByItems,
        WindowFrame? frame,
        DataValue[] results,
        NullHandling nullHandling = NullHandling.RespectNulls,
        bool fromLast = false,
        CancellationToken cancellationToken = default);
}
