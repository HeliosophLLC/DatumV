using System.Globalization;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Functions;

/// <summary>
/// Canonicalises a parsed SQL <see cref="Expression"/> from a parameter's
/// <c>CHECK (…)</c> clause into one of the typed <see cref="ParameterCheck"/>
/// subclasses. Recognised shapes (with the parameter name as the free
/// variable bound to the parameter):
/// <list type="bullet">
///   <item><description><c>x BETWEEN a AND b</c> → <see cref="BetweenCheck"/></description></item>
///   <item><description><c>(x &gt;= a) AND (x &lt;= b)</c> → <see cref="BetweenCheck"/></description></item>
///   <item><description><c>x &gt; a</c> / <c>x &gt;= a</c> → <see cref="GreaterThanCheck"/></description></item>
///   <item><description><c>x &lt; a</c> / <c>x &lt;= a</c> → <see cref="LessThanCheck"/></description></item>
///   <item><description><c>x IN (v1, v2, …)</c> → <see cref="InCheck"/></description></item>
/// </list>
/// Anything that doesn't match falls back to <see cref="CustomCheck"/> with
/// the original expression. Comparison operands may appear on either side
/// (<c>a &lt;= x</c> is treated the same as <c>x &gt;= a</c>).
/// </summary>
/// <remarks>
/// <para>
/// <strong>Literal kind coercion.</strong> Bounds parse as whatever the
/// numeric literal naturally produces (small integers as <c>Int8</c>,
/// fractions as <c>Float64</c>). The walker promotes everything to
/// <see cref="decimal"/> so the typed check shapes are independent of the
/// authored literal type — <c>BETWEEN 0 AND 1</c> and
/// <c>BETWEEN 0.0 AND 1.0</c> produce identical canonical forms.
/// </para>
/// <para>
/// <strong>What the walker does NOT do.</strong> It does not verify that
/// the parameter name actually appears in the expression — a
/// <c>CHECK (1 = 1)</c> would canonicalise to a CustomCheck. Slice C's
/// scalar validation path treats CustomCheck as "validated elsewhere"
/// (the SQL evaluator path) and lets the expression through. Authors who
/// write structurally non-canonical CHECKs get the CustomCheck fallback,
/// which is fine — it's the documented escape hatch.
/// </para>
/// </remarks>
public static class ParameterCheckWalker
{
    /// <summary>
    /// Canonicalises <paramref name="expr"/> into a typed <see cref="ParameterCheck"/>
    /// or returns <see cref="CustomCheck"/> wrapping the original expression when no
    /// canonical shape matches. <paramref name="paramName"/> is the parameter's name
    /// (case-insensitive match against any unqualified <see cref="ColumnReference"/>
    /// inside the expression).
    /// </summary>
    public static ParameterCheck Canonicalise(Expression expr, string paramName)
    {
        if (TryBetween(expr, paramName, out ParameterCheck? between)) return between;
        if (TryConjunctionAsBetween(expr, paramName, out ParameterCheck? andBetween)) return andBetween;
        if (TryComparison(expr, paramName, out ParameterCheck? cmp)) return cmp;
        if (TryIn(expr, paramName, out ParameterCheck? inCheck)) return inCheck;
        return new CustomCheck(expr);
    }

