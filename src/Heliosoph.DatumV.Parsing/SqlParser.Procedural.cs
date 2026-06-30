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
    // ───────────────────── Procedural statement parsers ─────────────────────
    //
    // BEGIN/END, IF/ELSE, WHILE, FOR (counter and IN forms), DECLARE, SET.
    // Each is a top-level statement in its own right, dispatched by first
    // keyword in SingleStatementParser. Bodies recurse via
    // SP.Ref(() => SingleStatementParser!) — the closure is evaluated lazily
    // so static-field-init order doesn't matter.

    /// <summary>
    /// <c>SET @var = expr</c> — assignment to an existing variable. The SET
    /// token is shared with UPDATE's column-assignment clause; that overlap
    /// is harmless because UPDATE starts with the UPDATE keyword and only
    /// consumes SET as an interior token, while SetStatementParser starts
    /// with SET at the top level.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> SetStatementParser =
        from setKw in Token.EqualTo(SqlToken.Set)
        from name in IdentifierOrKeywordAsName
        from eq in Token.EqualTo(SqlToken.Equals)
        from value in SP.Ref(() => ExpressionParser!)
        select (Statement)new SetStatement(
            name,
            value,
            ToSpan(setKw));

    /// <summary>
    /// Variable-name slot of a DECLARE. Delegates to
    /// <see cref="IdentifierOrKeywordAsName"/>, but on failure inspects the
    /// next token: if it lexes as a SQL reserved keyword whose text reads
    /// like an identifier (<c>OFFSET</c>, <c>LIMIT</c>, <c>WHERE</c>, …), we
    /// throw a targeted <see cref="ParseException"/> at that token instead
    /// of letting the Superpower default surface a misleading
    /// "expected end" at the next nested block boundary further down. The
    /// failure mode without this — silent backtrack out of DECLARE followed
    /// by a downstream WHILE/IF parse error — costs ~30 min of bisection
    /// per occurrence to recognise as a reserved-word collision.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> DeclareVariableNameParser =
        input =>
        {
            TokenListParserResult<SqlToken, string> nameResult = IdentifierOrKeywordAsName(input);
            if (nameResult.HasValue) return nameResult;
            if (!input.IsAtEnd)
            {
                Token<SqlToken> head = input.ConsumeToken().Value;
                if (head.Kind != SqlToken.Identifier && LooksLikeIdentifier(head.ToStringValue()))
                {
                    string word = head.ToStringValue();
                    throw new ParseException(
                        $"'{word}' is a reserved keyword and cannot be used as a DECLARE variable name. " +
                        $"Rename it (e.g. '{word}_value') or double-quote it (\"{word}\").",
                        head.Position);
                }
            }
            return nameResult;
        };

    private static bool LooksLikeIdentifier(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!char.IsLetter(text[0]) && text[0] != '_') return false;
        for (int i = 1; i < text.Length; i++)
        {
            if (!char.IsLetterOrDigit(text[i]) && text[i] != '_') return false;
        }
        return true;
    }

    /// <summary>
    /// <c>DECLARE @var TypeName [= initializer]</c>. Type is required at
    /// parse time; type-inference from initializer is not yet supported.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DeclareStatementParser =
        from declareKw in Token.EqualTo(SqlToken.Declare)
        from name in DeclareVariableNameParser
        from typeName in TypeNameParser
        from initializer in (
            from eq in Token.EqualTo(SqlToken.Equals)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AsNullable().OptionalOrDefault()
        select (Statement)new DeclareStatement(
            name,
            typeName,
            initializer,
            ToSpan(declareKw));

    /// <summary>
    /// <c>BEGIN stmt[;] stmt[;] ... [;] END</c> — block of statements. Empty
    /// blocks (<c>BEGIN END</c>) are not supported — at least one statement
    /// is required, matching T-SQL. Statement separators (<c>;</c>) are
    /// optional, mirroring the top-level batch grammar; the trailing
    /// <c>;</c> before <c>END</c> is also optional.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> BlockStatementParser =
        from beginKw in Token.EqualTo(SqlToken.Begin)
        from first in SP.Ref(() => SingleStatementParser!)
        from rest in (
            from semi in Token.EqualTo(SqlToken.Semicolon).Many()
            from stmt in SP.Ref(() => SingleStatementParser!)
            select stmt
        ).Try().Many()
        from trailing in Token.EqualTo(SqlToken.Semicolon).Many()
        from endKw in Token.EqualTo(SqlToken.End)
        select (Statement)new BlockStatement(
            (IReadOnlyList<Statement>)(new[] { first }.Concat(rest).ToArray()),
            ToSpan(beginKw));

    /// <summary>
    /// <c>IF predicate then-stmt [ELSE else-stmt]</c>. <c>ELSE IF</c> falls
    /// out naturally when the else-statement is itself an
    /// <see cref="IfStatement"/> — no special syntactic form needed.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> IfStatementParser =
        from ifKw in Token.EqualTo(SqlToken.If)
        from predicate in SP.Ref(() => ExpressionParser!)
        from thenStmt in SP.Ref(() => SingleStatementParser!)
        from elseStmt in (
            from elseKw in Token.EqualTo(SqlToken.Else)
            from stmt in SP.Ref(() => SingleStatementParser!)
            select stmt
        ).AsNullable().OptionalOrDefault()
        select (Statement)new IfStatement(
            predicate,
            thenStmt,
            elseStmt,
            ToSpan(ifKw));

    /// <summary>
    /// <c>WHILE predicate body</c> — re-evaluates the predicate before each
    /// iteration. NULL predicate terminates the loop.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> WhileStatementParser =
        from whileKw in Token.EqualTo(SqlToken.While)
        from predicate in SP.Ref(() => ExpressionParser!)
        from body in SP.Ref(() => SingleStatementParser!)
        select (Statement)new WhileStatement(
            predicate,
            body,
            ToSpan(whileKw));

    /// <summary>
    /// Counter-FOR: <c>FOR @i = start TO end body</c>. Inclusive on both ends.
    /// STEP is not yet supported (defaults to 1); add it when a use case
    /// arises. Distinguished from <see cref="ForInStatementParser"/> by the
    /// <c>=</c> token after the variable.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ForCounterStatementParser =
        from forKw in Token.EqualTo(SqlToken.For)
        from name in IdentifierOrKeywordAsName
        from eq in Token.EqualTo(SqlToken.Equals)
        from start in SP.Ref(() => ExpressionParser!)
        from toKw in Token.EqualTo(SqlToken.To)
        from end in SP.Ref(() => ExpressionParser!)
        from body in SP.Ref(() => SingleStatementParser!)
        select (Statement)new ForCounterStatement(
            name,
            start,
            end,
            Step: null,
            body,
            ToSpan(forKw));

    /// <summary>
    /// Cursor-FOR: <c>FOR @row IN (query) body</c>. The source must be
    /// parenthesised — keeps disambiguation from counter-FOR cheap and
    /// matches how subqueries appear elsewhere.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ForInStatementParser =
        from forKw in Token.EqualTo(SqlToken.For)
        from name in IdentifierOrKeywordAsName
        from inKw in Token.EqualTo(SqlToken.In)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from source in SP.Ref(() => QueryExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        from body in SP.Ref(() => SingleStatementParser!)
        select (Statement)new ForInStatement(
            name,
            source,
            body,
            ToSpan(forKw));

    /// <summary>
    /// Dispatcher between counter-FOR and FOR-IN. Both forms share
    /// <c>FOR @var</c>; the next token (<c>=</c> for counter, <c>IN</c> for
    /// cursor) decides. <c>.Try()</c> on the counter parser backtracks to
    /// FOR-IN if the variable is followed by <c>IN</c> instead of <c>=</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ForStatementParser =
        ForCounterStatementParser.Try().Or(ForInStatementParser);

    /// <summary>
    /// <c>BREAK</c> — keyword-only statement; legality (must be inside a loop)
    /// is enforced at execution time, not parse time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> BreakStatementParser =
        from breakKw in Token.EqualTo(SqlToken.Break)
        select (Statement)new BreakStatement(ToSpan(breakKw));

    /// <summary>
    /// <c>RETURN expression</c> — yields the value of <see cref="ReturnStatement.Value"/>
    /// as the enclosing procedural function's scalar result and exits the body.
    /// Legality (must sit inside a procedural-UDF body) is enforced at execution
    /// time; the parser only recognises the shape so it can appear inside the
    /// <c>BEGIN…END</c> block of <see cref="CreateFunctionParser"/>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ReturnStatementParser =
        from returnKw in Token.EqualTo(SqlToken.Return)
        from value in SP.Ref(() => ExpressionParser!)
        select (Statement)new ReturnStatement(value, ToSpan(returnKw));

    /// <summary>
    /// <c>CONTINUE</c> — keyword-only statement; legality (must be inside a loop)
    /// is enforced at execution time, not parse time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ContinueStatementParser =
        from continueKw in Token.EqualTo(SqlToken.Continue)
        select (Statement)new ContinueStatement(ToSpan(continueKw));

    /// <summary>
    /// <c>PRINT expression</c> — emits a diagnostic string to the batch event
    /// stream. The expression is parsed eagerly, so anything valid in a SELECT
    /// projection works (literal, variable reference, scalar subquery,
    /// function call, etc.).
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> PrintStatementParser =
        from printKw in Token.EqualTo(SqlToken.Print)
        from value in SP.Ref(() => ExpressionParser!)
        select (Statement)new PrintStatement(value, ToSpan(printKw));

    /// <summary>
    /// <c>APPEND expr TO @list</c> — in-place append to a body-local
    /// <c>List&lt;T&gt;</c> accumulator. The value is parsed eagerly (any SELECT-
    /// projection expression); the target is a plain variable name. That the
    /// target is actually a list is checked at execution time, not parse time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AppendStatementParser =
        from appendKw in Token.EqualTo(SqlToken.Append)
        from value in SP.Ref(() => ExpressionParser!)
        from toKw in Token.EqualTo(SqlToken.To)
        from target in IdentifierOrKeywordAsName
        select (Statement)new AppendStatement(value, target, ToSpan(appendKw));

    /// <summary>
    /// <c>RESERVE expr FOR @list</c> — capacity hint for a body-local
    /// <c>List&lt;T&gt;</c> accumulator. Mirrors <see cref="AppendStatementParser"/>
    /// but uses <c>FOR</c> as the target preposition. List-ness of the target is
    /// enforced at execution time.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ReserveStatementParser =
        from reserveKw in Token.EqualTo(SqlToken.Reserve)
        from capacity in SP.Ref(() => ExpressionParser!)
        from forKw in Token.EqualTo(SqlToken.For)
        from target in IdentifierOrKeywordAsName
        select (Statement)new ReserveStatement(capacity, target, ToSpan(reserveKw));

    /// <summary>
    /// <c>ASSERT predicate [MESSAGE message-expr]</c> — procedural invariant
    /// check. Distinct from the SELECT-clause <c>ASSERT</c>: this form is a
    /// standalone statement, always aborts on failure, and does not support
    /// the per-row SKIP/ABORT mode. Catchable by an enclosing
    /// <c>TRY ... CATCH</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AssertStatementParser =
        from assertKw in Token.EqualTo(SqlToken.Assert)
        from predicate in SP.Ref(() => ExpressionParser!)
        from message in (
            from msgKw in Token.EqualTo(SqlToken.Message)
            from msgExpr in SP.Ref(() => ExpressionParser!)
            select msgExpr
        ).AsNullable().Try().OptionalOrDefault()
        select (Statement)new AssertStatement(predicate, message, ToSpan(assertKw));

    /// <summary>
    /// <c>RAISE expression</c> — explicitly throws an error from procedural
    /// code. The expression is evaluated and rendered to a string for the
    /// exception message; <c>RAISE @err</c> inside a catch rethrows the
    /// caught error.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> RaiseStatementParser =
        from raiseKw in Token.EqualTo(SqlToken.Raise)
        from message in SP.Ref(() => ExpressionParser!)
        select (Statement)new RaiseStatement(message, ToSpan(raiseKw));

    /// <summary>
    /// <c>TRY stmt CATCH @err stmt [FINALLY stmt]</c> — procedural exception
    /// handling, IF-flavored. Each body is a single statement; pair with
    /// <c>BEGIN ... END</c> for multi-statement bodies. The error variable
    /// is auto-declared in the catch body's scope and holds the caught
    /// exception's message.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> TryStatementParser =
        from tryKw in Token.EqualTo(SqlToken.Try)
        from tryBody in SP.Ref(() => SingleStatementParser!)
        from catchKw in Token.EqualTo(SqlToken.Catch)
        from errorVarName in IdentifierOrKeywordAsName
        from catchBody in SP.Ref(() => SingleStatementParser!)
        from finallyBody in (
            from finallyKw in Token.EqualTo(SqlToken.Finally)
            from body in SP.Ref(() => SingleStatementParser!)
            select body
        ).AsNullable().OptionalOrDefault()
        select (Statement)new TryStatement(
            tryBody,
            errorVarName,
            catchBody,
            finallyBody,
            ToSpan(tryKw));

    /// <summary>
    /// Parses <c>INSERT INTO name [(col, ...)] {SELECT … | VALUES (…), …} [RETURNING expr [, expr]*]</c>.
    /// The optional <c>RETURNING</c> clause turns the INSERT into a query that
    /// yields the resolved (post-DEFAULT, post-IDENTITY) inserted rows after
    /// the implicit commit completes — PostgreSQL semantics.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> InsertParser =
        from insertKw in Token.EqualTo(SqlToken.Insert)
        from intoKw in Token.EqualTo(SqlToken.Into)
        from qualifiedName in QualifiedTableNameParser
        from columnNames in (
            from open in Token.EqualTo(SqlToken.LeftParen)
            from names in IdentifierOrKeywordAsName.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select names
        ).AsNullable().OptionalOrDefault()
        from source in DefaultValuesSourceParser.Try()
            .Or(ValuesSourceParser.Select(v => (InsertSource)v).Try())
            .Or(SP.Ref(() => QueryExpressionParser!).Select(q => (InsertSource)new InsertQuerySource(q)))
        from returning in ReturningClauseParser.AsNullable().OptionalOrDefault()
        select (Statement)new InsertStatement(
            qualifiedName.TableName,
            columnNames is { Length: > 0 } ? columnNames : null,
            source,
            returning,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses the <c>DEFAULT VALUES</c> source form of an INSERT statement —
    /// PG-compatible shorthand for "insert one row, every column omitted".
    /// </summary>
    private static readonly TokenListParser<SqlToken, InsertSource> DefaultValuesSourceParser =
        from defaultKw in Token.EqualTo(SqlToken.Default)
        from valuesKw in Token.EqualTo(SqlToken.Values)
        select (InsertSource)new InsertDefaultValuesSource();

    /// <summary>
    /// Parses <c>RETURNING expr [, expr]*</c> — the projection list that the
    /// INSERT statement yields after committing. Reuses <see cref="ColumnList"/>
    /// so the surface (column references, computed expressions, <c>*</c>,
    /// table-qualified <c>t.*</c>, aliases) matches a SELECT projection.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<SelectColumn>> ReturningClauseParser =
        from returningKw in Token.EqualTo(SqlToken.Returning)
        from columns in ColumnList
        select (IReadOnlyList<SelectColumn>)columns;

    /// <summary>
    /// Parses <c>VALUES (expr, ...), (expr, ...) ...</c>.
    /// </summary>
    // `DEFAULT` keyword inside a VALUES row routes the slot through the
    // column's resolution path (same as omitting the column). Produces a
    // sentinel AST node that the InsertExecutor recognises per row.
    private static readonly TokenListParser<SqlToken, Expression> DefaultKeywordOrExpression =
        Token.EqualTo(SqlToken.Default).Value((Expression)new DefaultValueExpression())
            .Try()
            .Or(SP.Ref(() => ExpressionParser!));

    private static readonly TokenListParser<SqlToken, InsertValuesSource> ValuesSourceParser =
        from valuesKw in Token.EqualTo(SqlToken.Values)
        from rows in (
            from open in Token.EqualTo(SqlToken.LeftParen)
            from values in DefaultKeywordOrExpression.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
            from close in Token.EqualTo(SqlToken.RightParen)
            select (IReadOnlyList<Expression>)values
        ).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        select new InsertValuesSource(rows);

    /// <summary>
    /// Parses <c>UPDATE name [AS alias] SET col = expr [, ...] [FROM source [JOIN ...]*] [WHERE ...]</c>.
    /// Follows PostgreSQL semantics: the target table is not repeated in FROM; the WHERE clause
    /// contains both join conditions and filters.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> UpdateParser =
        from updateKw in Token.EqualTo(SqlToken.Update)
        from qualifiedName in QualifiedTableNameParser
        from alias in (
            from _as in Token.EqualTo(SqlToken.As).OptionalOrDefault()
            from aliasName in IdentifierLike.Select(GetTokenText)
            select aliasName
        ).Try().AsNullable().OptionalOrDefault()
        from setKw in Token.EqualTo(SqlToken.Set)
        from assignments in (
            from colName in IdentifierOrKeywordAsName
            from eq in Token.EqualTo(SqlToken.Equals)
            from value in SP.Ref(() => ExpressionParser!)
            select new ColumnAssignment(colName, value)
        ).ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from fromClause in FromClauseParser.AsNullable().OptionalOrDefault()
        from joinClauses in JoinClausesParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from returning in ReturningClauseParser.AsNullable().OptionalOrDefault()
        select (Statement)new UpdateStatement(
            qualifiedName.TableName,
            alias,
            assignments,
            fromClause,
            joinClauses.Length > 0 ? joinClauses : null,
            whereClause,
            SchemaName: qualifiedName.SchemaName,
            Returning: returning);

    /// <summary>
    /// Parses <c>DELETE FROM name [WHERE ...] [RETURNING expr [, expr]*]</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DeleteParser =
        from deleteKw in Token.EqualTo(SqlToken.Delete)
        from fromKw in Token.EqualTo(SqlToken.From)
        from qualifiedName in QualifiedTableNameParser
        from whereClause in WhereClauseParser.OptionalOrDefault()
        from returning in ReturningClauseParser.AsNullable().OptionalOrDefault()
        select (Statement)new DeleteStatement(
            qualifiedName.TableName,
            whereClause,
            qualifiedName.SchemaName,
            Returning: returning);

    /// <summary>
    /// Disambiguating prefix for <c>ALTER TABLE name</c>. <c>ALTER</c> is
    /// a unique starting keyword among DDL/DML statements, so the
    /// <c>.Try()</c> exists only to backtrack against the
    /// <c>QueryExpression</c> branch in <see cref="SingleStatementParser"/>;
    /// the body parsers run unprotected so deep failures (e.g., bad
    /// <c>DEFAULT</c> expression) propagate with their real
    /// <c>Remainder.Position</c>.
    /// </summary>
    /// <summary>
    /// Result of parsing the <c>ALTER TABLE [IF EXISTS] name</c> prefix.
    /// <see cref="IfExists"/> is the PG-canonical table-level guard that
    /// turns "no such table" into a no-op for every ALTER body.
    /// </summary>
    private readonly record struct AlterTablePrefixResult(bool IfExists, string TableName, string? SchemaName);

    private static readonly TokenListParser<SqlToken, AlterTablePrefixResult> AlterTablePrefix =
        (from alterKw in Token.EqualTo(SqlToken.Alter)
         from tableKw in Token.EqualTo(SqlToken.Table)
         from ifExists in IfExistsParser
         from qualifiedName in QualifiedTableNameParser
         select new AlterTablePrefixResult(ifExists, qualifiedName.TableName, qualifiedName.SchemaName))
        .Try();

    // Same order-independent alternation as CREATE TABLE columns.
    // PRIMARY KEY participates so users can write
    // `ALTER TABLE t ADD COLUMN id Int64 GENERATED ALWAYS AS IDENTITY PRIMARY KEY`
    // — the canonical "I forgot a PK" workflow.
    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> AlterAddColumnConstraintParser =
        NotNullConstraintParser
            .Or(BareNullConstraintParser)
            .Or(PrimaryKeyConstraintParser)
            .Or(DefaultConstraintParser)
            .Or(GeneratedConstraintParser)
            .Or(BareAsComputedConstraintParser)
            .Or(BareIdentityConstraintParser);

    /// <summary>
    /// Parses the <c>[COLUMN] col type [column_constraint …]</c> body of an
    /// <c>ALTER TABLE name ADD</c> statement once the <c>ADD</c> keyword
    /// has been consumed. Constraints may appear in any order; duplicates
    /// and conflicting nullability throw <see cref="ParseException"/> with
    /// a position anchored at the offending token.
    /// </summary>
    private static TokenListParser<SqlToken, Statement> AlterTableAddColumnBody(AlterTablePrefixResult prefix) =>
        from columnKw in Token.EqualTo(SqlToken.Column).OptionalOrDefault()
        from colName in IdentifierOrKeywordAsName
        from typeName in RequireColumnType(colName, "ALTER TABLE ADD COLUMN")
        from clauses in AlterAddColumnConstraintParser.Many()
        select FoldAlterAddColumnConstraints(prefix, colName, typeName, clauses);

    /// <summary>
    /// Folds the constraint-clause list into an
    /// <see cref="AlterTableAddColumnStatement"/>. Mirrors
    /// <see cref="FoldColumnConstraints"/> but without the PK slot since
    /// ALTER TABLE ADD COLUMN doesn't carry one in v1.
    /// </summary>
    private static Statement FoldAlterAddColumnConstraints(
        AlterTablePrefixResult prefix,
        string colName,
        string typeName,
        ColumnConstraintClause[] clauses)
    {
        bool? nullable = null;
        bool primaryKey = false;
        Expression? defaultValue = null;
        GeneratedSlotConstraint? generatedSlot = null;

        foreach (ColumnConstraintClause clause in clauses)
        {
            switch (clause)
            {
                case NullabilityConstraint nc:
                    if (nullable.HasValue)
                    {
                        string prior = nullable.Value ? "NULL" : "NOT NULL";
                        string current = nc.Nullable ? "NULL" : "NOT NULL";
                        string detail = nullable.Value == nc.Nullable
                            ? $"duplicate {current} constraint on column '{colName}'"
                            : $"conflicting nullability constraints on column '{colName}': {prior} already specified, cannot also specify {current}";
                        throw new ParseException(detail, nc.Position);
                    }
                    nullable = nc.Nullable;
                    break;

                case PrimaryKeyConstraint pkc:
                    if (primaryKey)
                    {
                        throw new ParseException(
                            $"duplicate PRIMARY KEY constraint on column '{colName}'",
                            pkc.Position);
                    }
                    primaryKey = true;
                    break;

                case DefaultConstraint dc:
                    if (defaultValue is not null)
                    {
                        throw new ParseException(
                            $"duplicate DEFAULT constraint on column '{colName}'",
                            dc.Position);
                    }
                    defaultValue = dc.Expression;
                    break;

                case GeneratedSlotConstraint gsc:
                    if (generatedSlot is not null)
                    {
                        throw new ParseException(
                            $"duplicate GENERATED / IDENTITY / computed-expression constraint on column '{colName}'",
                            gsc.Position);
                    }
                    generatedSlot = gsc;
                    break;
            }
        }

        // PRIMARY KEY implies NOT NULL when nullability wasn't given explicitly
        // (matches CREATE TABLE column-definition semantics).
        bool finalNullable = nullable ?? !primaryKey;

        return new AlterTableAddColumnStatement(
            prefix.TableName, colName, typeName,
            DefaultValue: defaultValue,
            Nullable: finalNullable,
            ComputedExpression: generatedSlot?.ComputedExpression,
            Identity: generatedSlot?.Identity,
            PrimaryKey: primaryKey,
            TableIfExists: prefix.IfExists,
            SchemaName: prefix.SchemaName);
    }

    /// <summary>
    /// Parses the <c>[COLUMN] [IF EXISTS] col</c> body of an
    /// <c>ALTER TABLE name DROP</c> statement once the <c>DROP</c>
    /// keyword has been consumed.
    /// </summary>
    private static TokenListParser<SqlToken, Statement> AlterTableDropColumnBody(AlterTablePrefixResult prefix) =>
        from columnKw in Token.EqualTo(SqlToken.Column).OptionalOrDefault()
        from ifExists in IfExistsParser
        from colName in IdentifierOrKeywordAsName
        select (Statement)new AlterTableDropColumnStatement(prefix.TableName, colName, ifExists, prefix.IfExists, prefix.SchemaName);

    /// <summary>
    /// Parses the <c>CONSTRAINT [IF EXISTS] constraint_name</c> body of an
    /// <c>ALTER TABLE name DROP</c> statement once the <c>DROP</c> keyword
    /// has been consumed. The <c>CONSTRAINT</c> token is consumed inside
    /// this parser (not by the caller) so the caller can <c>.Try()</c>
    /// over the whole thing to fall back to the drop-column body when no
    /// CONSTRAINT token is present.
    /// </summary>
    private static TokenListParser<SqlToken, Statement> AlterTableDropConstraintBody(AlterTablePrefixResult prefix) =>
        from constraintKw in Token.EqualTo(SqlToken.Constraint)
        from ifExists in IfExistsParser
        from constraintName in IdentifierOrKeywordAsName
        select (Statement)new AlterTableDropConstraintStatement(prefix.TableName, constraintName, ifExists, prefix.IfExists, prefix.SchemaName);

    /// <summary>
    /// Parses the column-attribute drop target after <c>DROP</c>: one of
    /// <c>IDENTITY</c>, <c>DEFAULT</c>, or <c>NOT NULL</c>. NOT NULL
    /// consumes two tokens so it's structurally distinct from the others.
    /// </summary>
    private static readonly TokenListParser<SqlToken, AlterColumnDropTarget> AlterColumnDropTargetParser =
        Token.EqualTo(SqlToken.Identity).Try().Select(_ => AlterColumnDropTarget.Identity)
            .Or(Token.EqualTo(SqlToken.Default).Try().Select(_ => AlterColumnDropTarget.Default))
            .Or((from notKw in Token.EqualTo(SqlToken.Not)
                 from nullKw in Token.EqualTo(SqlToken.Null)
                 select AlterColumnDropTarget.NotNull));

    /// <summary>
    /// Parses the column-attribute set target after <c>SET</c>: currently
    /// only <c>NOT NULL</c>. NOT NULL consumes two tokens, mirroring its
    /// shape on the DROP side.
    /// </summary>
    private static readonly TokenListParser<SqlToken, AlterColumnSetTarget> AlterColumnSetTargetParser =
        (from notKw in Token.EqualTo(SqlToken.Not)
         from nullKw in Token.EqualTo(SqlToken.Null)
         select AlterColumnSetTarget.NotNull);

    /// <summary>
    /// Parses the <c>COLUMN col { DROP target | SET target } [IF EXISTS]</c>
    /// body of an <c>ALTER TABLE name ALTER</c> statement, once the outer
    /// <c>ALTER</c> verb has been consumed. Dispatches DROP vs SET on the
    /// verb token; the IF EXISTS clause is DROP-only (PG accepts it on
    /// DROP IDENTITY, treats DROP DEFAULT as idempotent regardless, and
    /// has no equivalent for SET — a SET NOT NULL with an absent target
    /// is naturally idempotent since the destination state is the goal).
    /// </summary>
    private static TokenListParser<SqlToken, Statement> AlterTableAlterColumnBody(AlterTablePrefixResult prefix) =>
        from columnKw in Token.EqualTo(SqlToken.Column)
        from colName in IdentifierOrKeywordAsName
        from verb in Token.EqualTo(SqlToken.Drop).Try()
            .Or(Token.EqualTo(SqlToken.Set))
        from body in verb.Kind == SqlToken.Drop
            ? AlterColumnDropBody(prefix, colName)
            : AlterColumnSetBody(prefix, colName)
        select body;

    private static TokenListParser<SqlToken, Statement> AlterColumnDropBody(
        AlterTablePrefixResult prefix, string colName) =>
        from target in AlterColumnDropTargetParser
        from ifExists in IfExistsParser
        select (Statement)new AlterTableAlterColumnDropStatement(
            prefix.TableName, colName,
            target,
            ifExists,
            prefix.IfExists,
            prefix.SchemaName);

    private static TokenListParser<SqlToken, Statement> AlterColumnSetBody(
        AlterTablePrefixResult prefix, string colName) =>
        from target in AlterColumnSetTargetParser
        select (Statement)new AlterTableAlterColumnSetStatement(
            prefix.TableName, colName,
            target,
            prefix.IfExists,
            prefix.SchemaName);

    /// <summary>
    /// Parses <c>ALTER TABLE name (ADD ... | DROP ... | ALTER COLUMN ...)</c>.
    /// The prefix matches <c>ALTER TABLE name</c> as a Try-protected unit.
    /// After <c>DROP</c>, the drop-constraint body is tried first (it
    /// expects a <c>CONSTRAINT</c> token next); on miss the parser
    /// backtracks into the (legacy) drop-column body.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AlterTableParser =
        from prefix in AlterTablePrefix
        from verb in Token.EqualTo(SqlToken.Add).Try()
            .Or(Token.EqualTo(SqlToken.Drop).Try())
            .Or(Token.EqualTo(SqlToken.Alter))
        from body in verb.Kind switch
        {
            SqlToken.Add => AlterTableAddColumnBody(prefix),
            SqlToken.Alter => AlterTableAlterColumnBody(prefix),
            _ => AlterTableDropConstraintBody(prefix).Try()
                .Or(AlterTableDropColumnBody(prefix)),
        }
        select body;

    /// <summary>
    /// Parses a single statement: a DDL/DML command or a query expression.
    /// </summary>
    /// <summary>
    /// Parses <c>ANALYZE table</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> AnalyzeTableParser =
        from analyzeKw in Token.EqualTo(SqlToken.Analyze)
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new AnalyzeTableStatement(qualifiedName.TableName, qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>REINDEX [TABLE] name</c>. The optional <c>TABLE</c>
    /// keyword mirrors PostgreSQL's surface — useful for symmetry with
    /// <c>DROP TABLE</c> and to leave room for future <c>REINDEX
    /// DATABASE</c> / <c>REINDEX INDEX</c> variants.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ReindexTableParser =
        from reindexKw in Token.EqualTo(SqlToken.Reindex)
        from tableKw in Token.EqualTo(SqlToken.Table).Optional()
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new ReindexTableStatement(qualifiedName.TableName, qualifiedName.SchemaName);

    /// <summary>
    /// Parses a single statement: a DDL/DML command or a query expression.
    /// </summary>
    /// <remarks>
    /// The CREATE-* parsers (<see cref="CreateFunctionParser"/>,
    /// <see cref="CreateProcedureParser"/>, <see cref="CreateTableParser"/>)
    /// each have their own <c>.Try()</c>-protected prefix that disambiguates
    /// against the sibling CREATE-* alternatives — once a prefix matches, the
    /// rest of the parser runs without a surrounding <c>.Try()</c> so
    /// committed failures inside the body propagate with deep
    /// <c>Remainder.Position</c>. Superpower's <c>Or</c> picks the branch
    /// with the deepest remainder, so a parse error inside a procedural
    /// body surfaces at its real position rather than collapsing to
    /// "unexpected CREATE at column 1".
    /// </remarks>
    private static readonly TokenListParser<SqlToken, Statement> SingleStatementParser =
        CopyStatementParser
            .Or(CreateFunctionParser)
            .Or(DropFunctionParser.Try())
            .Or(CreateProcedureParser)
            .Or(DropProcedureParser.Try())
            .Or(CreateModelParser)
            .Or(DropModelParser.Try())
            .Or(CreateViewParser)
            .Or(DropViewParser.Try())
            .Or(EvictModelParser.Try())
            .Or(ResetCalibrationParser.Try())
            .Or(CallStatementParser.Try())
            // Procedural-flow statements: keyword-dispatched, all share the
            // SP.Ref() lazy-recursion pattern so bodies can themselves be any
            // statement (including another procedural form).
            .Or(BlockStatementParser.Try())
            .Or(IfStatementParser.Try())
            .Or(WhileStatementParser.Try())
            .Or(ForStatementParser.Try())
            .Or(BreakStatementParser.Try())
            .Or(ContinueStatementParser.Try())
            .Or(ReturnStatementParser.Try())
            .Or(PrintStatementParser.Try())
            .Or(AppendStatementParser.Try())
            .Or(ReserveStatementParser.Try())
            .Or(AssertStatementParser.Try())
            .Or(RaiseStatementParser.Try())
            .Or(TryStatementParser.Try())
            .Or(DeclareStatementParser.Try())
            .Or(SetSearchPathParser)
            .Or(SetStatementParser.Try())
            .Or(CreateSchemaParser)
            .Or(CreateTableParser)
            .Or(DropSchemaParser)
            .Or(DropTableParser.Try())
            .Or(CreateIndexParser.Try())
            .Or(DropIndexParser.Try())
            .Or(InsertParser.Try())
            .Or(UpdateParser.Try())
            .Or(DeleteParser.Try())
            .Or(AlterTableParser)
            .Or(AnalyzeTableParser.Try())
            .Or(ReindexTableParser.Try())
            .Or(QueryExpressionParser.Select(q => (Statement)new QueryStatement(q)));

    /// <summary>
    /// Parses a batch of statements. Statements are typically separated by
    /// <c>;</c>, but the separator is optional — block-terminated statements
    /// (anything ending with <c>END</c>) are common boundaries where forcing
    /// a trailing <c>;</c> reads as awkward. Each statement parser is greedy
    /// and keyword-anchored, so consecutive statements without a separator
    /// disambiguate cleanly: <c>SELECT 1 SELECT 2</c> parses as two
    /// statements, while <c>SELECT 1 + 2</c> parses as one (the <c>+</c>
    /// continues the SELECT's expression). Empty statements (extra
    /// semicolons) are silently ignored.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<Statement>> BatchParser =
        from first in SingleStatementParser
        from rest in (
            from semi in Token.EqualTo(SqlToken.Semicolon).Many()
            from stmt in SingleStatementParser
            select stmt
        ).Try().Many()
        from trailing in Token.EqualTo(SqlToken.Semicolon).Many()
        select (IReadOnlyList<Statement>)(new[] { first }.Concat(rest).ToArray());

    /// <summary>The full batch parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyList<Statement>> FullBatchParser =
        BatchParser.AtEnd();

    /// <summary>The full query expression parser that expects to consume all input.</summary>
    private static readonly TokenListParser<SqlToken, QueryExpression> FullParser =
        QueryExpressionParser.AtEnd();

    /// <summary>
    /// Set of tokens that begin top-level clauses. Used by the error-recovering
    /// parser to find the next safe synchronization point after a clause failure.
    /// </summary>
    private static readonly HashSet<SqlToken> ClauseStartTokens =
    [
        SqlToken.With,
        SqlToken.Select,
        SqlToken.From,
        SqlToken.Join,
        SqlToken.Inner,
        SqlToken.Left,
        SqlToken.Right,
        SqlToken.Full,
        SqlToken.Cross,
        SqlToken.Where,
        SqlToken.Group,
        SqlToken.Having,
        SqlToken.Qualify,
        SqlToken.Assert,
        SqlToken.Define,
        SqlToken.Pivot,
        SqlToken.Unpivot,
        SqlToken.Order,
        SqlToken.Limit,
        SqlToken.Offset,
        SqlToken.Union,
        SqlToken.Intersect,
        SqlToken.Except,
        SqlToken.Create,
        SqlToken.Drop,
        SqlToken.Insert,
        SqlToken.Update,
        SqlToken.Delete,
        SqlToken.Alter,
        SqlToken.Call,
    ];

}
