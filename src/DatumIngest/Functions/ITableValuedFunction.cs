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
    /// Validates the call-site arguments and returns the output schema.
    /// Mirrors <see cref="IScalarFunction.ValidateArguments"/> for TVFs:
    /// implementations throw <see cref="FunctionArgumentException"/> on
    /// kind or value mismatches so the planner and language server surface
    /// errors before execution.
    /// </summary>
    /// <param name="argumentKinds">The data kinds of the call-site arguments.</param>
    /// <param name="constantArguments">
    /// One slot per argument: populated when the argument is a
    /// <see cref="Parsing.Ast.LiteralExpression"/> at plan time (which
    /// includes <c>$parameter</c> references — <see cref="Execution.ParameterBinder"/>
    /// substitutes those into literals before planning, so this fires for
    /// both literal-in-source and bound-parameter call shapes), and
    /// <c>null</c> when the argument is a column reference or any other
    /// expression whose value isn't knowable at plan time.
    /// TVFs whose output schema is determined by what they're about to
    /// read (FITS bintables, HDF5 datasets, Parquet columns, …) peek the
    /// file here and surface a real column schema. TVFs whose schema is
    /// fixed by signature ignore this parameter. Lifetime is bound to
    /// this call — implementations must not retain references past
    /// return.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for any plan-time I/O (file peek, schema
    /// introspection). TVFs that don't do plan-time work can ignore it.
    /// </param>
    /// <returns>The schema describing the columns each output row will contain.</returns>
    /// <exception cref="FunctionArgumentException">
    /// The argument kinds or values do not satisfy this function's requirements.
    /// </exception>
    Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        CancellationToken cancellationToken);

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
