using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution.Operators.BatchPredicates;

/// <summary>
/// Compiles a predicate AST into an <see cref="IBatchPredicate"/> when the
/// shape matches a v1-supported pattern. Returns <see langword="null"/> for
/// any shape the v1 compiler doesn't recognise — callers fall back to the
/// per-row evaluator path. The v1 set covers <c>column OP literal</c> (and
/// the mirrored <c>literal OP column</c>) for Float32 / Int32 / Int64
/// columns with the six standard comparisons.
/// </summary>
/// <remarks>
/// Compilation is intentionally cheap: walk the AST, resolve column ordinal
/// from a sample row, return a pre-bound predicate object. The "compile"
/// step has no IL emission or LINQ-Expressions tree building — that's a
/// later round once we see whether interpretation overhead matters. For now
/// the win is purely from collapsing per-row dispatch into a tight
/// monomorphic inner loop.
/// </remarks>
internal static class BatchPredicateCompiler
{
    /// <summary>
    /// Tries to compile <paramref name="predicate"/> against the schema of
    /// <paramref name="sampleRow"/>. Returns the compiled predicate on
    /// success, or <see langword="null"/> when the AST has a shape the v1
    /// compiler doesn't handle — caller must fall back to per-row.
    /// </summary>
    public static IBatchPredicate? TryCompile(Expression predicate, Row sampleRow)
    {
        if (predicate is not BinaryExpression binary)
        {
            return null;
        }

        if (!TryGetBatchComparison(binary.Operator, out BatchComparison op))
        {
            return null;
        }

        // Try column OP literal.
        if (TryResolveColumn(binary.Left, sampleRow, out int leftOrdinal, out DataKind leftKind)
            && TryResolveLiteral(binary.Right, out object? rightValue))
        {
            return BuildScalarPredicate(leftOrdinal, leftKind, rightValue, op);
        }

        // Try literal OP column — flip the operator and treat the column as the left side.
        if (TryResolveLiteral(binary.Left, out object? leftValue)
            && TryResolveColumn(binary.Right, sampleRow, out int rightOrdinal, out DataKind rightKind))
        {
            return BuildScalarPredicate(rightOrdinal, rightKind, leftValue, FlipOp(op));
        }

        return null;
    }

    private static bool TryGetBatchComparison(BinaryOperator op, out BatchComparison batchOp)
    {
        switch (op)
        {
            case BinaryOperator.Equal: batchOp = BatchComparison.Equal; return true;
            case BinaryOperator.NotEqual: batchOp = BatchComparison.NotEqual; return true;
            case BinaryOperator.LessThan: batchOp = BatchComparison.LessThan; return true;
            case BinaryOperator.LessThanOrEqual: batchOp = BatchComparison.LessEqual; return true;
            case BinaryOperator.GreaterThan: batchOp = BatchComparison.GreaterThan; return true;
            case BinaryOperator.GreaterThanOrEqual: batchOp = BatchComparison.GreaterEqual; return true;
            default:
                batchOp = default;
                return false;
        }
    }

    /// <summary>
    /// Inverts <paramref name="op"/> for the literal-OP-column rewrite:
    /// <c>5 &lt; x</c> means the same as <c>x &gt; 5</c>. Equal / NotEqual are
    /// commutative; the others swap.
    /// </summary>
    private static BatchComparison FlipOp(BatchComparison op) => op switch
    {
        BatchComparison.LessThan => BatchComparison.GreaterThan,
        BatchComparison.LessEqual => BatchComparison.GreaterEqual,
        BatchComparison.GreaterThan => BatchComparison.LessThan,
        BatchComparison.GreaterEqual => BatchComparison.LessEqual,
        _ => op,
    };

