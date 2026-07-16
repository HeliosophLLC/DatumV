using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions;

/// <summary>
/// Superseded by <see cref="ITableValuedFunction.ValidateArguments(ReadOnlySpan{DataKind}, ReadOnlySpan{DataValue?}, IValueStore, CancellationToken)"/>, which is
/// now part of the base interface. Kept as an empty marker to avoid breaking any
/// external implementations; remove once all call sites have been updated.
/// </summary>
[Obsolete("Implement ITableValuedFunction.ValidateArguments directly. This interface will be removed in a future version.")]
public interface ISchemaAwareTableFunction : ITableValuedFunction { }
