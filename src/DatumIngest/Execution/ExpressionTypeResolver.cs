using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Infers the output <see cref="DataKind"/> of an AST <see cref="Expression"/>
/// by examining column references against a known source schema and resolving
/// function return types. Used for schema introspection (editor autocomplete)
/// without executing the query.
/// </summary>
public static class ExpressionTypeResolver
{
    /// <summary>
    /// Resolves the output data kind of an expression given a source schema
    /// and function registry. Returns <c>null</c> when the type cannot be
    /// determined statically (e.g. dynamic CAST targets, unknown columns).
    /// </summary>
    /// <param name="expression">The AST expression to analyze.</param>
    /// <param name="sourceSchema">The schema providing column type information.</param>
    /// <param name="functions">The function registry for resolving scalar function return types.</param>
    /// <returns>The inferred data kind, or <c>null</c> if it cannot be determined.</returns>
    public static DataKind? ResolveType(Expression expression, Schema sourceSchema, FunctionRegistry functions)
    {
        return expression switch
        {
            LiteralExpression literal => ResolveLiteral(literal),
            ColumnReference column => ResolveColumn(column, sourceSchema),
            BinaryExpression binary => ResolveBinary(binary, sourceSchema, functions),
            UnaryExpression unary => ResolveUnary(unary, sourceSchema, functions),
            FunctionCallExpression function => ResolveFunction(function, sourceSchema, functions),
            InExpression => DataKind.Scalar,
            BetweenExpression => DataKind.Scalar,
            IsNullExpression => DataKind.Scalar,
            CastExpression cast => ResolveCast(cast),
            CaseExpression caseExpr => ResolveCaseExpression(caseExpr, sourceSchema, functions),
            _ => null,
        };
    }

    private static DataKind? ResolveLiteral(LiteralExpression literal)
    {
        if (literal.Value is null)
        {
            return DataKind.Scalar;
        }

        return literal.Value switch
        {
            int or long or float or double => DataKind.Scalar,
            string => DataKind.String,
            bool => DataKind.Boolean,
            _ => null,
        };
    }

    private static DataKind? ResolveColumn(ColumnReference column, Schema sourceSchema)
    {
        // Try qualified name first, then unqualified.
        if (column.TableName is not null)
        {
            string qualifiedName = $"{column.TableName}.{column.ColumnName}";
            ColumnInfo? qualified = sourceSchema.FindColumn(qualifiedName);
            if (qualified is not null)
            {
                return qualified.Kind;
            }
        }

        ColumnInfo? info = sourceSchema.FindColumn(column.ColumnName);
        return info?.Kind;
    }

    private static DataKind? ResolveBinary(BinaryExpression binary, Schema sourceSchema, FunctionRegistry functions)
    {
        // Comparison and logical operators always produce Scalar (boolean result).
        if (IsComparisonOrLogical(binary.Operator))
        {
            return DataKind.Scalar;
        }

        // Arithmetic operators: find common kind of both operands.
        DataKind? leftKind = ResolveType(binary.Left, sourceSchema, functions);
        DataKind? rightKind = ResolveType(binary.Right, sourceSchema, functions);

        if (leftKind is null || rightKind is null)
        {
            return leftKind ?? rightKind;
        }

        // Duration ± Duration preserves Duration (do not widen to Scalar).
        if (leftKind.Value == DataKind.Duration && rightKind.Value == DataKind.Duration
            && binary.Operator is BinaryOperator.Add or BinaryOperator.Subtract)
        {
            return DataKind.Duration;
        }

        return TypeCoercion.FindCommonKind(leftKind.Value, rightKind.Value) ?? DataKind.Scalar;
    }

    private static DataKind? ResolveUnary(UnaryExpression unary, Schema sourceSchema, FunctionRegistry functions)
    {
        return unary.Operator switch
        {
            // NOT always produces a boolean (Scalar).
            UnaryOperator.Not => DataKind.Scalar,

            // Negate preserves the operand kind.
            UnaryOperator.Negate => ResolveType(unary.Operand, sourceSchema, functions) ?? DataKind.Scalar,
            _ => null,
        };
    }

