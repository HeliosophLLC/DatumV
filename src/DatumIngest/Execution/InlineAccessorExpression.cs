using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Post-plan AST node introduced by the inline-metadata accessor elider in
/// place of <see cref="FunctionCallExpression"/> calls to functions that
/// implement <see cref="DatumIngest.Functions.IInlineMetadataAccessor"/>.
/// The evaluator handles this node by reading the target field directly
/// off the argument's <see cref="DatumIngest.Model.DataValue"/> payload,
/// bypassing <see cref="DatumIngest.Functions.IScalarFunction.ExecuteAsync"/>
/// dispatch on the common (stamped) path.
/// </summary>
/// <param name="Argument">The single media-typed argument expression.</param>
/// <param name="Field">Which inline-metadata field to read.</param>
/// <remarks>
/// <para>
/// On the fallback path (inline metadata reads as the unstamped zero
/// sentinel), the evaluator looks the original function up by
/// <see cref="InlineAccessorDescriptors.Descriptor.FunctionName"/> and
/// delegates to its <see cref="DatumIngest.Functions.IScalarFunction.ExecuteAsync"/>
/// so the slow-path decode behaviour is preserved bit-for-bit with the
/// pre-elision call.
/// </para>
/// <para>
/// The node is structurally equatable (record equality) on
/// <c>(Argument, Field)</c>, which is the contract
/// <see cref="CommonSubexpressionEliminator"/> relies on to deduplicate
/// repeated accessor calls in <c>WHERE</c>/<c>SELECT</c>/<c>ORDER BY</c>.
/// </para>
/// </remarks>
public sealed record InlineAccessorExpression(
    Expression Argument,
    InlineAccessorField Field) : Expression;
