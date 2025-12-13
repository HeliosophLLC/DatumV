namespace DatumIngest.Functions;

/// <summary>
/// Superseded by <see cref="ITableValuedFunction.ValidateArguments"/>, which is
/// now part of the base interface. Kept as an empty marker to avoid breaking any
/// external implementations; remove once all call sites have been updated.
/// </summary>
[Obsolete("Implement ITableValuedFunction.ValidateArguments directly. This interface will be removed in a future version.")]
public interface ISchemaAwareTableFunction : ITableValuedFunction { }
