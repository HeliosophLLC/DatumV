using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions;

/// <summary>
/// Interface for table-valued functions that produce a stream of rows
/// (e.g. <c>RANGE</c>).
/// </summary>
public interface ITableValuedFunction
{
    /// <summary>The SQL function name (case-insensitive matching).</summary>
    string Name { get; }

    /// <summary>
    /// Validates argument kinds and returns the output schema. Mirrors
    /// <see cref="IScalarFunction.ValidateArguments"/> for TVFs: implementations
    /// throw <see cref="FunctionArgumentException"/> on kind mismatches so the
    /// planner and language server surface errors before execution.
    /// </summary>
    /// <param name="argumentKinds">The data kinds of the call-site arguments.</param>
    /// <returns>The schema describing the columns each output row will contain.</returns>
    /// <exception cref="FunctionArgumentException">
    /// The argument kinds do not satisfy this function's requirements.
    /// </exception>
    Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds);

    /// <summary>
    /// Executes the function and yields rows asynchronously. Arguments are
    /// pre-evaluated once by the operator before this call; implementations
    /// should rent output batches via <paramref name="context"/>.
    /// </summary>
    /// <param name="arguments">The evaluated argument values.</param>
    /// <param name="context">
    /// The execution context supplying the pool, cancellation token, and
    /// query-level arena for batch rental.
    /// </param>
    /// <returns>An async stream of row batches produced by the function.</returns>
    IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context);
}
