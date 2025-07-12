using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Extended interface for table-valued functions that can describe their
/// output schema without execution. Enables schema introspection for
/// editor autocomplete and query validation scenarios.
/// </summary>
public interface ISchemaAwareTableFunction : ITableValuedFunction
{
    /// <summary>
    /// Returns the schema of the rows this function will produce,
    /// given the kinds of the arguments being passed.
    /// </summary>
    /// <param name="argumentKinds">The data kinds of the arguments.</param>
    /// <returns>The output schema describing the columns each row will contain.</returns>
    Schema GetOutputSchema(ReadOnlySpan<DataKind> argumentKinds);
}