    private static DataKind? ResolveFunction(FunctionCallExpression function, Schema sourceSchema, FunctionRegistry functions)
    {
        IScalarFunction? scalarFunction = functions.TryGetScalar(function.FunctionName);
        if (scalarFunction is null)
        {
            return null;
        }

        // Resolve argument kinds so we can call ValidateArguments.
        DataKind[] argumentKinds = new DataKind[function.Arguments.Count];
        bool allResolved = true;

        for (int index = 0; index < function.Arguments.Count; index++)
        {
            DataKind? kind = ResolveType(function.Arguments[index], sourceSchema, functions);
            if (kind is null)
            {
                allResolved = false;
                break;
            }

            argumentKinds[index] = kind.Value;
        }

        if (!allResolved)
        {
            return null;
        }

        try
        {
            return scalarFunction.ValidateArguments(argumentKinds);
        }
        catch (ArgumentException)
        {
            // If validation fails, the type cannot be determined.
            return null;
        }
    }

    private static DataKind? ResolveCast(CastExpression cast)
    {
        return ResolveCastTargetKind(cast.TargetType);
    }

    /// <summary>
    /// Resolves a CAST target type name to a DataKind. Accepts both enum names
    /// and common aliases ("bool", "time", "duration").
    /// </summary>
    internal static DataKind? ResolveCastTargetKind(string targetType)
    {
        // The target type is a string literal naming a DataKind member.
        if (Enum.TryParse<DataKind>(targetType, ignoreCase: true, out DataKind targetKind))
        {
            return targetKind;
        }

        // Accept common aliases that don't match enum names.
        if (string.Equals(targetType, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Boolean;
        }

        if (string.Equals(targetType, "time", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Time;
        }

        if (string.Equals(targetType, "duration", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Duration;
        }

        return null;
    }

    private static bool IsComparisonOrLogical(BinaryOperator op)
    {
        return op is BinaryOperator.Equal
            or BinaryOperator.NotEqual
            or BinaryOperator.LessThan
            or BinaryOperator.GreaterThan
            or BinaryOperator.LessThanOrEqual
            or BinaryOperator.GreaterThanOrEqual
            or BinaryOperator.And
            or BinaryOperator.Or
            or BinaryOperator.Like;
    }

    /// <summary>
    /// Resolves the output type of a CASE expression by finding the common type
    /// across all THEN branch results and the optional ELSE result.
    /// When the standard widening chain cannot unify two branch types and one of
    /// them is String, the non-String type wins (SQL Server-style precedence).
    /// String values are implicitly parsed to the target type at evaluation time.
    /// </summary>
    private static DataKind? ResolveCaseExpression(CaseExpression caseExpression, Schema sourceSchema, FunctionRegistry functions)
    {
        DataKind? commonKind = null;

        foreach (WhenClause whenClause in caseExpression.WhenClauses)
        {
            DataKind? branchKind = ResolveType(whenClause.Result, sourceSchema, functions);
            if (branchKind is null)
            {
                return null;
            }

            commonKind = commonKind is null
                ? branchKind
                : UnifyCaseBranchKinds(commonKind.Value, branchKind.Value);

            if (commonKind is null)
            {
                return null;
            }
        }

        if (caseExpression.ElseResult is not null)
        {
            DataKind? elseKind = ResolveType(caseExpression.ElseResult, sourceSchema, functions);
            if (elseKind is not null && commonKind is not null)
            {
                commonKind = UnifyCaseBranchKinds(commonKind.Value, elseKind.Value);
            }
        }

        return commonKind;
    }

    /// <summary>
    /// Unifies two CASE branch kinds. Tries the standard widening chain first;
    /// when that fails and one kind is String, applies SQL Server-style precedence
    /// by preferring the non-String kind (String values are parsed at runtime).
    /// </summary>
    internal static DataKind? UnifyCaseBranchKinds(DataKind kindA, DataKind kindB)
    {
        DataKind? common = TypeCoercion.FindCommonKind(kindA, kindB);
        if (common is not null)
        {
            return common;
        }

        // String + coercible type: prefer the non-String kind.
        if (kindA == DataKind.String && TypeCoercion.CanCoerceStringTo(kindB))
        {
            return kindB;
        }

        if (kindB == DataKind.String && TypeCoercion.CanCoerceStringTo(kindA))
        {
            return kindA;
        }

        return null;
    }
}
