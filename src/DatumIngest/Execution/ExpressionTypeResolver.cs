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
        // The target type is a string literal naming a DataKind member.
        if (Enum.TryParse<DataKind>(cast.TargetType, ignoreCase: true, out DataKind targetKind))
        {
            return targetKind;
        }

        // Accept common aliases that don't match enum names.
        if (string.Equals(cast.TargetType, "bool", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Boolean;
        }

        if (string.Equals(cast.TargetType, "time", StringComparison.OrdinalIgnoreCase))
        {
            return DataKind.Time;
        }

        if (string.Equals(cast.TargetType, "duration", StringComparison.OrdinalIgnoreCase))
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
}
