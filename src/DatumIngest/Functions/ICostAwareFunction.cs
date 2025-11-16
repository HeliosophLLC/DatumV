using DatumIngest.Model;

namespace DatumIngest.Functions;

/// <summary>
/// Optional interface for scalar functions that incur resolution-dependent costs
/// beyond their fixed <see cref="IScalarFunction.QueryUnitCost"/>. The evaluator
/// calls <see cref="ComputeSupplementalCost(ReadOnlySpan{DataValue}, DataValue, in InvocationFrame)"/>
/// after execution and adds the result to the <see cref="DatumIngest.Execution.QueryMeter"/>.
/// </summary>
/// <remarks>
/// Only functions whose real cost varies with input size need to implement this.
/// The canonical example is image functions where pixel count determines actual work.
/// Functions with fixed per-invocation cost (the vast majority) do not implement this
/// interface — their flat <see cref="IScalarFunction.QueryUnitCost"/> is sufficient.
/// </remarks>
public interface ICostAwareFunction
{
    /// <summary>
    /// Normalization constant: one supplemental Query Unit per this many pixels.
    /// Shared between runtime metering and pre-execution manifest-based estimation.
    /// </summary>
    public const long PixelsPerQueryUnit = 100_000;

    /// <summary>
    /// Computes the supplemental Query Unit cost for a completed invocation,
    /// based on the actual arguments and result (e.g. decoded image dimensions).
    /// </summary>
    /// <param name="arguments">The evaluated argument values passed to the function.</param>
    /// <param name="result">The value returned by the function.</param>
    /// <returns>
    /// The supplemental QU cost to add on top of <see cref="IScalarFunction.QueryUnitCost"/>.
    /// Returns zero when the input is too small to warrant additional charge.
    /// </returns>
    long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result);

    /// <summary>
    /// Frame-aware cost computation. Use this overload when arguments may be sidecar-
    /// backed or arena-backed — the frame supplies the source store and sidecar registry
    /// needed to read payload bytes for the cost calculation. The default implementation
    /// falls back to the legacy <see cref="ComputeSupplementalCost(ReadOnlySpan{DataValue}, DataValue)"/>
    /// which only handles inline values.
    /// </summary>
    long ComputeSupplementalCost(ReadOnlySpan<DataValue> arguments, DataValue result, in InvocationFrame frame)
        => ComputeSupplementalCost(arguments, result);
}
