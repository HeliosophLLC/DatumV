using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// A literal whose payload has been pre-materialized into a <see cref="DataValue"/>
/// at plan time by <see cref="LiteralHoister"/>. Produced once per query; the
/// evaluator returns <see cref="Value"/> directly without re-encoding on every row.
///
/// Parser-produced <see cref="LiteralExpression"/> nodes are rewritten into this
/// form by a pass that runs between planning and execution. Non-inline string
/// literals get materialized into the query's long-lived store exactly once, so a
/// predicate like <c>WHERE col = 'long-string-literal'</c> evaluated over 21M
/// rows costs a single arena write instead of 21M.
/// </summary>
public sealed record LiteralValueExpression(DataValue Value) : Expression;