    /// <summary>
    /// Resolves a column-reference expression against <paramref name="sampleRow"/>
    /// to its ordinal + <see cref="DataKind"/>. Mirrors the qualified/unqualified
    /// lookup the evaluator does at run time, but performed once at compile time
    /// so the inner loop only needs an integer ordinal.
    /// </summary>
    private static bool TryResolveColumn(Expression expression, Row sampleRow, out int ordinal, out DataKind kind)
    {
        ordinal = -1;
        kind = default;
        if (expression is not ColumnReference colRef)
        {
            return false;
        }

        IReadOnlyDictionary<string, int> nameIndex = sampleRow.ColumnLookup.NameIndex;
        if (colRef.QualifiedName is not null && nameIndex.TryGetValue(colRef.QualifiedName, out ordinal))
        {
            kind = sampleRow[ordinal].Kind;
            return true;
        }
        if (nameIndex.TryGetValue(colRef.ColumnName, out ordinal))
        {
            kind = sampleRow[ordinal].Kind;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Extracts the underlying CLR value from a literal-shaped expression.
    /// Handles both <see cref="LiteralExpression"/> (parser-fresh) and
    /// <see cref="LiteralValueExpression"/> (hoisted by LiteralHoister into
    /// a pre-materialised DataValue).
    /// </summary>
    private static bool TryResolveLiteral(Expression expression, out object? value)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                value = literal.Value;
                return true;
            case LiteralValueExpression hoisted:
                value = UnwrapDataValue(hoisted.Value);
                return value is not null;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>
    /// Unwraps a <see cref="DataValue"/> back to its boxed CLR scalar for the
    /// kinds the v1 compiler supports. NULL or unsupported kinds return null,
    /// telling the caller to fall back to the per-row path.
    /// </summary>
    private static object? UnwrapDataValue(DataValue dv)
    {
        if (dv.IsNull) return null;
        return dv.Kind switch
        {
            DataKind.Int8 => (object)dv.AsInt8(),
            DataKind.Int16 => (object)dv.AsInt16(),
            DataKind.Int32 => (object)dv.AsInt32(),
            DataKind.Int64 => (object)dv.AsInt64(),
            DataKind.Float32 => (object)dv.AsFloat32(),
            DataKind.Float64 => (object)dv.AsFloat64(),
            _ => null,
        };
    }

    /// <summary>
    /// Dispatches to the concrete scalar predicate based on the column kind +
    /// literal type. Performs the CLR coercion needed so the inner loop's
    /// comparison is single-type (e.g. <c>WHERE value &gt; 500</c> on a
    /// Float32 column promotes the int literal to float at compile time;
    /// the inner loop is then float vs float, branch-free).
    /// </summary>
    private static IBatchPredicate? BuildScalarPredicate(int ordinal, DataKind columnKind, object? literal, BatchComparison op)
    {
        if (literal is null) return null;

        switch (columnKind)
        {
            case DataKind.Float32:
                if (TryCoerceToFloat(literal, out float floatLit))
                {
                    return new Float32ColumnVsLiteralPredicate(ordinal, floatLit, op);
                }
                return null;

            case DataKind.Int32:
                if (TryCoerceToInt32(literal, out int int32Lit))
                {
                    return new Int32ColumnVsLiteralPredicate(ordinal, int32Lit, op);
                }
                return null;

            case DataKind.Int64:
                if (TryCoerceToInt64(literal, out long int64Lit))
                {
                    return new Int64ColumnVsLiteralPredicate(ordinal, int64Lit, op);
                }
                return null;

            default:
                return null;
        }
    }

    private static bool TryCoerceToFloat(object literal, out float value)
    {
        switch (literal)
        {
            case float f: value = f; return true;
            case double d: value = (float)d; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case short s: value = s; return true;
            case sbyte b: value = b; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryCoerceToInt32(object literal, out int value)
    {
        switch (literal)
        {
            case int i: value = i; return true;
            case short s: value = s; return true;
            case sbyte b: value = b; return true;
            // Long → int32 only when in range; out-of-range falls back to per-row
            // (which would also produce a runtime error, but that's correctness-preserving).
            case long l when l >= int.MinValue && l <= int.MaxValue: value = (int)l; return true;
            default: value = 0; return false;
        }
    }

    private static bool TryCoerceToInt64(object literal, out long value)
    {
        switch (literal)
        {
            case long l: value = l; return true;
            case int i: value = i; return true;
            case short s: value = s; return true;
            case sbyte b: value = b; return true;
            default: value = 0; return false;
        }
    }
}
