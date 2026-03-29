using DatumIngest.Execution;

namespace DatumIngest.Functions;

/// <summary>
/// Marker interface implemented by scalar functions whose body is a single
/// inline-metadata read from <see cref="DatumIngest.Model.DataValue"/>'s
/// per-kind payload bytes (e.g. <c>image_width</c>, <c>video_height</c>,
/// <c>point_cloud_count</c>). Implementing this signals to the planner-time
/// elider that the call site can be rewritten as
/// <see cref="InlineAccessorExpression"/>, eliminating the
/// <see cref="IScalarFunction.ExecuteAsync"/> dispatch on the common path.
/// </summary>
/// <remarks>
/// <para>
/// The function's regular <see cref="IScalarFunction.ExecuteAsync"/>
/// remains the source of truth for behaviour — the elision is a fast path
/// that the evaluator also delegates back to when the inline metadata
/// reads as the "unstamped" zero sentinel and the function needs to fall
/// back to a full decode. Implementations therefore continue to ship the
/// full decode fallback in <c>ExecuteAsync</c>.
/// </para>
/// <para>
/// Adding a function to the elision set is a two-step opt-in: implement
/// this interface (one-line <see cref="Field"/> getter) and add the
/// matching <see cref="InlineAccessorField"/> enum value + descriptor.
/// </para>
/// </remarks>
public interface IInlineMetadataAccessor : IScalarFunction
{
    /// <summary>
    /// Identifies which inline-metadata field this function reads from
    /// the argument's <see cref="DatumIngest.Model.DataValue"/> payload.
    /// The elider uses this to construct the
    /// <see cref="InlineAccessorExpression"/> replacement node.
    /// </summary>
    InlineAccessorField Field { get; }
}
