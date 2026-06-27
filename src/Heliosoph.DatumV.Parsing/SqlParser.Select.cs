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
    // ───────────────────── SELECT columns ─────────────────────

    /// <summary>
    /// An optional EXCEPT clause that follows <c>*</c> or <c>table.*</c> to exclude
    /// specific columns from the wildcard expansion: <c>* EXCEPT (col1, col2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<string>> ExceptColumnsClause =
        from exceptKw in Token.EqualTo(SqlToken.Except)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from columns in Token.EqualTo(SqlToken.Identifier)
            .Select(GetTokenText)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (IReadOnlyList<string>)columns;

    /// <summary>
    /// A single replacement item inside a REPLACE clause: <c>expression AS column_name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReplacement> ReplacementItem =
        from expression in ExpressionParser
        from asKw in Token.EqualTo(SqlToken.As)
        from name in Token.EqualTo(SqlToken.Identifier)
        select new ColumnReplacement(expression, GetTokenText(name));

    /// <summary>
    /// An optional REPLACE clause that follows <c>*</c> or <c>table.*</c> (and optional EXCEPT)
    /// to substitute column values: <c>* REPLACE (expr AS col1, expr AS col2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<ColumnReplacement>> ReplaceColumnsClause =
        from replaceKw in Token.EqualTo(SqlToken.Replace)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from items in ReplacementItem.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (IReadOnlyList<ColumnReplacement>)items;

    /// <summary>SELECT * or SELECT * EXCEPT (col1, col2) or SELECT * REPLACE (expr AS col) (all columns with optional exclusion/replacement).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> StarColumn =
        from star in Token.EqualTo(SqlToken.Star)
        from excluded in ExceptColumnsClause.Try().AsNullable().OptionalOrDefault()
        from replaced in ReplaceColumnsClause.Try().AsNullable().OptionalOrDefault()
        select (SelectColumn)new SelectAllColumns(excluded, replaced);

    /// <summary>SELECT table.* or SELECT table.* EXCEPT (col1, col2) or SELECT table.* REPLACE (expr AS col) (all columns from a specific table with optional exclusion/replacement).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> TableStarColumn =
        from table in Token.EqualTo(SqlToken.Identifier)
        from dot in Token.EqualTo(SqlToken.Dot)
        from star in Token.EqualTo(SqlToken.Star)
        from excluded in ExceptColumnsClause.Try().AsNullable().OptionalOrDefault()
        from replaced in ReplaceColumnsClause.Try().AsNullable().OptionalOrDefault()
        select (SelectColumn)new SelectTableColumns(GetTokenText(table), ToSpan(table, star), excluded, replaced);

    /// <summary>
    /// Procedural-variable assignment column: <c>name := expr</c> at the top
    /// level of a SELECT list (no alias). The <c>:=</c> operator is the
    /// PG-native PL/pgSQL assignment marker, unambiguous against the
    /// comparison <c>=</c>. The RHS becomes the projected expression; the
    /// variable name is lifted onto
    /// <see cref="SelectColumn.AssignedVariableName"/> so the procedural
    /// batch executor routes the SELECT into the variable-assignment path
    /// instead of yielding rows.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> AssignmentColumn =
        from name in SP.Ref(() => IdentifierOrKeywordAsName!)
        from assign in Token.EqualTo(SqlToken.ColonEquals)
        from rhs in SP.Ref(() => ExpressionParser!)
        select new SelectColumn(rhs, Alias: null, AssignedVariableName: name);

    /// <summary>
    /// A single expression column with optional <c>AS</c> alias.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ExpressionColumn =
        from expression in SP.Ref(() => ExpressionParser!)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from name in IdentifierLike
            select GetTokenText(name)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
        select new SelectColumn(expression, alias);

    /// <summary>A single column in the SELECT list.</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn> ColumnItem =
        TableStarColumn.Try()
            .Or(StarColumn.Try())
            .Or(AssignmentColumn.Try())
            .Or(ExpressionColumn);

    // ───────────────────── SCAN (fold/prefix-scan) expressions ─────────────────────

    /// <summary>
    /// Parses the OVER clause for a SCAN expression. Unlike window functions, ORDER BY is required.
    /// </summary>
    private static readonly TokenListParser<SqlToken, WindowSpecification> ScanOverClauseParser =
        from over in Token.EqualTo(SqlToken.Over)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from partitionBy in WindowPartitionByParser.OptionalOrDefault()
        from orderBy in WindowOrderByParser
        from close in Token.EqualTo(SqlToken.RightParen)
        select new WindowSpecification(partitionBy, orderBy);

    /// <summary>
    /// Parses a parenthesized, comma-separated list of two or more expressions.
    /// Used for tuple-form SCAN body and INIT clauses.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression[]> ParenExpressionListParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from first in SP.Ref(() => ExpressionParser!)
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AtLeastOnce()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new Expression[] { first }.Concat(rest).ToArray();

    /// <summary>
    /// Parses a parenthesized, comma-separated list of two or more identifiers for
    /// SCAN accumulator names or output aliases. Already consumes the delimiters.
    /// </summary>
    private static TokenListParser<SqlToken, string[]> ScanNameListParser() =>
        from open in Token.EqualTo(SqlToken.LeftParen)
        from firstName in IdentifierLike
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from ident in IdentifierLike
            select ident
        ).AtLeastOnce()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new[] { GetTokenText(firstName) }.Concat(rest.Select(GetTokenText)).ToArray();

    /// <summary>
    /// Tuple-form SCAN expression:
    /// <c>SCAN (a, b) = (e1, e2) INIT (v1, v2) OVER (...) AS (a1, a2)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> TupleScanExpressionParser =
        from scanKw in Token.EqualTo(SqlToken.Scan)
        from names in ScanNameListParser()
        from eq in Token.EqualTo(SqlToken.Equals)
        from bodies in ParenExpressionListParser
        from initKw in Token.EqualTo(SqlToken.Init)
        from inits in ParenExpressionListParser
        from window in ScanOverClauseParser
        from asKw in Token.EqualTo(SqlToken.As)
        from aliases in ScanNameListParser()
        select (Expression)new ScanExpression(names, bodies, inits, window, aliases, ToSpan(scanKw));

    /// <summary>
    /// Scalar SCAN expression:
    /// <c>SCAN acc = expr INIT seed OVER (...) AS alias</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ScalarScanExpressionParser =
        from scanKw in Token.EqualTo(SqlToken.Scan)
        from name in Token.EqualTo(SqlToken.Identifier)
        from eq in Token.EqualTo(SqlToken.Equals)
        from body in SP.Ref(() => ExpressionParser!)
        from initKw in Token.EqualTo(SqlToken.Init)
        from init in SP.Ref(() => ExpressionParser!)
        from window in ScanOverClauseParser
        from asKw in Token.EqualTo(SqlToken.As)
        from alias in IdentifierLike
        select (Expression)new ScanExpression(
            [GetTokenText(name)], [body], [init], window, [GetTokenText(alias)], ToSpan(scanKw));

    /// <summary>
    /// SCAN expression in either tuple or scalar form.
    /// Tuple is tried first with backtracking since both start with the SCAN token.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression> ScanExpressionParser =
        TupleScanExpressionParser.Try().Or(ScalarScanExpressionParser);

    // ───────────────────── LET bindings ─────────────────────

    /// <summary>
    /// Parses a comma-separated list of two or more identifiers for a destructure pattern,
    /// already positioned after the opening delimiter.
    /// </summary>
    private static TokenListParser<SqlToken, string[]> DestructureNameListParser(SqlToken closingToken) =>
        from firstName in Token.EqualTo(SqlToken.Identifier)
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from ident in Token.EqualTo(SqlToken.Identifier)
            select ident
        ).AtLeastOnce()
        from close in Token.EqualTo(closingToken)
        select new[] { GetTokenText(firstName) }.Concat(rest.Select(GetTokenText)).ToArray();

    /// <summary>
    /// A positional destructuring LET binding: <c>LET (a, b [, c ...]) = expression</c>.
    /// Extracts values by zero-based index from a Vector, Array, or Struct.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> PositionalDestructureLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in DestructureNameListParser(SqlToken.RightParen)
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        select new LetBinding(
            string.Empty,
            expression,
            OutputAlias: null,
            Span: ToSpan(open),
            Destructure: new DestructurePattern(names, DestructureMode.Positional, ToSpan(open)));

    /// <summary>
    /// A named destructuring LET binding: <c>LET {field1, field2 [, field3 ...]} = expression</c>.
    /// Extracts values by field name from a Struct.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> NamedDestructureLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from open in Token.EqualTo(SqlToken.LeftBrace)
        from names in DestructureNameListParser(SqlToken.RightBrace)
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        select new LetBinding(
            string.Empty,
            expression,
            OutputAlias: null,
            Span: ToSpan(open),
            Destructure: new DestructurePattern(names, DestructureMode.Named, ToSpan(open)));

    /// <summary>A single LET binding: <c>LET name = expression [AS alias]</c>.</summary>
    private static readonly TokenListParser<SqlToken, LetBinding> ScalarLetBindingParser =
        from letKw in Token.EqualTo(SqlToken.Let)
        from name in IdentifierLike
        from eq in Token.EqualTo(SqlToken.Equals)
        from expression in ExpressionParser
        from outputAlias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from alias in IdentifierLike
            select GetTokenText(alias)
        ).OptionalOrDefault()
        select new LetBinding(GetTokenText(name), expression, outputAlias, ToSpan(name));

    /// <summary>
    /// A single LET binding in any form: positional destructuring, named destructuring, or scalar.
    /// Positional and named are tried first (with backtracking) so that the scalar parser does not
    /// greedily consume the <c>LET</c> keyword before the pattern delimiter is visible.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding> LetBindingParser =
        PositionalDestructureLetBindingParser.Try()
            .Or(NamedDestructureLetBindingParser.Try())
            .Or(ScalarLetBindingParser);

    /// <summary>
    /// Zero or more comma-separated LET bindings at the start of a SELECT list.
    /// Each binding is followed by a comma that separates it from the next
    /// binding or the first output column.
    /// </summary>
    private static readonly TokenListParser<SqlToken, LetBinding[]> LetBindingsParser =
        (from binding in LetBindingParser
         from comma in Token.EqualTo(SqlToken.Comma)
         select binding).Many();

    /// <summary>Comma-delimited list of SELECT columns (at least one required).</summary>
    private static readonly TokenListParser<SqlToken, SelectColumn[]> ColumnList =
        from first in ColumnItem
        from rest in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from item in ColumnItem
            select item
        ).Many()
        select new SelectColumn[] { first }.Concat(rest).ToArray();

    // ───────────────────── FROM clause ─────────────────────

    /// <summary>
    /// Parses BERNOULLI, SYSTEM, STRATIFIED, or BALANCED as a <see cref="TablesampleMethod"/>.
    /// These are parsed as identifiers (not reserved keywords) to avoid breaking user table names.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TablesampleMethod> TablesampleMethodParser =
        Token.EqualTo(SqlToken.Identifier)
            .Where(token =>
            {
                string text = GetTokenText(token);
                return text.Equals("BERNOULLI", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("STRATIFIED", StringComparison.OrdinalIgnoreCase)
                    || text.Equals("BALANCED", StringComparison.OrdinalIgnoreCase);
            }, "BERNOULLI, SYSTEM, STRATIFIED, or BALANCED")
            .Select(token =>
            {
                string text = GetTokenText(token);
                if (text.Equals("BERNOULLI", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.Bernoulli;
                if (text.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.System;
                if (text.Equals("STRATIFIED", StringComparison.OrdinalIgnoreCase)) return TablesampleMethod.Stratified;
                return TablesampleMethod.Balanced;
            });

    /// <summary>
    /// Parses a single unqualified column name as a <see cref="ColumnReference"/>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReference> UnqualifiedColumnParser =
        Token.EqualTo(SqlToken.Identifier)
            .Select(token => new ColumnReference(null, GetTokenText(token), ToSpan(token)));

    /// <summary>
    /// Parses an <c>ON column</c> or <c>ON (col1, col2, ...)</c> stratification key list.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<ColumnReference>> StratifyColumnsParser =
        from onKeyword in Token.EqualTo(SqlToken.On)
        from columns in (
            // Parenthesized composite key: ON (col1, col2, ...)
            from open in Token.EqualTo(SqlToken.LeftParen)
            from cols in UnqualifiedColumnParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<ColumnReference>)cols
        ).Try().Or(
            // Single column: ON col
            UnqualifiedColumnParser.Select(col => (IReadOnlyList<ColumnReference>)new[] { col })
        )
        select columns;

    /// <summary>
    /// Parses a TABLESAMPLE clause:
    /// <c>TABLESAMPLE BERNOULLI|SYSTEM(percentage) [REPEATABLE(seed)]</c> or
    /// <c>TABLESAMPLE STRATIFIED|BALANCED(arg) ON column [REPEATABLE(seed)]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TablesampleClause> TablesampleClauseParser =
        from tablesampleKeyword in Token.EqualTo(SqlToken.Tablesample)
        from method in TablesampleMethodParser
        from open in Token.EqualTo(SqlToken.LeftParen)
        from argument in SP.Ref(() => ExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from stratifyColumns in StratifyColumnsParser.AsNullable().OptionalOrDefault()
        from seed in (
            from repeatableKeyword in Token.EqualTo(SqlToken.Repeatable)
            from seedOpen in Token.EqualTo(SqlToken.LeftParen)
            from seedExpression in SP.Ref(() => ExpressionParser!)
            from seedClose in Token.EqualTo(SqlToken.RightParen)
            select seedExpression
        ).AsNullable().OptionalOrDefault()
        select ValidateTablesampleClause(method, argument, seed, stratifyColumns);

    /// <summary>
    /// Validates that the ON clause is present for Stratified/Balanced and absent for Bernoulli/System.
    /// </summary>
    private static TablesampleClause ValidateTablesampleClause(
        TablesampleMethod method, Expression argument, Expression? seed,
        IReadOnlyList<ColumnReference>? stratifyColumns)
    {
        bool requiresOn = method is TablesampleMethod.Stratified or TablesampleMethod.Balanced;

        if (requiresOn && stratifyColumns is null)
        {
            throw new ParseException(
                $"TABLESAMPLE {method.ToString().ToUpperInvariant()} requires an ON clause specifying the stratification column(s).",
                default);
        }

        if (!requiresOn && stratifyColumns is not null)
        {
            throw new ParseException(
                $"TABLESAMPLE {method.ToString().ToUpperInvariant()} does not support an ON clause.",
                default);
        }

        return new TablesampleClause(method, argument, seed, stratifyColumns);
    }

    /// <summary>A table reference with optional schema qualifier, TABLESAMPLE clause, and alias.</summary>
    /// <remarks>
    /// Both segments accept <see cref="SqlToken.TypeKeyword"/> so a table named
    /// after a DataKind (e.g. <c>video</c>, <c>image</c>, <c>int32</c>) is
    /// addressable both bare and as a schema-qualified pair. There's no
    /// ambiguity at FROM position: a type literal can't appear here.
    /// </remarks>
    private static readonly TokenListParser<SqlToken, TableSource> TableReferenceParser =
        from first in Token.EqualTo(SqlToken.Identifier)
            .Or(Token.EqualTo(SqlToken.StringLiteral))
            .Or(Token.EqualTo(SqlToken.TypeKeyword))
        from schemaQualified in (
            from dot in Token.EqualTo(SqlToken.Dot)
            from second in Token.EqualTo(SqlToken.Identifier)
                .Or(Token.EqualTo(SqlToken.StringLiteral))
                .Or(Token.EqualTo(SqlToken.TypeKeyword))
            select second
        ).OptionalOrDefault()
        from tablesample in TablesampleClauseParser.AsNullable().OptionalOrDefault()
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
        select (TableSource)(schemaQualified.HasValue
            ? new TableReference(GetTokenText(schemaQualified), alias, ToSpan(first, schemaQualified), tablesample, SchemaName: GetTokenText(first))
            : new TableReference(GetTokenText(first), alias, ToSpan(first), tablesample));

    /// <summary>A subquery source: (SELECT ...) [AS] alias.</summary>
    private static readonly TokenListParser<SqlToken, TableSource> SubquerySourceParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from query in SP.Ref(() => SelectStatementParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select aliasName
        ).Try().Or(IdentifierLike)
        select (TableSource)new SubquerySource(query, GetTokenText(alias));

    /// <summary>
    /// A table-valued function source: identifier(args) [AS] alias.
    /// Must be tried before table reference because both start with Identifier.
    /// Arguments share the named-or-positional combinator with scalar
    /// <see cref="FunctionCall"/> so <c>fn(a := 1, b =&gt; 2)</c> parses at
    /// a TVF call site too; the <c>NamedArgPermuter</c> planner pass
    /// resolves the names against the TVF signature.
    /// </summary>
    private static readonly TokenListParser<SqlToken, TableSource> FunctionSourceParser =
        from nameTuple in NamespacedFunctionName
        from open in Token.EqualTo(SqlToken.LeftParen)
        from rawArgs in NamedOrPositionalArgument
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().Or(IdentifierLike.Select(GetTokenText))
        .OptionalOrDefault()
        let args = ExtractArgumentExpressions(rawArgs)
        let argNames = ExtractArgumentNames(rawArgs)
        select (TableSource)new FunctionSource(
            GetTokenText(nameTuple.Name),
            args,
            alias,
            ToSpan(nameTuple.Name),
            SchemaName: nameTuple.Namespace,
            ArgumentNames: argNames);

    /// <summary>A table source: subquery, function call, or table reference.</summary>
    /// <remarks>
    /// <para>
    /// <strong>No <c>.Try()</c> on <see cref="SubquerySourceParser"/>.</strong>
    /// A <c>(</c> at table-source position unambiguously means subquery —
    /// no other branch starts with it — so we commit on the open paren and
    /// let any failure inside (a malformed SELECT, an unsupported clause
    /// variant, an OFFSET that takes an expression instead of a literal)
    /// surface with its real position rather than backtracking to a generic
    /// "expected identifier" at the <c>(</c>. The <c>FunctionSource</c> vs
    /// <c>TableReference</c> branch IS genuinely ambiguous (both start with
    /// <see cref="SqlToken.Identifier"/>), so <c>.Try()</c> stays there.
    /// </para>
    /// </remarks>
    private static readonly TokenListParser<SqlToken, TableSource> TableSourceParser =
        SubquerySourceParser
            .Or(FunctionSourceParser.Try())
            .Or(TableReferenceParser);

    /// <summary>FROM table_source</summary>
    private static readonly TokenListParser<SqlToken, FromClause> FromClauseParser =
        from fromKw in Token.EqualTo(SqlToken.From)
        from source in TableSourceParser
        select new FromClause(source);

    /// <summary>
    /// SQL-89 / PG comma-separated FROM sources: <c>FROM a, b, c</c>. Each
    /// comma source is lowered to a synthetic <see cref="JoinClause"/> with
    /// <see cref="JoinType.Cross"/> so the existing planner machinery
    /// handles it uniformly with explicit <c>CROSS JOIN</c> syntax.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Implicit LATERAL.</strong> PG promotes a <see cref="FunctionSource"/>
    /// in this position to LATERAL automatically — the function's arguments
    /// may reference columns of preceding FROM items without the user
    /// writing the keyword. <see cref="SubquerySource"/> and
    /// <see cref="TableReference"/> stay non-lateral; a correlated subquery
    /// still needs explicit <c>JOIN LATERAL</c> syntax.
    /// </para>
    /// </remarks>
    private static readonly TokenListParser<SqlToken, JoinClause[]> CommaJoinSourcesParser =
        (
            from comma in Token.EqualTo(SqlToken.Comma)
            from source in TableSourceParser
            select new JoinClause(JoinType.Cross, source, OnCondition: null,
                IsLateral: source is FunctionSource)
        ).Many();

    // ───────────────────── JOIN clauses ─────────────────────

    /// <summary>Join type keyword combinations, including LATERAL and T-SQL APPLY variants.</summary>
    private static readonly TokenListParser<SqlToken, (JoinType Type, bool IsLateral)> JoinTypeParser =
        // CROSS APPLY (T-SQL style lateral cross join)
        Token.EqualTo(SqlToken.Cross)
            .IgnoreThen(Token.EqualTo(SqlToken.Apply))
            .Select(_ => (JoinType.Cross, true)).Try()
        // OUTER APPLY (T-SQL style lateral left join)
        .Or(Token.EqualTo(SqlToken.Outer)
            .IgnoreThen(Token.EqualTo(SqlToken.Apply))
            .Select(_ => (JoinType.Left, true)).Try())
        // INNER JOIN
        .Or(Token.EqualTo(SqlToken.Inner).IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.Inner, false)).Try())
        // LEFT [OUTER] JOIN [LATERAL]
        .Or(from _ in Token.EqualTo(SqlToken.Left)
            from __ in Token.EqualTo(SqlToken.Outer).OptionalOrDefault()
            from ___ in Token.EqualTo(SqlToken.Join)
            from isLateral in Token.EqualTo(SqlToken.Lateral).Select(t => true).OptionalOrDefault(false)
            select (JoinType.Left, isLateral))
        // RIGHT [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Right)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.Right, false)).Try())
        // FULL [OUTER] JOIN
        .Or(Token.EqualTo(SqlToken.Full)
            .IgnoreThen(Token.EqualTo(SqlToken.Outer).OptionalOrDefault())
            .IgnoreThen(Token.EqualTo(SqlToken.Join))
            .Select(_ => (JoinType.FullOuter, false)).Try())
        // CROSS JOIN [LATERAL] — .Try() allows backtracking when CROSS is followed by
        // VALIDATE (for CROSS VALIDATE) instead of JOIN.
        .Or((from _ in Token.EqualTo(SqlToken.Cross)
            from __ in Token.EqualTo(SqlToken.Join)
            from isLateral in Token.EqualTo(SqlToken.Lateral).Select(t => true).OptionalOrDefault(false)
            select (JoinType.Cross, isLateral)).Try())
        // Plain JOIN (defaults to INNER)
        .Or(Token.EqualTo(SqlToken.Join)
            .Select(_ => (JoinType.Inner, false)));

    /// <summary>A single JOIN clause with source and optional ON condition.</summary>
    /// <remarks>
    /// A <see cref="FunctionSource"/> on the right of any JOIN is promoted to
    /// LATERAL automatically, matching PG's "function calls in FROM are
    /// implicitly LATERAL" rule and mirroring <see cref="CommaJoinSourcesParser"/>.
    /// Without this, <c>... JOIN open_cifar10(a.bytes) AS c</c> would resolve
    /// <c>a.bytes</c> against an empty right-side scope.
    /// </remarks>
    private static readonly TokenListParser<SqlToken, JoinClause> JoinClauseParser =
        from joinInfo in JoinTypeParser
        from source in TableSourceParser
        from onCondition in (
            from onKw in Token.EqualTo(SqlToken.On)
            from condition in ExpressionParser
            select condition
        ).OptionalOrDefault()
        select new JoinClause(joinInfo.Type, source, onCondition,
            joinInfo.IsLateral || source is FunctionSource);

    /// <summary>Zero or more JOIN clauses.</summary>
    private static readonly TokenListParser<SqlToken, JoinClause[]> JoinClausesParser =
        JoinClauseParser.Many();

    // ───────────────────── WHERE clause ─────────────────────

    /// <summary>WHERE expression</summary>
    private static readonly TokenListParser<SqlToken, Expression> WhereClauseParser =
        from whereKw in Token.EqualTo(SqlToken.Where)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── GROUP BY clause ─────────────────────

    /// <summary>GROUP BY ALL | GROUP BY expr1, expr2, ...</summary>
    private static readonly TokenListParser<SqlToken, GroupByClause> GroupByClauseParser =
        from groupKw in Token.EqualTo(SqlToken.Group)
        from byKw in Token.EqualTo(SqlToken.By)
        from result in Token.EqualTo(SqlToken.All)
            .Select(_ => new GroupByClause(Array.Empty<Expression>(), IsAll: true))
            .Or(ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
                .Select(expressions => new GroupByClause(expressions)))
        select result;

    // ───────────────────── HAVING clause ─────────────────────

    /// <summary>HAVING expression</summary>
    private static readonly TokenListParser<SqlToken, Expression> HavingClauseParser =
        from havingKw in Token.EqualTo(SqlToken.Having)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── QUALIFY clause ─────────────────────

    /// <summary>QUALIFY expression (post-window-function filter).</summary>
    private static readonly TokenListParser<SqlToken, Expression> QualifyClauseParser =
        from qualifyKw in Token.EqualTo(SqlToken.Qualify)
        from condition in ExpressionParser
        select condition;

    // ───────────────────── ASSERT clause ─────────────────────

    /// <summary>
    /// Optional <c>ON FAIL SKIP | WARN | ABORT ["message"]</c> suffix for ASSERT.
    /// Parsed contextually using <c>ON</c> + identifier text to avoid reserving
    /// SKIP, WARN, and ABORT as keywords. An optional string literal immediately
    /// after the mode keyword is captured as an inline message.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (AssertFailureMode Mode, Expression? InlineMessage)> AssertFailureModeParser =
        from onKw in Token.EqualTo(SqlToken.On)
        from failKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => string.Equals(t.ToStringValue(), "FAIL", StringComparison.OrdinalIgnoreCase))
        from modeKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => string.Equals(t.ToStringValue(), "SKIP", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t.ToStringValue(), "WARN", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(t.ToStringValue(), "ABORT", StringComparison.OrdinalIgnoreCase))
        from inlineMessage in StringLiteral.AsNullable().Try().OptionalOrDefault()
        select (
            Mode: string.Equals(modeKw.ToStringValue(), "SKIP", StringComparison.OrdinalIgnoreCase)
                ? AssertFailureMode.Skip
                : string.Equals(modeKw.ToStringValue(), "WARN", StringComparison.OrdinalIgnoreCase)
                    ? AssertFailureMode.Warn
                    : AssertFailureMode.Abort,
            InlineMessage: inlineMessage);

    /// <summary>
    /// A single ASSERT clause: <c>ASSERT predicate [MESSAGE expr] [ON FAIL SKIP | WARN | ABORT ["message"]]</c>.
    /// The <c>MESSAGE</c> keyword form takes precedence over an inline string after the mode keyword
    /// when both are present.
    /// </summary>
    private static readonly TokenListParser<SqlToken, AssertClause> AssertClauseParser =
        from assertKw in Token.EqualTo(SqlToken.Assert)
        from predicate in ExpressionParser
        from message in (
            from msgKw in Token.EqualTo(SqlToken.Message)
            from msgExpr in ExpressionParser
            select msgExpr
        ).AsNullable().Try().OptionalOrDefault()
        from failureModeResult in AssertFailureModeParser.Try().OptionalOrDefault((Mode: AssertFailureMode.Abort, InlineMessage: default(Expression?)))
        select new AssertClause(predicate, message ?? failureModeResult.InlineMessage, failureModeResult.Mode, ToSpan(assertKw));

    /// <summary>Zero or more ASSERT clauses following QUALIFY.</summary>
    private static readonly TokenListParser<SqlToken, AssertClause[]> AssertClausesParser =
        AssertClauseParser.Many();

    // ───────────────────── DEFINE block ─────────────────────

    /// <summary>
    /// A single declaration inside a DEFINE block: either an inline LET binding
    /// or an ASSERT clause. The first element is non-null for LET declarations;
    /// the second element is non-null for ASSERT declarations.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding? Let, AssertClause? Assert)>
        DefineDeclarationParser =
        LetBindingParser
            .Select(let => (Let: (LetBinding?)let, Assert: (AssertClause?)null))
            .Or(AssertClauseParser
            .Select(assert => (Let: (LetBinding?)null, Assert: (AssertClause?)assert)));

    /// <summary>
    /// Parses a DEFINE block: <c>DEFINE { declaration [;] ... }</c>.
    /// Each declaration is either a LET binding or an ASSERT clause; semicolons
    /// are optional separators. Returns embedded LET bindings and ASSERT clauses
    /// as separate arrays so the SELECT parsers can merge them with their own bindings.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)>
        DefineBlockParser =
        from defineKw in Token.EqualTo(SqlToken.Define)
        from openBrace in Token.EqualTo(SqlToken.LeftBrace)
        from declarations in (
            from decl in DefineDeclarationParser
            from semi in Token.EqualTo(SqlToken.Semicolon).OptionalOrDefault()
            select decl
        ).Many()
        from closeBrace in Token.EqualTo(SqlToken.RightBrace)
        select (
            declarations.Where(d => d.Let is not null).Select(d => d.Let!).ToArray(),
            declarations.Where(d => d.Assert is not null).Select(d => d.Assert!).ToArray()
        );

    /// <summary>
    /// Tries to parse a DEFINE block first; if absent, falls back to zero or more inline
    /// LET bindings followed by commas. Returns the same <c>(LetBinding[], AssertClause[])</c>
    /// tuple in both cases, enabling a single binding site in the SELECT parsers.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (LetBinding[] LetBindings, AssertClause[] Assertions)>
        LetOrDefineParser =
        DefineBlockParser.Try()
            .Or(LetBindingsParser
            .Select(bindings => (LetBindings: bindings, Assertions: Array.Empty<AssertClause>())));

    /// <summary>
    /// Combines ASSERT clauses sourced from a DEFINE block with ASSERT clauses
    /// written as trailing clauses after the column list. Returns <see langword="null"/>
    /// when both inputs are empty so the <see cref="SelectStatement"/> field stays null.
    /// </summary>
    private static IReadOnlyList<AssertClause>? MergeAssertions(AssertClause[] fromDefine, AssertClause[] fromClauses)
    {
        if (fromDefine.Length == 0 && fromClauses.Length == 0)
            return null;
        if (fromDefine.Length == 0)
            return fromClauses;
        if (fromClauses.Length == 0)
            return fromDefine;
        return fromDefine.Concat(fromClauses).ToArray();
    }

    /// <summary>
    /// Concatenates the comma-style implicit cross joins with the explicit
    /// JOIN clauses in textual order. Returns <see langword="null"/> when
    /// both inputs are empty so the resulting <see cref="SelectStatement.Joins"/>
    /// stays <see langword="null"/> for join-less queries.
    /// </summary>
    private static IReadOnlyList<JoinClause>? CombineJoins(JoinClause[] commaJoins, JoinClause[] explicitJoins)
    {
        if (commaJoins.Length == 0 && explicitJoins.Length == 0)
            return null;
        if (commaJoins.Length == 0)
            return explicitJoins;
        if (explicitJoins.Length == 0)
            return commaJoins;
        return commaJoins.Concat(explicitJoins).ToArray();
    }

    // ───────────────────── PIVOT clause ─────────────────────

    /// <summary>
    /// Parses a bare column reference (no table prefix) and returns a
    /// <see cref="ColumnReference"/> rather than the <see cref="Expression"/> base type.
    /// Used by both <see cref="PivotClauseParser"/> and <see cref="UnpivotClauseParser"/>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnReference> BareColumnReferenceParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        select new ColumnReference(null, GetTokenText(name), ToSpan(name));

    /// <summary>
    /// PIVOT ( aggregate [, aggregate ...] FOR pivot_column [IN ( value [, value ...] )] ) [AS alias]
    /// <para>
    /// The value list is optional — when omitted the executor auto-discovers all distinct
    /// values at runtime (subject to the cardinality cap defined on <see cref="PivotClause"/>).
    /// </para>
    /// </summary>
    private static readonly TokenListParser<SqlToken, PivotClause> PivotClauseParser =
        from pivotKw in Token.EqualTo(SqlToken.Pivot)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from aggregates in FunctionCall
                .Where(e => e is FunctionCallExpression, "aggregate function call")
                .Select(e => (FunctionCallExpression)e)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from forKw in Token.EqualTo(SqlToken.For)
        from pivotColumn in BareColumnReferenceParser
        from valueList in (
            from inKw in Token.EqualTo(SqlToken.In)
            from openParen in Token.EqualTo(SqlToken.LeftParen)
            from values in ExpressionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from closeParen in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>?)values
        ).Try().OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().OptionalOrDefault()
        select new PivotClause(aggregates, pivotColumn, valueList, alias);

    // ───────────────────── UNPIVOT clause ─────────────────────

    /// <summary>
    /// UNPIVOT [INCLUDE NULLS] ( value_column FOR name_column IN ( column [, column ...] ) ) [AS alias]
    /// </summary>
    private static readonly TokenListParser<SqlToken, UnpivotClause> UnpivotClauseParser =
        from unpivotKw in Token.EqualTo(SqlToken.Unpivot)
        from includeNulls in (
            from includKw in Token.EqualTo(SqlToken.Include)
            from nullsKw in Token.EqualTo(SqlToken.Nulls)
            select true
        ).Try().OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        from valueColumn in Token.EqualTo(SqlToken.Identifier)
        from forKw in Token.EqualTo(SqlToken.For)
        from nameColumn in Token.EqualTo(SqlToken.Identifier)
        from inKw in Token.EqualTo(SqlToken.In)
        from openParen in Token.EqualTo(SqlToken.LeftParen)
        from sourceColumns in BareColumnReferenceParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from closeParen in Token.EqualTo(SqlToken.RightParen)
        from close in Token.EqualTo(SqlToken.RightParen)
        from alias in (
            from asKw in Token.EqualTo(SqlToken.As)
            from aliasName in IdentifierLike
            select GetTokenText(aliasName)
        ).Try().OptionalOrDefault()
        select new UnpivotClause(GetTokenText(valueColumn), GetTokenText(nameColumn), sourceColumns, includeNulls, alias);

    // ───────────────────── ORDER BY clause ─────────────────────

    /// <summary>A single ORDER BY item: expression [ASC|DESC].</summary>
    private static readonly TokenListParser<SqlToken, OrderByItem> OrderByItemParser =
        from expression in ExpressionParser
        from direction in Token.EqualTo(SqlToken.Asc).Select(_ => SortDirection.Ascending)
            .Or(Token.EqualTo(SqlToken.Desc).Select(_ => SortDirection.Descending))
            .OptionalOrDefault(SortDirection.Ascending)
        select new OrderByItem(expression, direction);

    /// <summary>ORDER BY item1, item2, ...</summary>
    private static readonly TokenListParser<SqlToken, OrderByClause> OrderByClauseParser =
        from orderKw in Token.EqualTo(SqlToken.Order)
        from byKw in Token.EqualTo(SqlToken.By)
        from items in OrderByItemParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select new OrderByClause(items);

    // ───────────────────── LIMIT / OFFSET ─────────────────────

    /// <summary>
    /// LIMIT expr — accepts any scalar expression that evaluates to an
    /// integer at execute time. The runtime evaluator resolves
    /// <c>@var</c> references against the active procedural variable
    /// scope, lets call sites compose <c>random(...)</c> / arithmetic /
    /// <c>udf.X(...)</c> into a row count, and constant-folds plain
    /// numeric literals (the most common case) without runtime cost.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression?> LimitParser =
        from limitKw in Token.EqualTo(SqlToken.Limit)
        from value in SP.Ref(() => ExpressionParser!)
        select (Expression?)value;

    /// <summary>
    /// OFFSET expr — same shape as <see cref="LimitParser"/>: any scalar
    /// expression yielding an integer, evaluated once at the start of the
    /// operator's run.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Expression?> OffsetParser =
        from offsetKw in Token.EqualTo(SqlToken.Offset)
        from value in SP.Ref(() => ExpressionParser!)
        select (Expression?)value;

    // ───────────────────── Common Table Expressions ─────────────────────

    /// <summary>
    /// Optional explicit column name list for a CTE: <c>(col1, col2, ...)</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string[]> CommonTableExpressionColumnListParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in IdentifierLike
            .Select(GetTokenText)
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select names;

    /// <summary>
    /// Materialization hint: <c>MATERIALIZED</c> or <c>NOT MATERIALIZED</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, MaterializationHint> MaterializationHintParser =
        (from notKw in Token.EqualTo(SqlToken.Not)
         from matKw in Token.EqualTo(SqlToken.Materialized)
         select MaterializationHint.NotMaterialized)
        .Try()
        .Or(from matKw in Token.EqualTo(SqlToken.Materialized)
            select MaterializationHint.Materialized);

    /// <summary>
    /// A single CTE definition: <c>name [(cols)] AS [MATERIALIZED | NOT MATERIALIZED] ( query )</c>.
    /// For recursive CTEs, the body contains <c>UNION ALL</c> separating the anchor and
    /// recursive member queries. For non-recursive CTEs, the body is a full query expression
    /// supporting set operations (UNION ALL, INTERSECT, EXCEPT).
    /// The <paramref name="isRecursive"/> flag is threaded from the WITH RECURSIVE prefix.
    /// </summary>
    private static TokenListParser<SqlToken, CommonTableExpression> SingleCommonTableExpressionParser(bool isRecursive) =>
        isRecursive
            ? RecursiveCommonTableExpressionParser
            : NonRecursiveCommonTableExpressionParser;

    /// <summary>
    /// Parses a non-recursive CTE whose body is a full query expression, supporting
    /// UNION, UNION ALL, INTERSECT, EXCEPT, ORDER BY, LIMIT, and OFFSET inside the parentheses.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression> NonRecursiveCommonTableExpressionParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from columnNames in CommonTableExpressionColumnListParser.OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from hint in MaterializationHintParser.OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        // Data-modifying CTE bodies (PostgreSQL's INSERT/UPDATE/DELETE … RETURNING)
        // share the parens-delimited shape with regular query expressions.
        // Each of INSERT / UPDATE / DELETE has a distinct leading keyword, so
        // they can be tried in series before falling back to
        // QueryExpressionParser for a regular SELECT body.
        from body in InsertParser.Try().Select(stmt =>
            (QueryExpression)new InsertQueryExpression((InsertStatement)stmt))
            .Or(UpdateParser.Try().Select(stmt =>
                (QueryExpression)new UpdateQueryExpression((UpdateStatement)stmt)))
            .Or(DeleteParser.Try().Select(stmt =>
                (QueryExpression)new DeleteQueryExpression((DeleteStatement)stmt)))
            .Or(SP.Ref(() => QueryExpressionParser!))
        from close in Token.EqualTo(SqlToken.RightParen)
        select new CommonTableExpression(
            GetTokenText(name),
            body,
            RecursiveQuery: null,
            columnNames,
            IsRecursive: false,
            hint);

    /// <summary>
    /// Parses a recursive CTE whose body is split into an anchor member and a recursive
    /// member separated by UNION ALL. Both are parsed as individual SELECT statements
    /// for separate planning at execution time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression> RecursiveCommonTableExpressionParser =
        from name in Token.EqualTo(SqlToken.Identifier)
        from columnNames in CommonTableExpressionColumnListParser.OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from hint in MaterializationHintParser.OptionalOrDefault()
        from open in Token.EqualTo(SqlToken.LeftParen)
        from anchorQuery in SP.Ref(() => SelectStatementParser!)
        from recursivePart in (
            from unionKw in Token.EqualTo(SqlToken.Union)
            from allKw in Token.EqualTo(SqlToken.All)
            from recursiveQuery in SP.Ref(() => SelectStatementParser!)
            select (SelectStatement?)recursiveQuery
        ).OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        select new CommonTableExpression(
            GetTokenText(name),
            new SelectQueryExpression(anchorQuery),
            recursivePart,
            columnNames,
            IsRecursive: true,
            hint);

    /// <summary>
    /// The WITH clause: <c>WITH [RECURSIVE] cte1, cte2, ... SELECT ...</c>.
    /// Parses one or more comma-separated CTE definitions.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CommonTableExpression[]> WithClauseParser =
        from withKw in Token.EqualTo(SqlToken.With)
        from recursive in Token.EqualTo(SqlToken.Recursive).OptionalOrDefault()
        from ctes in SP.Ref(() => SingleCommonTableExpressionParser(recursive.HasValue))
            .ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select ctes;

    // ───────────────────── CROSS VALIDATE ─────────────────────

    /// <summary>
    /// Parses a named argument of the form <c>name = value</c> where name is a contextual
    /// identifier and value is a numeric literal.
    /// </summary>
    private static TokenListParser<SqlToken, (string Name, Expression Value)> NamedArg(string name) =>
        from n in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals(name, StringComparison.OrdinalIgnoreCase), name)
        from eq in Token.EqualTo(SqlToken.Equals)
        from value in SP.Ref(() => ExpressionParser!)
        select (name, value);

    /// <summary>
    /// Parses a CROSS VALIDATE clause:
    /// <c>CROSS VALIDATE(k = N [, seed = S]) ON key [STRATIFY BY col] [GROUP BY col] AS alias</c>.
    /// CROSS is a keyword token; VALIDATE and STRATIFY are contextual identifiers.
    /// </summary>
    private static readonly TokenListParser<SqlToken, CrossValidateClause> CrossValidateClauseParser =
        from cross in Token.EqualTo(SqlToken.Cross)
        from validate in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("VALIDATE", StringComparison.OrdinalIgnoreCase), "VALIDATE")
        from open in Token.EqualTo(SqlToken.LeftParen)
        from k in NamedArg("k")
        from seed in (
            from comma in Token.EqualTo(SqlToken.Comma)
            from s in NamedArg("seed")
            select s.Value
        ).AsNullable().OptionalOrDefault()
        from close in Token.EqualTo(SqlToken.RightParen)
        from onKw in Token.EqualTo(SqlToken.On)
        from keyColumns in (
            from lp in Token.EqualTo(SqlToken.LeftParen)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from rp in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>)cols
        ).Try().Or(
            SP.Ref(() => ExpressionParser!).Select(e => (IReadOnlyList<Expression>)new[] { e })
        )
        from stratifyColumns in (
            from stratifyKw in Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("STRATIFY", StringComparison.OrdinalIgnoreCase), "STRATIFY")
            from byKw in Token.EqualTo(SqlToken.By)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            select (IReadOnlyList<Expression>?)cols
        ).OptionalOrDefault()
        from groupColumns in (
            from groupKw in Token.EqualTo(SqlToken.Group)
            from byKw in Token.EqualTo(SqlToken.By)
            from cols in SP.Ref(() => ExpressionParser!).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            select (IReadOnlyList<Expression>?)cols
        ).OptionalOrDefault()
        from asKw in Token.EqualTo(SqlToken.As)
        from alias in IdentifierLike
        select new CrossValidateClause(k.Value, seed, keyColumns, stratifyColumns, groupColumns, GetTokenText(alias));

    // ───────────────────── SELECT statement ─────────────────────

    /// <summary>The core SELECT statement parser (without WITH preamble).</summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> SelectStatementParser =
        from selectKw in Token.EqualTo(SqlToken.Select)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from letOrDefine in LetOrDefineParser
        from columns in ColumnList
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from commaJoins in CommaJoinSourcesParser
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from crossValidateClause in CrossValidateClauseParser.AsNullable().Try().OptionalOrDefault()
        from groupByClause in GroupByClauseParser.OptionalOrDefault()
        from havingClause in HavingClauseParser.OptionalOrDefault()
        from qualifyClause in QualifyClauseParser.OptionalOrDefault()
        from assertions in AssertClausesParser
        from pivotClause in PivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from unpivotClause in UnpivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from orderByClause in OrderByClauseParser.OptionalOrDefault()
        from limitValue in LimitParser.OptionalOrDefault()
        from offsetValue in OffsetParser.OptionalOrDefault()
        select new SelectStatement(
            columns,
            fromClause,
            CombineJoins(commaJoins, joinClauses),
            whereClause,
            groupByClause,
            havingClause,
            qualifyClause,
            MergeAssertions(letOrDefine.Assertions, assertions),
            pivotClause,
            unpivotClause,
            orderByClause,
            limitValue,
            offsetValue,
            Distinct: distinct.HasValue,
            LetBindings: letOrDefine.LetBindings.Length > 0 ? letOrDefine.LetBindings : null,
            CrossValidate: crossValidateClause);

    /// <summary>
    /// Bare SELECT parser: same as <see cref="SelectStatementParser"/> but stops
    /// before ORDER BY, LIMIT, and OFFSET. Used by <see cref="QueryPrimary"/> so
    /// that trailing ORDER BY/LIMIT/OFFSET bind to the compound level rather
    /// than to an individual SELECT branch in set operations.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> BareSelectStatementParser =
        from selectKw in Token.EqualTo(SqlToken.Select)
        from distinct in Token.EqualTo(SqlToken.Distinct).OptionalOrDefault()
        from letOrDefine in LetOrDefineParser
        from columns in ColumnList
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from commaJoins in CommaJoinSourcesParser
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from crossValidateClause in CrossValidateClauseParser.AsNullable().Try().OptionalOrDefault()
        from groupByClause in GroupByClauseParser.OptionalOrDefault()
        from havingClause in HavingClauseParser.OptionalOrDefault()
        from qualifyClause in QualifyClauseParser.OptionalOrDefault()
        from assertions in AssertClausesParser
        from pivotClause in PivotClauseParser.AsNullable().Try().OptionalOrDefault()
        from unpivotClause in UnpivotClauseParser.AsNullable().Try().OptionalOrDefault()
        select new SelectStatement(
            columns,
            fromClause,
            CombineJoins(commaJoins, joinClauses),
            whereClause,
            groupByClause,
            havingClause,
            qualifyClause,
            MergeAssertions(letOrDefine.Assertions, assertions),
            pivotClause,
            unpivotClause,
            OrderBy: null,
            Limit: null,
            Offset: null,
            Distinct: distinct.HasValue,
            LetBindings: letOrDefine.LetBindings.Length > 0 ? letOrDefine.LetBindings : null,
            CrossValidate: crossValidateClause);

    /// <summary>
    /// Top-level statement parser: optional WITH clause followed by SELECT.
    /// The WITH clause's CTE definitions are threaded into the <see cref="SelectStatement"/>.
    /// Used only for backward-compatible direct statement parsing.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> StatementParser =
        from ctes in WithClauseParser.OptionalOrDefault()
        from statement in SelectStatementParser
        select ctes is not null && ctes.Length > 0
            ? statement with { CommonTableExpressions = ctes }
            : statement;

    /// <summary>
    /// Bare statement parser: optional WITH clause followed by a SELECT without
    /// trailing ORDER BY/LIMIT/OFFSET/INTO. Used for set operation branches.
    /// </summary>
    private static readonly TokenListParser<SqlToken, SelectStatement> BareStatementParser =
        from ctes in WithClauseParser.OptionalOrDefault()
        from statement in BareSelectStatementParser
        select ctes is not null && ctes.Length > 0
            ? statement with { CommonTableExpressions = ctes }
            : statement;

    // ───────────────────── Compound query (set operations) ─────────────────────

    /// <summary>
    /// A single SELECT statement (with optional WITH preamble) as a query primary.
    /// Uses the bare parser to avoid consuming trailing clauses that should bind
    /// to the compound level.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryPrimary =
        from statement in BareStatementParser
        select (QueryExpression)new SelectQueryExpression(statement);

    /// <summary>
    /// Parses a query term: one or more query primaries combined by INTERSECT [ALL].
    /// INTERSECT binds tighter than UNION/EXCEPT per SQL standard.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryTerm =
        from first in QueryPrimary
        from rest in (
            from intersectKw in Token.EqualTo(SqlToken.Intersect)
            from all in Token.EqualTo(SqlToken.All).OptionalOrDefault()
            from right in QueryPrimary
            select (All: all.HasValue, Right: right)
        ).Many()
        select rest.Aggregate(
            first,
            (left, pair) => new CompoundQueryExpression(
                left, SetOperationType.Intersect, pair.All, pair.Right));

    /// <summary>
    /// Parses a full compound query: one or more query terms combined by UNION [ALL] or EXCEPT [ALL].
    /// UNION and EXCEPT have equal precedence, lower than INTERSECT.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> CompoundQueryParser =
        from first in QueryTerm
        from rest in (
            from op in Token.EqualTo(SqlToken.Union).Value(SetOperationType.Union)
                .Or(Token.EqualTo(SqlToken.Except).Value(SetOperationType.Except))
            from all in Token.EqualTo(SqlToken.All).OptionalOrDefault()
            from right in QueryTerm
            select (OperationType: op, All: all.HasValue, Right: right)
        ).Many()
        select rest.Aggregate(
            first,
            (left, pair) => new CompoundQueryExpression(
                left, pair.OperationType, pair.All, pair.Right));

    /// <summary>
    /// Full query expression parser: compound query optionally followed by ORDER BY, LIMIT,
    /// and OFFSET that apply to the entire combined result. For a single SELECT without
    /// set operations, these trailing clauses are already parsed on the SelectStatement itself.
    /// </summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> QueryExpressionParser =
        from query in CompoundQueryParser
        from orderBy in OrderByClauseParser.OptionalOrDefault()
        from limit in LimitParser.OptionalOrDefault()
        from offset in OffsetParser.OptionalOrDefault()
        select ApplyTrailingClauses(query, orderBy, limit, offset);

}
