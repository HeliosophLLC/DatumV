using DatumIngest.Model;

namespace DatumIngest.Execution.Operators.BatchPredicates;

/// <summary>
/// Batch predicate for <c>column OP literal</c> where the column is Float32
/// and the literal is a single Float32. Six concrete inner loops, one per
/// comparison operator — the operator is hoisted out of the inner loop with
/// a single switch so the inner body stays branch-free + monomorphic.
/// </summary>
internal sealed class Float32ColumnVsLiteralPredicate : IBatchPredicate
{
    private readonly int _ordinal;
    private readonly float _literal;
    private readonly BatchComparison _op;

    public Float32ColumnVsLiteralPredicate(int ordinal, float literal, BatchComparison op)
    {
        _ordinal = ordinal;
        _literal = literal;
        _op = op;
    }

    public void Evaluate(RowBatch batch, Span<bool> mask)
    {
        int n = batch.Count;
        float lit = _literal;
        int ord = _ordinal;

        switch (_op)
        {
            case BatchComparison.Equal:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() == lit;
                }
                break;
            case BatchComparison.NotEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() != lit;
                }
                break;
            case BatchComparison.LessThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() < lit;
                }
                break;
            case BatchComparison.LessEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() <= lit;
                }
                break;
            case BatchComparison.GreaterThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() > lit;
                }
                break;
            case BatchComparison.GreaterEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsFloat32() >= lit;
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported BatchComparison: {_op}");
        }
    }
}

/// <summary>
/// Batch predicate for <c>column OP literal</c> where the column is Int32 and
/// the literal is Int32. Same shape as <see cref="Float32ColumnVsLiteralPredicate"/>.
/// </summary>
internal sealed class Int32ColumnVsLiteralPredicate : IBatchPredicate
{
    private readonly int _ordinal;
    private readonly int _literal;
    private readonly BatchComparison _op;

    public Int32ColumnVsLiteralPredicate(int ordinal, int literal, BatchComparison op)
    {
        _ordinal = ordinal;
        _literal = literal;
        _op = op;
    }

    public void Evaluate(RowBatch batch, Span<bool> mask)
    {
        int n = batch.Count;
        int lit = _literal;
        int ord = _ordinal;

        switch (_op)
        {
            case BatchComparison.Equal:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() == lit;
                }
                break;
            case BatchComparison.NotEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() != lit;
                }
                break;
            case BatchComparison.LessThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() < lit;
                }
                break;
            case BatchComparison.LessEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() <= lit;
                }
                break;
            case BatchComparison.GreaterThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() > lit;
                }
                break;
            case BatchComparison.GreaterEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt32() >= lit;
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported BatchComparison: {_op}");
        }
    }
}

/// <summary>
/// Batch predicate for <c>column OP literal</c> where the column is Int64 and
/// the literal is Int64.
/// </summary>
internal sealed class Int64ColumnVsLiteralPredicate : IBatchPredicate
{
    private readonly int _ordinal;
    private readonly long _literal;
    private readonly BatchComparison _op;

    public Int64ColumnVsLiteralPredicate(int ordinal, long literal, BatchComparison op)
    {
        _ordinal = ordinal;
        _literal = literal;
        _op = op;
    }

    public void Evaluate(RowBatch batch, Span<bool> mask)
    {
        int n = batch.Count;
        long lit = _literal;
        int ord = _ordinal;

        switch (_op)
        {
            case BatchComparison.Equal:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() == lit;
                }
                break;
            case BatchComparison.NotEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() != lit;
                }
                break;
            case BatchComparison.LessThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() < lit;
                }
                break;
            case BatchComparison.LessEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() <= lit;
                }
                break;
            case BatchComparison.GreaterThan:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() > lit;
                }
                break;
            case BatchComparison.GreaterEqual:
                for (int i = 0; i < n; i++)
                {
                    DataValue v = batch[i][ord];
                    mask[i] = !v.IsNull && v.AsInt64() >= lit;
                }
                break;
            default:
                throw new InvalidOperationException($"Unsupported BatchComparison: {_op}");
        }
    }
}