    private static bool TryBetween(Expression expr, string paramName, out ParameterCheck result)
    {
        if (expr is BetweenExpression { Negated: false } b
            && IsParamRef(b.Expression, paramName)
            && TryDecimal(b.Low, out decimal lo)
            && TryDecimal(b.High, out decimal hi))
        {
            result = new BetweenCheck(lo, hi);
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryConjunctionAsBetween(Expression expr, string paramName, out ParameterCheck result)
    {
        // Recognise (paramName >= lo AND paramName <= hi) — produced by authors
        // who don't reach for BETWEEN, and by Slice D's parser for compound
        // CHECKs that compile through the normal binary-expression path.
        if (expr is BinaryExpression { Operator: BinaryOperator.And } and
            && TryOneSided(and.Left, paramName, out bool leftIsLower, out decimal leftBound, out bool leftInclusive)
            && TryOneSided(and.Right, paramName, out bool rightIsLower, out decimal rightBound, out bool rightInclusive)
            && leftIsLower != rightIsLower
            && leftInclusive && rightInclusive)
        {
            decimal lo = leftIsLower ? leftBound : rightBound;
            decimal hi = leftIsLower ? rightBound : leftBound;
            result = new BetweenCheck(lo, hi);
            return true;
        }
        result = null!;
        return false;
    }

    private static bool TryComparison(Expression expr, string paramName, out ParameterCheck result)
    {
        if (TryOneSided(expr, paramName, out bool isLower, out decimal bound, out bool inclusive))
        {
            result = isLower
                ? new GreaterThanCheck(bound, inclusive)
                : new LessThanCheck(bound, inclusive);
            return true;
        }
        result = null!;
        return false;
    }

    /// <summary>
    /// Recognises one-sided bound shapes: <c>x &gt; a</c>, <c>x &gt;= a</c>,
    /// <c>a &lt; x</c>, <c>a &lt;= x</c>, and the mirror inequalities. Outputs
    /// <paramref name="isLowerBound"/> = true when the bound restricts from below
    /// (i.e., maps to <see cref="GreaterThanCheck"/>), false for upper bound
    /// (maps to <see cref="LessThanCheck"/>).
    /// </summary>
    private static bool TryOneSided(
        Expression expr,
        string paramName,
        out bool isLowerBound,
        out decimal bound,
        out bool inclusive)
    {
        isLowerBound = false;
        bound = 0m;
        inclusive = false;

        if (expr is not BinaryExpression bin) return false;
        BinaryOperator op = bin.Operator;
        bool paramOnLeft;
        if (IsParamRef(bin.Left, paramName) && TryDecimal(bin.Right, out decimal rhs))
        {
            paramOnLeft = true;
            bound = rhs;
        }
        else if (IsParamRef(bin.Right, paramName) && TryDecimal(bin.Left, out decimal lhs))
        {
            paramOnLeft = false;
            bound = lhs;
        }
        else
        {
            return false;
        }

        switch (op)
        {
            case BinaryOperator.GreaterThan:
                isLowerBound = paramOnLeft;
                inclusive = false;
                return true;
            case BinaryOperator.GreaterThanOrEqual:
                isLowerBound = paramOnLeft;
                inclusive = true;
                return true;
            case BinaryOperator.LessThan:
                isLowerBound = !paramOnLeft;
                inclusive = false;
                return true;
            case BinaryOperator.LessThanOrEqual:
                isLowerBound = !paramOnLeft;
                inclusive = true;
                return true;
            default:
                return false;
        }
    }

    private static bool TryIn(Expression expr, string paramName, out ParameterCheck result)
    {
        if (expr is InExpression { Negated: false } inExpr
            && IsParamRef(inExpr.Expression, paramName))
        {
            List<string> values = new(inExpr.Values.Count);
            for (int i = 0; i < inExpr.Values.Count; i++)
            {
                if (!TryStringValue(inExpr.Values[i], out string s))
                {
                    result = null!;
                    return false;
                }
                values.Add(s);
            }
            result = new InCheck(values);
            return true;
        }
        result = null!;
        return false;
    }

    private static bool IsParamRef(Expression expr, string paramName) =>
        expr is ColumnReference { TableName: null } col
        && string.Equals(col.ColumnName, paramName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Best-effort literal → decimal coercion. Handles the numeric primitives
    /// the lexer actually produces for unsuffixed numeric literals. Negation
    /// is folded through the unary-minus wrapper because the parser keeps
    /// unary minus as a separate AST node rather than baking it into the
    /// literal's value.
    /// </summary>
    private static bool TryDecimal(Expression expr, out decimal value)
    {
        if (expr is UnaryExpression { Operator: UnaryOperator.Negate } neg
            && TryDecimal(neg.Operand, out decimal inner))
        {
            value = -inner;
            return true;
        }
        if (expr is LiteralExpression { Value: object boxed })
        {
            switch (boxed)
            {
                case sbyte sb: value = sb; return true;
                case short s16: value = s16; return true;
                case int i32: value = i32; return true;
                case long i64: value = i64; return true;
                case byte u8: value = u8; return true;
                case ushort u16: value = u16; return true;
                case uint u32: value = u32; return true;
                case ulong u64: value = u64; return true;
                case float f32: value = (decimal)f32; return true;
                case double f64: value = (decimal)f64; return true;
                case decimal dec: value = dec; return true;
                case string str when decimal.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out decimal parsed):
                    value = parsed;
                    return true;
            }
        }
        value = 0m;
        return false;
    }

    /// <summary>
    /// Best-effort literal → string coercion for IN-list members. Strings
    /// pass through verbatim; numbers stringify via invariant culture so
    /// the canonical form matches the runtime comparison path in
    /// <see cref="InCheck.Validate"/>.
    /// </summary>
    private static bool TryStringValue(Expression expr, out string value)
    {
        if (expr is UnaryExpression { Operator: UnaryOperator.Negate } neg
            && TryDecimal(neg.Operand, out decimal inner))
        {
            value = (-inner).ToString(CultureInfo.InvariantCulture);
            return true;
        }
        if (expr is LiteralExpression { Value: object boxed })
        {
            if (boxed is string s)
            {
                value = s;
                return true;
            }
            if (TryDecimal(expr, out decimal d))
            {
                value = d.ToString(CultureInfo.InvariantCulture);
                return true;
            }
        }
        value = null!;
        return false;
    }
}
