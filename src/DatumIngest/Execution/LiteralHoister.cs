using DatumIngest.Functions.Audio;
using DatumIngest.Functions.Image;
using DatumIngest.Functions.Video;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Execution;

/// <summary>
/// Rewrites <see cref="LiteralExpression"/> nodes into <see cref="LiteralValueExpression"/>
/// by materializing each literal's payload into a long-lived <see cref="IValueStore"/>
/// exactly once. After this pass runs, the expression evaluator returns the cached
/// <see cref="DataValue"/> for every literal access, skipping the per-row
/// <c>switch</c>-on-CLR-type and arena write that <see cref="LiteralExpression"/> incurs.
///
/// Intended to run between planning and execution, with the query's persistent store
/// (e.g. <c>context.Store</c>) so hoisted values outlive any individual batch.
/// </summary>
public static class LiteralHoister
{
    /// <summary>
    /// Returns the <see cref="DataValue"/> carried by <paramref name="expression"/>
    /// if it is a plan-time-known constant — i.e. a top-level
    /// <see cref="LiteralExpression"/> (substituted from a <c>$parameter</c> by
    /// <see cref="ParameterBinder"/>, or written as a literal in source) or an
    /// already-hoisted <see cref="LiteralValueExpression"/>. Returns <c>false</c>
    /// for column references, function calls, binary expressions over literals
    /// (no constant-folding of compound expressions), and anything else.
    /// </summary>
    /// <remarks>
    /// Used by callers that need plan-time access to a literal's value without
    /// rewriting the whole subtree — most notably TVF schema resolution, where
    /// <c>open_h5_dataset('foo.h5', '/x')</c> wants the two path strings to
    /// peek the file's schema. <paramref name="store"/> is consulted only when
    /// the expression is a <see cref="LiteralExpression"/> whose payload needs
    /// arena allocation (long strings, binary parameters).
    /// </remarks>
    public static bool TryGetConstantValue(
        Expression expression,
        IValueStore store,
        out DataValue value)
    {
        switch (expression)
        {
            case LiteralExpression literal:
                value = HoistLiteral(literal, store).Value;
                return true;
            case LiteralValueExpression hoisted:
                value = hoisted.Value;
                return true;
            default:
                value = default;
                return false;
        }
    }

    /// <summary>
    /// Recursively rewrites <paramref name="expression"/> so every contained
    /// <see cref="LiteralExpression"/> becomes a <see cref="LiteralValueExpression"/>.
    /// Nodes with no literals are returned unchanged.
    /// </summary>
    public static Expression Hoist(Expression expression, IValueStore store) =>
        expression switch
        {
            LiteralExpression lit => HoistLiteral(lit, store),

            // Already hoisted, or a leaf that can't contain a literal.
            LiteralValueExpression
                or ColumnReference
                or CurrentTimestampExpression
                or ParameterExpression
                or TypeLiteralExpression
                or ScanExpression
                or ErrorExpression => expression,

            BinaryExpression b => b with
            {
                Left = Hoist(b.Left, store),
                Right = Hoist(b.Right, store),
            },

            UnaryExpression u => u with { Operand = Hoist(u.Operand, store) },

            FunctionCallExpression fn => fn with
            {
                Arguments = HoistList(fn.Arguments, store),
            },

            CastExpression c => c with { Expression = Hoist(c.Expression, store) },

            InExpression i => i with
            {
                Expression = Hoist(i.Expression, store),
                Values = HoistList(i.Values, store),
            },

            BetweenExpression bt => bt with
            {
                Expression = Hoist(bt.Expression, store),
                Low = Hoist(bt.Low, store),
                High = Hoist(bt.High, store),
            },

            IsNullExpression n => n with { Expression = Hoist(n.Expression, store) },

            CaseExpression ce => ce with
            {
                Operand = ce.Operand is null ? null : Hoist(ce.Operand, store),
                WhenClauses = ce.WhenClauses
                    .Select(w => new WhenClause(Hoist(w.Condition, store), Hoist(w.Result, store)))
                    .ToList(),
                ElseResult = ce.ElseResult is null ? null : Hoist(ce.ElseResult, store),
            },

            LikeExpression like => like with
            {
                Expression = Hoist(like.Expression, store),
                Pattern = Hoist(like.Pattern, store),
                EscapeCharacter = Hoist(like.EscapeCharacter, store),
            },

            AtTimeZoneExpression atz => atz with
            {
                Expression = Hoist(atz.Expression, store),
                TimeZone = Hoist(atz.TimeZone, store),
            },

            StructLiteralExpression sl => sl with
            {
                Fields = sl.Fields
                    .Select(f => new StructField(f.Name, Hoist(f.Value, store)))
                    .ToList(),
            },

            IndexAccessExpression ia => ia with
            {
                Source = Hoist(ia.Source, store),
                Indices = ia.Indices.Select(i => Hoist(i, store)).ToArray(),
            },

            LambdaExpression lam => lam with { Body = Hoist(lam.Body, store) },

            // Subquery variants and window calls should have been rewritten to joins
            // or to a different operator before hoisting runs. If one survives, leave
            // it untouched so the evaluator's existing error paths fire with a clear
            // message rather than being silently masked here.
            _ => expression,
        };

