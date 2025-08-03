using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Optional extension interface for table-valued functions whose output schema depends
/// on the element kind of one or more <see cref="DataKind.Array"/> arguments.
/// </summary>
/// <remarks>
/// When <see cref="DatumIngest.Catalog.QuerySchemaResolver"/> resolves a
/// <c>FROM function(...)</c> source and the function implements this interface,
/// it calls <see cref="GetOutputSchema(ReadOnlySpan{DataKind}, ReadOnlySpan{DataKind?})"/>
/// instead of the base <see cref="ISchemaAwareTableFunction.GetOutputSchema(ReadOnlySpan{DataKind})"/>,
/// supplying the per-argument array element kinds extracted from source column metadata.
/// This enables functions like <c>UNNEST</c> to produce correct plan-time output schemas
/// for typed <see cref="DataKind.Array"/> inputs (e.g. <c>Array&lt;Scalar&gt;</c>)
/// rather than falling back to a generic String column.
/// </remarks>
public interface IElementKindAwareTableFunction : ISchemaAwareTableFunction
{
    /// <summary>
    /// Returns the schema of the rows this function will produce, given argument kinds
    /// and array element kind metadata.
    /// </summary>
    /// <param name="argumentKinds">
    /// The data kinds of the arguments. Identical to the span passed to
    /// <see cref="ISchemaAwareTableFunction.GetOutputSchema(ReadOnlySpan{DataKind})"/>.
    /// </param>
    /// <param name="arrayElementKinds">
    /// For each position in <paramref name="argumentKinds"/>: the array element
    /// kind when <c>argumentKinds[i] == DataKind.Array</c> and the element kind
    /// is known at plan time; otherwise <c>null</c>.
    /// </param>
    /// <returns>The output schema describing the columns each row will contain.</returns>
    Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds, ReadOnlySpan<DataKind?> arrayElementKinds);
}
