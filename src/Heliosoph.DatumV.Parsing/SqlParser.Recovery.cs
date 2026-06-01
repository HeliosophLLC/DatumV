using System.Linq;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8603, CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace Heliosoph.DatumV.Parsing;

public static partial class SqlParser
{
    /// <summary>
    /// Parses SQL clause-by-clause with error recovery. When a clause combinator
    /// fails, records the error, skips tokens to the next clause keyword, and
    /// continues parsing subsequent clauses.
    /// </summary>
    private static ParseResult ParseWithRecovery(TokenList<SqlToken> tokens)
    {
        List<ParseError> errors = new();
        Token<SqlToken>[] tokenArray = tokens.ToArray();
        int position = 0;

        // ── WITH clause (CTEs) ──
        CommonTableExpression[]? commonTableExpressions = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.With)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, CommonTableExpression[]> withResult =
                WithClauseParser.TryParse(remaining);

            if (!withResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid WITH clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                commonTableExpressions = withResult.Value;
                position += CountConsumed(tokenArray, position, withResult.Remainder);
            }
        }

        // ── SELECT columns ──
        SelectColumn[]? columns = null;
        LetBinding[]? recoveryLetBindings = null;
        List<AssertClause> recoveryDefineAssertions = new();
        if (position < tokenArray.Length)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Token<SqlToken>> selectResult =
                Token.EqualTo(SqlToken.Select).TryParse(remaining);

            if (!selectResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Expected SELECT keyword.");
                position = SkipToNextClauseIndex(tokenArray, position);
            }
            else
            {
                position += CountConsumed(tokenArray, position, selectResult.Remainder);

                // ── DEFINE block or inline LET bindings ──
                if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Define)
                {
                    TokenList<SqlToken> defineRemaining = new(tokenArray[position..]);
                    TokenListParserResult<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)> defineResult =
                        DefineBlockParser.TryParse(defineRemaining);

                    if (!defineResult.HasValue)
                    {
                        AddErrorFromToken(errors, tokenArray, position, "Invalid DEFINE block.");
                        position = SkipToNextClauseIndex(tokenArray, position + 1);
                    }
                    else
                    {
                        if (defineResult.Value.LetBindings.Length > 0)
                            recoveryLetBindings = defineResult.Value.LetBindings;
                        recoveryDefineAssertions.AddRange(defineResult.Value.Assertions);
                        position += CountConsumed(tokenArray, position, defineResult.Remainder);
                    }
                }
                else
                {
                    TokenList<SqlToken> letRemaining = new(tokenArray[position..]);
                    TokenListParserResult<SqlToken, LetBinding[]> letResult =
                        LetBindingsParser.TryParse(letRemaining);

                    if (letResult.HasValue && letResult.Value.Length > 0)
                    {
                        recoveryLetBindings = letResult.Value;
                        position += CountConsumed(tokenArray, position, letResult.Remainder);
                    }
                }

                TokenList<SqlToken> afterSelect = new(tokenArray[position..]);
                TokenListParserResult<SqlToken, SelectColumn[]> columnsResult =
                    ColumnList.TryParse(afterSelect);

                if (!columnsResult.HasValue)
                {
                    AddErrorFromToken(errors, tokenArray, position, "Invalid column list after SELECT.");
                    position = SkipToNextClauseIndex(tokenArray, position);
                }
                else
                {
                    columns = columnsResult.Value;
                    position += CountConsumed(tokenArray, position, columnsResult.Remainder);
                }
            }
        }
        else
        {
            AddErrorFromToken(errors, tokenArray, position, "Expected SELECT keyword.");
        }

        // ── FROM clause (optional) ──
        FromClause? fromClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.From)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, FromClause> fromResult =
                FromClauseParser.TryParse(remaining);

            if (!fromResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid FROM clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                fromClause = fromResult.Value;
                position += CountConsumed(tokenArray, position, fromResult.Remainder);
            }
        }

        // ── JOIN clauses ──
        List<JoinClause> joinClauses = new();
        while (position < tokenArray.Length && IsJoinStartToken(tokenArray[position].Kind))
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, JoinClause> joinResult =
                JoinClauseParser.TryParse(remaining);

            if (!joinResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid JOIN clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                joinClauses.Add(joinResult.Value);
                position += CountConsumed(tokenArray, position, joinResult.Remainder);
            }
        }

        // ── WHERE clause ──
        Expression? whereClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Where)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> whereResult =
                WhereClauseParser.TryParse(remaining);

            if (!whereResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid WHERE clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                whereClause = whereResult.Value;
                position += CountConsumed(tokenArray, position, whereResult.Remainder);
            }
        }

        // ── GROUP BY clause ──
        GroupByClause? groupByClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Group)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, GroupByClause> groupByResult =
                GroupByClauseParser.TryParse(remaining);

            if (!groupByResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid GROUP BY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                groupByClause = groupByResult.Value;
                position += CountConsumed(tokenArray, position, groupByResult.Remainder);
            }
        }

        // ── HAVING clause ──
        Expression? havingClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Having)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> havingResult =
                HavingClauseParser.TryParse(remaining);

            if (!havingResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid HAVING clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                havingClause = havingResult.Value;
                position += CountConsumed(tokenArray, position, havingResult.Remainder);
            }
        }

        // ── QUALIFY clause ──
        Expression? qualifyClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Qualify)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression> qualifyResult =
                QualifyClauseParser.TryParse(remaining);

            if (!qualifyResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid QUALIFY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                qualifyClause = qualifyResult.Value;
                position += CountConsumed(tokenArray, position, qualifyResult.Remainder);
            }
        }

        // ── ASSERT clauses ──
        List<AssertClause> assertions = new();
        while (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Assert)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, AssertClause> assertResult =
                AssertClauseParser.TryParse(remaining);

            if (!assertResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid ASSERT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
                break;
            }
            else
            {
                assertions.Add(assertResult.Value);
                position += CountConsumed(tokenArray, position, assertResult.Remainder);
            }
        }

        // ── PIVOT clause ──
        PivotClause? pivotClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Pivot)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, PivotClause> pivotResult =
                PivotClauseParser.TryParse(remaining);

            if (!pivotResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid PIVOT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                pivotClause = pivotResult.Value;
                position += CountConsumed(tokenArray, position, pivotResult.Remainder);
            }
        }

        // ── UNPIVOT clause ──
        UnpivotClause? unpivotClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Unpivot)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, UnpivotClause> unpivotResult =
                UnpivotClauseParser.TryParse(remaining);

            if (!unpivotResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid UNPIVOT clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                unpivotClause = unpivotResult.Value;
                position += CountConsumed(tokenArray, position, unpivotResult.Remainder);
            }
        }

        // ── ORDER BY clause ──
        OrderByClause? orderByClause = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Order)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, OrderByClause> orderByResult =
                OrderByClauseParser.TryParse(remaining);

            if (!orderByResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid ORDER BY clause.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                orderByClause = orderByResult.Value;
                position += CountConsumed(tokenArray, position, orderByResult.Remainder);
            }
        }

        // ── LIMIT ──
        Expression? limitValue = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Limit)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression?> limitResult =
                LimitParser.TryParse(remaining);

            if (!limitResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid LIMIT value.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                limitValue = limitResult.Value;
                position += CountConsumed(tokenArray, position, limitResult.Remainder);
            }
        }

        // ── OFFSET ──
        Expression? offsetValue = null;
        if (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Offset)
        {
            TokenList<SqlToken> remaining = new(tokenArray[position..]);
            TokenListParserResult<SqlToken, Expression?> offsetResult =
                OffsetParser.TryParse(remaining);

            if (!offsetResult.HasValue)
            {
                AddErrorFromToken(errors, tokenArray, position, "Invalid OFFSET value.");
                position = SkipToNextClauseIndex(tokenArray, position + 1);
            }
            else
            {
                offsetValue = offsetResult.Value;
                position += CountConsumed(tokenArray, position, offsetResult.Remainder);
            }
        }

        // ── Trailing semicolons ──
        while (position < tokenArray.Length && tokenArray[position].Kind == SqlToken.Semicolon)
        {
            position++;
        }

        // ── Trailing tokens ──
        if (position < tokenArray.Length)
        {
            AddErrorFromToken(errors, tokenArray, position, "Unexpected tokens after end of statement.");
        }

        // Build partial AST if we have enough structure.
        QueryExpression? query = null;
        if (columns is not null)
        {
            SelectStatement statement = new(
                columns,
                fromClause,
                joinClauses.Count > 0 ? joinClauses.ToArray() : null,
                whereClause,
                groupByClause,
                havingClause,
                qualifyClause,
                MergeAssertions(recoveryDefineAssertions.ToArray(), assertions.Count > 0 ? assertions.ToArray() : Array.Empty<AssertClause>()),
                pivotClause,
                unpivotClause,
                orderByClause,
                limitValue,
                offsetValue,
                CommonTableExpressions: commonTableExpressions,
                LetBindings: recoveryLetBindings);
            query = new SelectQueryExpression(statement);
        }

        return new ParseResult(query, errors);
    }

    /// <summary>
    /// Checks whether the given token kind starts a JOIN clause.
    /// </summary>
    private static bool IsJoinStartToken(SqlToken kind)
    {
        return kind is SqlToken.Join or SqlToken.Inner or
            SqlToken.Left or SqlToken.Right or SqlToken.Full or SqlToken.Cross;
    }

    /// <summary>
    /// Skips forward in the token array from the given index until a
    /// clause-starting keyword is found, returning that index. Returns
    /// past-the-end if no clause keyword is found.
    /// </summary>
    private static int SkipToNextClauseIndex(Token<SqlToken>[] tokenArray, int startIndex)
    {
        for (int i = startIndex; i < tokenArray.Length; i++)
        {
            if (ClauseStartTokens.Contains(tokenArray[i].Kind))
            {
                return i;
            }
        }

        return tokenArray.Length;
    }

    /// <summary>
    /// Counts how many tokens were consumed by comparing the starting position
    /// to the remainder returned by a <c>TryParse</c> call.
    /// </summary>
    private static int CountConsumed(Token<SqlToken>[] tokenArray, int startPosition, TokenList<SqlToken> remainder)
    {
        if (remainder.IsAtEnd)
        {
            return tokenArray.Length - startPosition;
        }

        Token<SqlToken> nextToken = remainder.ConsumeToken().Value;
        for (int i = startPosition; i < tokenArray.Length; i++)
        {
            if (tokenArray[i].Span.Position.Absolute == nextToken.Span.Position.Absolute)
            {
                return i - startPosition;
            }
        }

        return tokenArray.Length - startPosition;
    }

    /// <summary>
    /// Records a parse error at the given token index, or a default position
    /// if the index is past the end of the array.
    /// </summary>
    private static void AddErrorFromToken(List<ParseError> errors, Token<SqlToken>[] tokenArray, int index, string message)
    {
        if (index < tokenArray.Length)
        {
            Token<SqlToken> token = tokenArray[index];
            errors.Add(new ParseError
            {
                Message = message,
                Line = token.Span.Position.Line,
                Column = token.Span.Position.Column,
                Length = token.Span.Length,
            });
        }
        else
        {
            errors.Add(new ParseError
            {
                Message = message,
                Line = 1,
                Column = 1,
                Length = 1,
            });
        }
    }

}