    private static IReadOnlyList<Expression> HoistList(
        IReadOnlyList<Expression> list, IValueStore store)
    {
        if (list.Count == 0) return list;

        Expression[] result = new Expression[list.Count];
        bool changed = false;
        for (int i = 0; i < list.Count; i++)
        {
            Expression original = list[i];
            Expression hoisted = Hoist(original, store);
            result[i] = hoisted;
            if (!ReferenceEquals(original, hoisted)) changed = true;
        }
        return changed ? result : list;
    }

    private static LiteralValueExpression HoistLiteral(LiteralExpression lit, IValueStore store)
    {
        DataValue dv = lit.Value switch
        {
            null      => DataValue.UnknownNull(),
            DataValue existing => existing,
            sbyte i8  => DataValue.FromInt8(i8),
            short i16 => DataValue.FromInt16(i16),
            int i32   => DataValue.FromInt32(i32),
            long i64  => DataValue.FromInt64(i64),
            float f32 => DataValue.FromFloat32(f32),
            double f64 => DataValue.FromFloat64(f64),
            string s  => DataValue.FromString(s, store),
            bool b    => DataValue.FromBoolean(b),
            // Multipart-parameter binary payloads land here when the
            // ParameterBinder produces a LiteralExpression(BinaryParameter)
            // for $img / $clip / etc. The bytes go into the per-query
            // store once at hoist time so every row sees the same
            // arena-backed DataValue.
            BinaryParameter binary => binary.Kind switch
            {
                DataKind.Image => ImageDataValueFactory.FromEncodedBytes(binary.Bytes, store),
                DataKind.Audio => AudioDataValueFactory.FromEncodedBytes(binary.Bytes, store),
                DataKind.Video => VideoDataValueFactory.FromEncodedBytes(binary.Bytes, store),
                DataKind.UInt8 => DataValue.FromByteArray(binary.Bytes, store),
                _ => throw new InvalidOperationException(
                    $"BinaryParameter kind {binary.Kind} is not a recognised binary kind. " +
                    "Use Image / Audio / Video / UInt8."),
            },
            _ => throw new InvalidOperationException(
                $"Unsupported literal type for hoisting: {lit.Value.GetType().Name}."),
        };

        // No special tagging needed: the hoist store IS context.Store IS the
        // single per-query arena that every operator's batch arena points to.
        // A literal materialised here is in the one arena everyone reads from,
        // so plain IsInArena (single flag) is sufficient.
        return new LiteralValueExpression(dv);
    }
}
