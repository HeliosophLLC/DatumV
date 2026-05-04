using System.Linq;
using DatumIngest.Parsing.Ast;
using DatumIngest.Parsing.Tokens;
using Superpower;
using Superpower.Model;
using Superpower.Parsers;
using SP = Superpower.Parse;

#pragma warning disable CS8603, CS8604, CS8620 // Superpower combinators lack consistent nullable reference type annotations

namespace DatumIngest.Parsing;

public static partial class SqlParser
{
    // ───────────────────── DDL / DML statements ─────────────────────

    /// <summary>
    /// Parses an identifier that may also be a keyword token in other contexts.
    /// DDL table/column names like "set", "table", "values" etc. are valid identifiers
    /// when they appear in name position. This parser accepts an <see cref="SqlToken.Identifier"/>
    /// or any keyword token that can legally serve as an unquoted name.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> IdentifierOrKeywordAsName =
        Token.EqualTo(SqlToken.Identifier).Select(GetTokenText)
            .Or(Token.EqualTo(SqlToken.Table).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Set).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Values).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Column).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Default).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Add).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.If).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Primary).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Key).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Analyze).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Reindex).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Generated).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Always).Select(t => t.ToStringValue()))
            // STEP / UNIT / COMMENT / CHECK are reserved on UDF / model
            // parameter declarations but still allowed as bare identifiers
            // elsewhere (variable names, column names, …); listing them here
            // keeps the regression-test suites that use `step`/`comment` as
            // table or column names working.
            .Or(Token.EqualTo(SqlToken.Step).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Unit).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Comment).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.Check).Select(t => t.ToStringValue()))
            // MESSAGE is reserved only for the ASSERT / ASSERT_TRUE message
            // sub-clause; it shows up as a parameter name on many built-in
            // assertion functions (assert_true(condition, message),
            // assert_equal(actual, expected, message), …) so callers using
            // PG-style named arguments expect `message := '…'` to parse.
            .Or(Token.EqualTo(SqlToken.Message).Select(t => t.ToStringValue()))
            // AT and END collide with built-in scalar / TVF parameter names
            // (Drawing functions' `at` slot for placement Point2D, line/range
            // functions' `end`). Reserved only for AT TIME ZONE / END of
            // CASE / END of BEGIN…END — none of which can appear inside a
            // function-call argument list — so accepting them as bare names
            // here is unambiguous and lets `draw_circle(at := …)` /
            // `range(start := 0, end := 10)` parse via the PG-style named-arg
            // syntax. (Audit 2026-05-26 against
            // ParameterSpec / VariadicSpec collisions; TypeKeyword catches
            // duration/image/date/time/etc. via its own catch-all branch.)
            .Or(Token.EqualTo(SqlToken.At).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.End).Select(t => t.ToStringValue()))
            .Or(Token.EqualTo(SqlToken.TypeKeyword).Select(t => t.ToStringValue()));

    /// <summary>
    /// Parses a column type name. Accepts a plain scalar identifier
    /// (<c>Int32</c>, <c>String</c>, <c>Time</c>, the PG aliases
    /// <c>VARCHAR</c> / <c>TEXT</c>), the angle-bracket array wrapper
    /// (<c>Array&lt;T&gt;</c>, recursive in syntax — rejected past one
    /// level at resolution time), and the postfix sugars:
    /// <list type="bullet">
    /// <item><c>T[]</c> — variable-length array, canonicalises to
    /// <c>Array&lt;T&gt;</c>.</item>
    /// <item><c>T[N]</c> — fixed-length array, canonicalises to
    /// <c>Array&lt;T&gt;(N)</c>.</item>
    /// <item><c>T(N)</c> / <c>T(N, M, …)</c> — width for strings,
    /// dimensionality for arrays. Composes with the wrapper form
    /// (<c>Array&lt;Float32&gt;(384)</c>) and with bare scalars
    /// (<c>VARCHAR(10)</c>).</item>
    /// </list>
    /// Returns the canonical string for downstream resolution.
    /// </summary>
    /// <summary>
    /// One field entry inside a <c>Struct&lt;...&gt;</c> annotation:
    /// <c>name TypeName</c> or <c>name: TypeName</c> in source form, both
    /// canonicalised to <c>"name: TypeName"</c> for the manifest string.
    /// Accepting the colon form mirrors the rendered output (so a user
    /// can read a hover string and paste it back into SQL) and matches
    /// most languages where field-type annotations use a colon. The type
    /// half is recursive (via <see cref="TypeNameParser"/>) so fields
    /// can be any shape — scalar, array, or nested struct.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> StructFieldEntryParser =
        from fieldName in Token.EqualTo(SqlToken.Identifier)
        from _ in Token.EqualTo(SqlToken.Colon).Optional()
        from fieldType in SP.Ref(() => TypeNameParser!)
        select $"{GetTokenText(fieldName)}: {fieldType}";

    private static readonly TokenListParser<SqlToken, string> TypeNameParser =
        from baseOrWrapper in
            // Struct field-list form first: `Struct<name Type, name Type, ...>`.
            // Distinct from the generic `Name<inner>` wrapper because the
            // inner is a comma-separated list of named fields, not a single
            // type expression. Canonical output uses `name: Type` (colon-
            // separated) to mirror the hover popup's display and to make
            // re-parsing trivial for downstream consumers (LanguageServer).
            // Try() so a bare `Struct` (no field list) still falls through
            // to the plain identifier path below.
            (
                from structKw in
                    Token.EqualTo(SqlToken.TypeKeyword)
                        .Or(Token.EqualTo(SqlToken.Identifier))
                        .Where(t => GetTokenText(t).Equals("Struct", StringComparison.OrdinalIgnoreCase), "Struct")
                from open in Token.EqualTo(SqlToken.LessThan)
                from fields in StructFieldEntryParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
                from close in Token.EqualTo(SqlToken.GreaterThan)
                select $"Struct<{string.Join(", ", fields)}>"
            ).Try()
            // Wrapper form next; backtrack to bare scalar when the
            // identifier isn't followed by '<' — the bare scalar grammar is
            // a strict prefix of the wrapper grammar.
            .Or((
                from name in Token.EqualTo(SqlToken.Identifier)
                from open in Token.EqualTo(SqlToken.LessThan)
                from inner in SP.Ref(() => TypeNameParser!)
                from close in Token.EqualTo(SqlToken.GreaterThan)
                select $"{GetTokenText(name)}<{inner}>"
            ).Try())
            // PG multi-word temporal forms:
            //   `TIMESTAMP WITH TIME ZONE`     → canonical "TimestampTz"
            //   `TIMESTAMP WITHOUT TIME ZONE`  → canonical "Timestamp"
            // The tail is matched eagerly; if it doesn't appear, the bare
            // `TIMESTAMP` token falls through to the standard TypeKeyword
            // branch below (which resolves to DataKind.Timestamp via
            // case-insensitive enum parse).
            .Or(
                (
                    from prefix in Token.EqualTo(SqlToken.TypeKeyword)
                        .Where(t => GetTokenText(t).Equals("TIMESTAMP", StringComparison.OrdinalIgnoreCase),
                            "TIMESTAMP")
                    from tail in TimestampZoneTail.Try()
                    select tail
                ).Try())
            .Or(
                Token.EqualTo(SqlToken.TypeKeyword)
                    .Or(Token.EqualTo(SqlToken.Identifier))
                    .Or(Token.EqualTo(SqlToken.Time))
                    .Select(GetTokenText))
        from suffix in TypeNameSuffixParser.AsNullable().OptionalOrDefault()
        select ApplyTypeNameSuffix(baseOrWrapper, suffix);

    /// <summary>
    /// PG multi-word time-zone suffix on TIMESTAMP / TIME. Matches either
    /// <c>WITH TIME ZONE</c> (→ <c>"TimestampTz"</c>) or
    /// <c>WITHOUT TIME ZONE</c> (→ <c>"Timestamp"</c>). The leading
    /// <c>TIMESTAMP</c> token is consumed by the caller.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string> TimestampZoneTail =
        (
            from with in Token.EqualTo(SqlToken.With)
            from time in Token.EqualTo(SqlToken.Time)
            from zone in Token.EqualTo(SqlToken.Zone)
            select "TimestampTz"
        )
        .Or(
            from without in Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("WITHOUT", StringComparison.OrdinalIgnoreCase),
                    "WITHOUT")
            from time in Token.EqualTo(SqlToken.Time)
            from zone in Token.EqualTo(SqlToken.Zone)
            select "Timestamp");

    /// <summary>
    /// One of the postfix shapes a type name may carry. Exactly one of
    /// <see cref="BracketDim"/> being captured (with <see cref="IsBracket"/>
    /// true) or <see cref="ParenDims"/> being non-null applies; the suffix
    /// parser produces this and <see cref="ApplyTypeNameSuffix"/> renders it
    /// onto the canonical string.
    /// </summary>
    private sealed record TypeNameSuffix(bool IsBracket, int? BracketDim, int[]? ParenDims);

    private static readonly TokenListParser<SqlToken, TypeNameSuffix> TypeNameSuffixParser =
        // Bracket form: `[]` (variable) or `[N]` (fixed).
        (
            from lb in Token.EqualTo(SqlToken.LeftBracket)
            from dim in Token.EqualTo(SqlToken.NumberLiteral)
                .Select(t => (int?)ParseDimensionLiteral(t.ToStringValue()))
                .OptionalOrDefault()
            from rb in Token.EqualTo(SqlToken.RightBracket)
            select new TypeNameSuffix(IsBracket: true, BracketDim: dim, ParenDims: null)
        )
        .Or(
            // Paren form: `(N)` or `(N, M, …)`.
            from open in Token.EqualTo(SqlToken.LeftParen)
            from first in Token.EqualTo(SqlToken.NumberLiteral)
                .Select(t => ParseDimensionLiteral(t.ToStringValue()))
            from rest in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from n in Token.EqualTo(SqlToken.NumberLiteral)
                    .Select(t => ParseDimensionLiteral(t.ToStringValue()))
                select n
            ).Many()
            from close in Token.EqualTo(SqlToken.RightParen)
            select new TypeNameSuffix(
                IsBracket: false,
                BracketDim: null,
                ParenDims: rest.Length == 0 ? [first] : [first, .. rest])
        );

    private static int ParseDimensionLiteral(string text)
    {
        double parsed = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        return checked((int)parsed);
    }

    private static string ApplyTypeNameSuffix(string baseOrWrapper, TypeNameSuffix? suffix)
    {
        if (suffix is null) return baseOrWrapper;

        if (suffix.IsBracket)
        {
            // `T[]` → variable-length array; `T[N]` → fixed-length array.
            return suffix.BracketDim is int n
                ? $"Array<{baseOrWrapper}>({n})"
                : $"Array<{baseOrWrapper}>";
        }

        // Paren form composes with whatever shape baseOrWrapper already has.
        // E.g. `Array<Float32>(384)` keeps the wrapper and appends.
        return $"{baseOrWrapper}({string.Join(",", suffix.ParenDims!)})";
    }

    /// <summary>
    /// Wraps <see cref="TypeNameParser"/> with a column-context error check.
    /// When the next token is one of the column-constraint keywords
    /// (<c>AS</c>, <c>DEFAULT</c>, <c>GENERATED</c>, <c>IDENTITY</c>,
    /// <c>NOT</c>, <c>PRIMARY</c>), Superpower's default failure surfaces as
    /// "unexpected as 'AS', expected identifier, typekeyword or time" — which
    /// leaks token-class names. Throw a friendlier message instead, anchored
    /// at the modifier token so editors highlight the right spot.
    /// </summary>
    /// <param name="columnName">The column being parsed, included in the message.</param>
    /// <param name="context">User-visible context label, e.g. "column" or "ALTER TABLE ADD COLUMN".</param>
    private static TokenListParser<SqlToken, string> RequireColumnType(string columnName, string context) =>
        input =>
        {
            if (!input.IsAtEnd)
            {
                Token<SqlToken> head = input.ConsumeToken().Value;
                string? modifier = ColumnTypeModifierLabel(head.Kind);
                if (modifier is not null)
                {
                    throw new ParseException(
                        $"{context} '{columnName}': missing column type before {modifier} clause.",
                        head.Position);
                }
            }
            return TypeNameParser(input);
        };

    private static string? ColumnTypeModifierLabel(SqlToken kind) => kind switch
    {
        SqlToken.As => "AS",
        SqlToken.Default => "DEFAULT",
        SqlToken.Generated => "GENERATED",
        SqlToken.Identity => "IDENTITY",
        SqlToken.Not => "NOT",
        SqlToken.Primary => "PRIMARY",
        _ => null,
    };

    /// <summary>
    /// A single column-constraint clause captured during column parsing.
    /// Clauses are folded into <see cref="ColumnDefinition"/> after collection
    /// so duplicates / conflicts can be rejected with a token-anchored
    /// <see cref="ParseException"/>. Postgres treats the constraint list as
    /// order-independent — see
    /// https://www.postgresql.org/docs/current/sql-createtable.html — so the
    /// parser collects clauses with <c>.Many()</c> rather than imposing a
    /// fixed sequence.
    /// </summary>
    private abstract record ColumnConstraintClause(Position Position);
    private sealed record NullabilityConstraint(bool Nullable, Position Position) : ColumnConstraintClause(Position);
    /// <summary>
    /// Column-level PRIMARY KEY clause, optionally prefixed by a user-supplied
    /// constraint name (<c>CONSTRAINT my_pk PRIMARY KEY</c>). The name is
    /// propagated up to <see cref="CreateTableStatement.PrimaryKeyConstraintName"/>;
    /// a null name means "derive the default".
    /// </summary>
    private sealed record PrimaryKeyConstraint(string? ConstraintName, Position Position) : ColumnConstraintClause(Position);
    private sealed record DefaultConstraint(Expression Expression, Position Position) : ColumnConstraintClause(Position);
    /// <summary>
    /// Holds whichever of (computed expression, identity spec) the clause
    /// produced. Both <c>GENERATED</c> forms and the two legacy bare forms
    /// (bare <c>AS (expr)</c>, bare <c>IDENTITY</c>) reduce to this single
    /// "generated slot" so duplicate-clause detection treats them uniformly.
    /// </summary>
    private sealed record GeneratedSlotConstraint(
        Expression? ComputedExpression,
        IdentitySpec? Identity,
        Position Position) : ColumnConstraintClause(Position);

    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> NotNullConstraintParser =
        from notKw in Token.EqualTo(SqlToken.Not)
        from nullKw in Token.EqualTo(SqlToken.Null)
        select (ColumnConstraintClause)new NullabilityConstraint(Nullable: false, notKw.Position);

    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> BareNullConstraintParser =
        from nullKw in Token.EqualTo(SqlToken.Null)
        select (ColumnConstraintClause)new NullabilityConstraint(Nullable: true, nullKw.Position);

    /// <summary>
    /// Optional <c>CONSTRAINT name</c> prefix shared by named column-level
    /// constraint forms. Returns the user-supplied name, or
    /// <see langword="null"/> when no prefix was given.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string?> OptionalConstraintNamePrefix =
        (from constraintKw in Token.EqualTo(SqlToken.Constraint)
         from name in IdentifierOrKeywordAsName
         select (string?)name)
        .Try()
        .OptionalOrDefault();

    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> PrimaryKeyConstraintParser =
        from constraintName in OptionalConstraintNamePrefix
        from primaryKw in Token.EqualTo(SqlToken.Primary)
        from keyKw in Token.EqualTo(SqlToken.Key)
        select (ColumnConstraintClause)new PrimaryKeyConstraint(constraintName, primaryKw.Position);

    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> DefaultConstraintParser =
        from defaultKw in Token.EqualTo(SqlToken.Default)
        from expr in SP.Ref(() => ExpressionParser!)
        select (ColumnConstraintClause)new DefaultConstraint(expr, defaultKw.Position);

    // PG-canonical `GENERATED …` clause. Disambiguation between the computed
    // and IDENTITY forms is delegated to the GeneratedAlways / ByDefault arms.
    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> GeneratedConstraintParser =
        from generatedKw in Token.EqualTo(SqlToken.Generated)
        from result in GeneratedAlwaysArm.Try().Or(GeneratedByDefaultArm)
        select (ColumnConstraintClause)new GeneratedSlotConstraint(
            result.ComputedExpression, result.Identity, generatedKw.Position);

    // Legacy bare `AS (expr)` — computed-column shorthand that pre-dates the
    // GENERATED clause. Kept for backward compatibility.
    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> BareAsComputedConstraintParser =
        from asKw in Token.EqualTo(SqlToken.As)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expr in SP.Ref(() => ExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select (ColumnConstraintClause)new GeneratedSlotConstraint(
            ComputedExpression: expr, Identity: null, asKw.Position);

    // Legacy bare `IDENTITY[(seed, step)]` — pre-dates the GENERATED clause.
    // Equivalent to `GENERATED ALWAYS AS IDENTITY` (rejects explicit values).
    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> BareIdentityConstraintParser =
        from identityKw in Token.EqualTo(SqlToken.Identity)
        from spec in IdentitySeedStepParser.AsNullable().OptionalOrDefault()
        select (ColumnConstraintClause)new GeneratedSlotConstraint(
            ComputedExpression: null,
            Identity: spec ?? new IdentitySpec(1, 1),
            identityKw.Position);

    // All first tokens are unique (NOT / NULL / PRIMARY / DEFAULT / GENERATED
    // / AS / IDENTITY) so plain `Or` is safe — no `.Try()` needed.
    private static readonly TokenListParser<SqlToken, ColumnConstraintClause> ColumnConstraintParser =
        NotNullConstraintParser
            .Or(BareNullConstraintParser)
            .Or(PrimaryKeyConstraintParser)
            .Or(DefaultConstraintParser)
            .Or(GeneratedConstraintParser)
            .Or(BareAsComputedConstraintParser)
            .Or(BareIdentityConstraintParser);

    /// <summary>
    /// Parses a single column definition:
    /// <c>name type [column_constraint …]</c>.
    /// Column constraints (<c>NULL</c> / <c>NOT NULL</c> / <c>PRIMARY KEY</c>
    /// / <c>DEFAULT expr</c> / <c>GENERATED …</c> / legacy bare <c>AS (expr)</c>
    /// / legacy bare <c>IDENTITY</c>) may appear in any order; duplicates and
    /// conflicting nullability are rejected at parse time with a position
    /// pointing at the offending token.
    /// The <c>DEFAULT</c> and <c>IDENTITY</c> clauses accept their inputs
    /// loosely here; the catalog enforces "literal only" / "integer column /
    /// at most one per table" at <c>CREATE TABLE</c> time so validation
    /// stays in one place rather than spread across the parser.
    /// </summary>
    private static readonly TokenListParser<SqlToken, ColumnDefinition> ColumnDefinitionParser =
        from name in IdentifierOrKeywordAsName
        from typeName in RequireColumnType(name, "column")
        from clauses in ColumnConstraintParser.Many()
        select FoldColumnConstraints(name, typeName, clauses);

    /// <summary>
    /// Folds a collected list of column-constraint clauses into a
    /// <see cref="ColumnDefinition"/>. Rejects duplicate / conflicting
    /// clauses with a <see cref="ParseException"/> anchored at the second
    /// occurrence so editors can pinpoint the user-fixable token.
    /// </summary>
    private static ColumnDefinition FoldColumnConstraints(
        string name,
        string typeName,
        ColumnConstraintClause[] clauses)
    {
        bool? nullable = null;
        Position nullabilityPosition = default;
        bool primaryKey = false;
        string? primaryKeyConstraintName = null;
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
                            ? $"duplicate {current} constraint on column '{name}'"
                            : $"conflicting nullability constraints on column '{name}': {prior} already specified, cannot also specify {current}";
                        throw new ParseException(detail, nc.Position);
                    }
                    nullable = nc.Nullable;
                    nullabilityPosition = nc.Position;
                    break;

                case PrimaryKeyConstraint pkc:
                    if (primaryKey)
                    {
                        throw new ParseException(
                            $"duplicate PRIMARY KEY constraint on column '{name}'",
                            pkc.Position);
                    }
                    primaryKey = true;
                    primaryKeyConstraintName = pkc.ConstraintName;
                    break;

                case DefaultConstraint dc:
                    if (defaultValue is not null)
                    {
                        throw new ParseException(
                            $"duplicate DEFAULT constraint on column '{name}'",
                            dc.Position);
                    }
                    defaultValue = dc.Expression;
                    break;

                case GeneratedSlotConstraint gsc:
                    if (generatedSlot is not null)
                    {
                        throw new ParseException(
                            $"duplicate GENERATED / IDENTITY / computed-expression constraint on column '{name}'",
                            gsc.Position);
                    }
                    generatedSlot = gsc;
                    break;
            }
        }

        // Explicit NOT NULL wins. Otherwise PRIMARY KEY implies NOT NULL.
        // Otherwise default to nullable (matches PG).
        bool finalNullable = nullable ?? !primaryKey;

        return new ColumnDefinition(
            name, typeName,
            Nullable: finalNullable,
            PrimaryKey: primaryKey,
            DefaultValue: defaultValue,
            Identity: generatedSlot?.Identity,
            ComputedExpression: generatedSlot?.ComputedExpression,
            PrimaryKeyConstraintName: primaryKeyConstraintName);
    }

    /// <summary>
    /// Parses the <c>IDENTITY[(seed, step)]</c> clause. Bare <c>IDENTITY</c>
    /// defaults to <c>(1, 1)</c>; the parametrized form requires both
    /// integer literals and accepts a leading <c>-</c> on each so
    /// negative steps / seeds parse cleanly.
    /// </summary>
    /// <summary>
    /// Parses a signed integer literal (<c>5</c> or <c>-5</c>) as a
    /// <see cref="long"/>. Used by <see cref="IdentityClauseParser"/>
    /// where the surrounding grammar is rigid (no general expression
    /// parsing) but negative seeds / steps must still be writable.
    /// </summary>
    private static readonly TokenListParser<SqlToken, long> SignedIntegerLiteral =
        from sign in Token.EqualTo(SqlToken.Minus).Value(true).OptionalOrDefault(false)
        from numberToken in Token.EqualTo(SqlToken.NumberLiteral)
        select ParseSignedInteger(numberToken.ToStringValue(), sign);

    private static long ParseSignedInteger(string text, bool negate)
    {
        double parsed = double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        long magnitude = checked((long)parsed);
        return negate ? checked(-magnitude) : magnitude;
    }

    private static readonly TokenListParser<SqlToken, IdentitySpec> IdentitySeedStepParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from seed in SignedIntegerLiteral
        from comma in Token.EqualTo(SqlToken.Comma)
        from step in SignedIntegerLiteral
        from close in Token.EqualTo(SqlToken.RightParen)
        select new IdentitySpec(seed, step);

    private static readonly TokenListParser<SqlToken, IdentitySpec> IdentityClauseParser =
        Token.EqualTo(SqlToken.Identity)
            .IgnoreThen(IdentitySeedStepParser.AsNullable().OptionalOrDefault())
            .Select(spec => spec ?? new IdentitySpec(1, 1));

    /// <summary>
    /// Holds either a computed expression or an IDENTITY spec — the two
    /// shapes a <c>GENERATED</c> clause can produce. Exactly one of the
    /// two fields is non-null.
    /// </summary>
    private sealed record GeneratedClauseResult(Expression? ComputedExpression, IdentitySpec? Identity);

    /// <summary>
    /// Parses the SQL:2003 / PostgreSQL <c>GENERATED</c> clause in one of
    /// three forms:
    /// <list type="bullet">
    ///   <item><c>GENERATED ALWAYS AS (expr)</c> — computed (STORED) column.</item>
    ///   <item><c>GENERATED ALWAYS AS IDENTITY [(seed, step)]</c> — IDENTITY column that rejects explicit values.</item>
    ///   <item><c>GENERATED BY DEFAULT AS IDENTITY [(seed, step)]</c> — IDENTITY column that accepts explicit values when supplied.</item>
    /// </list>
    /// Disambiguates between the computed and IDENTITY forms by looking at
    /// the token after <c>AS</c>: <c>(</c> for computed, the
    /// <c>IDENTITY</c> keyword for IDENTITY. <c>BY DEFAULT</c> only takes
    /// the IDENTITY form (PostgreSQL doesn't allow <c>BY DEFAULT</c> for
    /// computed-column expressions either).
    /// </summary>
    // Computed-expression body for `GENERATED ALWAYS AS (expr)` — the
    // paren-delimited expression form.
    private static readonly TokenListParser<SqlToken, GeneratedClauseResult> GeneratedComputedBodyParser =
        from open in Token.EqualTo(SqlToken.LeftParen)
        from expr in SP.Ref(() => ExpressionParser!)
        from close in Token.EqualTo(SqlToken.RightParen)
        select new GeneratedClauseResult(ComputedExpression: expr, Identity: null);

    // IDENTITY body for `GENERATED ALWAYS AS IDENTITY [(seed, step)]` —
    // the always-rejects-explicit form.
    private static readonly TokenListParser<SqlToken, GeneratedClauseResult> GeneratedAlwaysIdentityBodyParser =
        from spec in IdentityClauseParser
        select new GeneratedClauseResult(
            ComputedExpression: null,
            Identity: spec with { AcceptUserValues = false });

    // `GENERATED ALWAYS AS …` arm — picks computed vs identity by lookahead
    // on `(` (computed) or `IDENTITY` (identity).
    private static readonly TokenListParser<SqlToken, GeneratedClauseResult> GeneratedAlwaysArm =
        from alwaysKw in Token.EqualTo(SqlToken.Always)
        from asKw in Token.EqualTo(SqlToken.As)
        from body in GeneratedComputedBodyParser.Try().Or(GeneratedAlwaysIdentityBodyParser)
        select body;

    // `GENERATED BY DEFAULT AS IDENTITY [(seed, step)]` arm — IDENTITY-only,
    // accepts explicit values when supplied.
    private static readonly TokenListParser<SqlToken, GeneratedClauseResult> GeneratedByDefaultArm =
        from byKw in Token.EqualTo(SqlToken.By)
        from defaultKw in Token.EqualTo(SqlToken.Default)
        from asKw in Token.EqualTo(SqlToken.As)
        from spec in IdentityClauseParser
        select new GeneratedClauseResult(
            ComputedExpression: null,
            Identity: spec with { AcceptUserValues = true });

    private static readonly TokenListParser<SqlToken, GeneratedClauseResult> GeneratedClauseParser =
        from generatedKw in Token.EqualTo(SqlToken.Generated)
        from result in GeneratedAlwaysArm.Try().Or(GeneratedByDefaultArm)
        select result;

    /// <summary>
    /// Parses <c>IF NOT EXISTS</c> as an optional guard clause for CREATE statements.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> IfNotExistsParser =
        (from ifKw in Token.EqualTo(SqlToken.If)
         from notKw in Token.EqualTo(SqlToken.Not)
         from existsKw in Token.EqualTo(SqlToken.Exists)
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses <c>IF EXISTS</c> as an optional guard clause for DROP statements.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> IfExistsParser =
        (from ifKw in Token.EqualTo(SqlToken.If)
         from existsKw in Token.EqualTo(SqlToken.Exists)
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses a table-level <c>[CONSTRAINT name] PRIMARY KEY (col1, col2, ...)</c>
    /// constraint. The optional <c>CONSTRAINT name</c> prefix supplies a
    /// user-friendly PK name (matches PG); when absent, the catalog
    /// derives the default <c>&lt;table&gt;_pkey</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (string? Name, string[] Columns)> TablePrimaryKeyConstraintParser =
        from constraintName in OptionalConstraintNamePrefix
        from primaryKw in Token.EqualTo(SqlToken.Primary)
        from keyKw in Token.EqualTo(SqlToken.Key)
        from open in Token.EqualTo(SqlToken.LeftParen)
        from names in IdentifierOrKeywordAsName.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        select (constraintName, names);

    /// <summary>
    /// Parses a column definition list with an optional trailing table-level
    /// <c>PRIMARY KEY (col, ...)</c> constraint. Uses <c>.Try()</c> on each
    /// <c>(comma, column)</c> pair so that the comma before <c>PRIMARY KEY</c>
    /// is not greedily consumed by a column-definition lookahead.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (ColumnDefinition[] Columns, string[]? PrimaryKeyColumns, string? PrimaryKeyConstraintName)>
        ColumnListWithOptionalPrimaryKeyParser =
            from first in ColumnDefinitionParser
            from rest in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from column in ColumnDefinitionParser
                select column
            ).Try().Many()
            from primaryKey in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from constraint in TablePrimaryKeyConstraintParser
                select constraint
            ).Try().Select(t => ((string? Name, string[] Columns)?)t).OptionalOrDefault()
            select (new[] { first }.Concat(rest).ToArray(), primaryKey?.Columns, primaryKey?.Name);

    /// <summary>
    /// Parses <c>CREATE [TEMP|TEMPORARY] TABLE [IF NOT EXISTS] name (col type, ..., [PRIMARY KEY (col, ...)])</c>.
    /// The <c>TEMP</c>/<c>TEMPORARY</c> keyword is optional — all tables created in a
    /// session are temporary, so <c>CREATE TABLE</c> is accepted as a synonym.
    /// </summary>
    /// <summary>
    /// Disambiguating prefix for <c>CREATE [TEMP|TEMPORARY] TABLE</c>.
    /// Same pattern as <see cref="CreateFunctionPrefix"/> /
    /// <see cref="CreateProcedurePrefix"/> — wrapped in <c>.Try()</c>
    /// for backtracking against sibling CREATE-* parsers, with the rest
    /// of <see cref="CreateTableParser"/> running without an outer
    /// <c>.Try()</c> so column-list / AS-SELECT body failures propagate
    /// with deep Remainder.Position.
    /// </summary>
    /// <summary>
    /// Disambiguating prefix for <c>CREATE [TEMP|TEMPORARY] TABLE</c>.
    /// Returns whether the TEMP keyword was present so downstream parsing
    /// can build the right <see cref="CreateTableStatement"/> shape.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> CreateTablePrefix =
        (from createKw in Token.EqualTo(SqlToken.Create)
         from tempKw in Token.EqualTo(SqlToken.Temp).Or(Token.EqualTo(SqlToken.Temporary))
            .Select(_ => true).OptionalOrDefault()
         from tableKw in Token.EqualTo(SqlToken.Table)
         select tempKw)
        .Try();

    /// <summary>
    /// Parses a possibly-qualified table identifier — <c>name</c> or
    /// <c>schema.name</c> — into a <c>(schemaName, tableName)</c> tuple
    /// where <c>schemaName</c> is <see langword="null"/> for unqualified
    /// references. The caller decides what unqualified means (today:
    /// <c>public</c> for DDL; eventually <c>search_path</c>-driven for DML).
    /// Shared by CREATE / DROP / ALTER TABLE; the <c>FROM</c>-clause
    /// table-reference parser still has its own inline copy because it
    /// also handles alias / TABLESAMPLE trailers.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (string? SchemaName, string TableName)>
        QualifiedTableNameParser =
            from first in IdentifierOrKeywordAsName
            from rest in (
                from dot in Token.EqualTo(SqlToken.Dot)
                from second in IdentifierOrKeywordAsName
                select second
            ).OptionalOrDefault()
            select rest is null
                ? ((string?)null, first)
                : ((string?)first, rest!);

    /// <summary>
    /// Optional <c>AT 'path'</c> trailing clause on a CREATE TABLE
    /// statement. The catalog gates whether to honor it via the
    /// <c>AllowExplicitTablePaths</c> option; production hosts disable
    /// the clause entirely. <see langword="null"/> when the clause is
    /// absent.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string?> AtPathParser =
        (from atKw in Token.EqualTo(SqlToken.At)
         from path in Token.EqualTo(SqlToken.StringLiteral)
         select (string?)path.ToStringValue().Trim('\''))
        .OptionalOrDefault();

    private static readonly TokenListParser<SqlToken, Statement> CreateTableParser =
        from isTemp in CreateTablePrefix
        from ifNotExists in IfNotExistsParser
        from qualifiedName in QualifiedTableNameParser
        from asOrParen in Token.EqualTo(SqlToken.As).Try()
            .Or(Token.EqualTo(SqlToken.LeftParen))
        from statement in asOrParen.Kind == SqlToken.As
            ? (from query in SP.Ref(() => QueryExpressionParser!)
               from path in AtPathParser
               select (Statement)new CreateTableAsSelectStatement(
                   qualifiedName.TableName, query, IsTemp: isTemp, IfNotExists: ifNotExists, StoragePath: path))
            : ColumnListWithOptionalPrimaryKeyParser
                .Then(result => Token.EqualTo(SqlToken.RightParen)
                    .IgnoreThen(AtPathParser)
                    .Select(path =>
                    {
                        IReadOnlyList<string>? primaryKeyColumns = result.PrimaryKeyColumns;
                        // Table-level `CONSTRAINT name PRIMARY KEY (...)` wins
                        // over column-level `CONSTRAINT name PRIMARY KEY` when
                        // both are somehow present; the table-level form is
                        // the canonical place to name a constraint in PG.
                        string? pkConstraintName = result.PrimaryKeyConstraintName;
                        if (primaryKeyColumns is null)
                        {
                            List<string>? inlineKeys = null;
                            foreach (ColumnDefinition column in result.Columns)
                            {
                                if (column.PrimaryKey)
                                {
                                    inlineKeys ??= new List<string>();
                                    inlineKeys.Add(column.Name);
                                    // Column-level constraint name only
                                    // surfaces when no table-level name was
                                    // supplied. Multiple inline names
                                    // (degenerate composite PK with named
                                    // clauses on multiple columns) — last
                                    // one wins; the user should use the
                                    // table-level form instead.
                                    if (pkConstraintName is null && column.PrimaryKeyConstraintName is not null)
                                    {
                                        pkConstraintName = column.PrimaryKeyConstraintName;
                                    }
                                }
                            }
                            primaryKeyColumns = inlineKeys;
                        }
                        return (Statement)new CreateTableStatement(
                            qualifiedName.TableName, result.Columns,
                            IsTemp: isTemp, IfNotExists: ifNotExists,
                            PrimaryKeyColumns: primaryKeyColumns,
                            StoragePath: path,
                            PrimaryKeyConstraintName: pkConstraintName,
                            SchemaName: qualifiedName.SchemaName);
                    }))
        select statement;

    /// <summary>
    /// Parses <c>DROP TABLE [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropTableParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from tableKw in Token.EqualTo(SqlToken.Table)
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new DropTableStatement(qualifiedName.TableName, ifExists, qualifiedName.SchemaName);

    /// <summary>
    /// Matches a contextual <c>SCHEMA</c> identifier. <c>SCHEMA</c> isn't
    /// a reserved token in the tokenizer; we accept it as an identifier
    /// in name-position so it stays usable as a column / table name
    /// elsewhere.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Unit> SchemaKeyword =
        Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("SCHEMA", StringComparison.OrdinalIgnoreCase), "SCHEMA")
            .Select(_ => Unit.Value);

    /// <summary>
    /// Parses <c>CREATE SCHEMA [IF NOT EXISTS] name</c>. The
    /// <c>CREATE SCHEMA</c> prefix is Try-protected so it backtracks
    /// cleanly against sibling <c>CREATE TABLE</c> / <c>CREATE FUNCTION</c>
    /// / <c>CREATE PROCEDURE</c>; the body (name + IF NOT EXISTS) runs
    /// without Try so internal errors surface at their real positions.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateSchemaParser =
        from prefix in (
            from createKw in Token.EqualTo(SqlToken.Create)
            from schemaKw in SchemaKeyword
            select Unit.Value
        ).Try()
        from ifNotExists in IfNotExistsParser
        from name in IdentifierOrKeywordAsName
        select (Statement)new CreateSchemaStatement(name, ifNotExists);

    /// <summary>
    /// Parses <c>DROP SCHEMA [IF EXISTS] name [CASCADE | RESTRICT]</c>.
    /// The trailing CASCADE/RESTRICT is optional; absence means RESTRICT
    /// (the PG default — error if non-empty).
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropSchemaParser =
        from prefix in (
            from dropKw in Token.EqualTo(SqlToken.Drop)
            from schemaKw in SchemaKeyword
            select Unit.Value
        ).Try()
        from ifExists in IfExistsParser
        from name in IdentifierOrKeywordAsName
        from cascade in (
            Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("CASCADE", StringComparison.OrdinalIgnoreCase), "CASCADE")
                .Select(_ => true)
            .Or(Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("RESTRICT", StringComparison.OrdinalIgnoreCase), "RESTRICT")
                .Select(_ => false))
        ).OptionalOrDefault()
        select (Statement)new DropSchemaStatement(name, ifExists, cascade);

    /// <summary>
    /// Parses <c>SET search_path = schema1, schema2, ...</c> (or
    /// <c>TO</c> in place of <c>=</c>, mirroring Postgres). At least one
    /// schema is required.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> SetSearchPathParser =
        (from setKw in Token.EqualTo(SqlToken.Set)
         from searchPathKw in Token.EqualTo(SqlToken.Identifier)
             .Where(t => GetTokenText(t).Equals("search_path", StringComparison.OrdinalIgnoreCase), "search_path")
         from assign in Token.EqualTo(SqlToken.Equals).Or(Token.EqualTo(SqlToken.To))
         from first in IdentifierOrKeywordAsName
         from rest in (
             from comma in Token.EqualTo(SqlToken.Comma)
             from next in IdentifierOrKeywordAsName
             select next
         ).Many()
         select (Statement)new SetSearchPathStatement(
             new[] { first }.Concat(rest).ToList()))
        .Try();

    /// <summary>
    /// Optional <c>USING method</c> clause after the column list. The
    /// <c>USING</c> keyword is matched as a contextual identifier (no
    /// dedicated token); the method name is captured lowercased so the
    /// catalog matches case-insensitively.
    /// </summary>
    private static readonly TokenListParser<SqlToken, string?> CreateIndexUsingParser =
        (from usingKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("USING", StringComparison.OrdinalIgnoreCase), "USING")
         from method in Token.EqualTo(SqlToken.Identifier)
         select (string?)GetTokenText(method).ToLowerInvariant()
        ).Try().OptionalOrDefault();

    /// <summary>
    /// One <c>key = 'value'</c> pair inside a <c>WITH (...)</c> options
    /// list. Keys are lowercased; values are interpreted as string
    /// literals (single-quoted). The catalog validates which keys make
    /// sense for the index method.
    /// </summary>
    private static readonly TokenListParser<SqlToken, (string Key, string Value)> CreateIndexWithOptionParser =
        from key in Token.EqualTo(SqlToken.Identifier)
        from eq in Token.EqualTo(SqlToken.Equals)
        from value in Token.EqualTo(SqlToken.StringLiteral)
        select (GetTokenText(key).ToLowerInvariant(), GetTokenText(value));

    /// <summary>
    /// Optional <c>WITH (key = 'value', ...)</c> clause. Returns
    /// <see langword="null"/> when absent so the catalog can distinguish
    /// "no options given" from "empty options object" without an extra
    /// flag.
    /// </summary>
    private static readonly TokenListParser<SqlToken, IReadOnlyDictionary<string, string>?> CreateIndexWithOptionsParser =
        (from withKw in Token.EqualTo(SqlToken.With)
         from openParen in Token.EqualTo(SqlToken.LeftParen)
         from pairs in CreateIndexWithOptionParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
         from closeParen in Token.EqualTo(SqlToken.RightParen)
         select (IReadOnlyDictionary<string, string>?)pairs
             .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
        ).OptionalOrDefault();

    /// <summary>
    /// Parses <c>CREATE [UNIQUE] INDEX [IF NOT EXISTS] name ON table
    /// (col1[, col2]*) [USING method] [WITH (opt = 'value', ...)]</c>.
    /// The <c>CREATE</c> prefix is wrapped in <c>.Try()</c> at the dispatcher
    /// so sibling <c>CREATE</c> parsers can backtrack; the body runs without
    /// an outer <c>.Try()</c> so body-shape failures surface with deep
    /// Remainder positions. <c>UNIQUE</c> is optional and toggles
    /// <see cref="CreateIndexStatement.IsUnique"/>. The <c>USING</c> and
    /// <c>WITH</c> clauses default to <see langword="null"/> for back-compat
    /// with pre-FTS CREATE INDEX statements.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateIndexParser =
        from createKw in Token.EqualTo(SqlToken.Create)
        from uniqueKw in Token.EqualTo(SqlToken.Unique).Select(_ => true).OptionalOrDefault()
        from indexKw in Token.EqualTo(SqlToken.Index)
        from ifNotExists in IfNotExistsParser
        from indexName in IdentifierOrKeywordAsName
        from onKw in Token.EqualTo(SqlToken.On)
        from qualifiedName in QualifiedTableNameParser
        from openParen in Token.EqualTo(SqlToken.LeftParen)
        from columns in IdentifierOrKeywordAsName.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from closeParen in Token.EqualTo(SqlToken.RightParen)
        from method in CreateIndexUsingParser
        from options in CreateIndexWithOptionsParser
        select (Statement)new CreateIndexStatement(
            indexName, qualifiedName.TableName, columns, ifNotExists,
            IsUnique: uniqueKw,
            Method: method,
            Options: options,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>DROP INDEX [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropIndexParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from indexKw in Token.EqualTo(SqlToken.Index)
        from ifExists in IfExistsParser
        from indexName in IdentifierOrKeywordAsName
        select (Statement)new DropIndexStatement(indexName, ifExists);

    /// <summary>
    /// Parses a single UDF parameter declaration:
    /// <c>@name TYPE [IS NOT NULL] [= default-expr]</c>. The <c>@</c>-prefix
    /// matches how the parameter is referenced inside the body and lines up
    /// with procedural variable syntax. <c>IS NOT NULL</c> appears before the
    /// default expression because <c>expr IS NOT NULL</c> is itself a valid
    /// scalar predicate — placing the modifier after the default would create
    /// an ambiguity (does <c>= 0 IS NOT NULL</c> mean "default 0, must not
    /// be null" or "default to the literal predicate <c>0 IS NOT NULL</c>"?).
    /// </summary>
    private static readonly TokenListParser<SqlToken, UdfParameter> UdfParameterParser =
        from name in IdentifierOrKeywordAsName
        from typeName in TypeNameParser
        from isNotNull in (
            from isKw in Token.EqualTo(SqlToken.Is)
            from notKw in Token.EqualTo(SqlToken.Not)
            from nullKw in Token.EqualTo(SqlToken.Null)
            select true
        ).OptionalOrDefault()
        from defaultValue in (
            from eq in Token.EqualTo(SqlToken.Equals)
            from expr in SP.Ref(() => ExpressionParser!)
            select expr
        ).AsNullable().OptionalOrDefault()
        from checkExpr in (
            from kw in Token.EqualTo(SqlToken.Check)
            from openParen in Token.EqualTo(SqlToken.LeftParen)
            from expr in SP.Ref(() => ExpressionParser!)
            from closeParen in Token.EqualTo(SqlToken.RightParen)
            select expr
        ).AsNullable().OptionalOrDefault()
        from step in (
            from kw in Token.EqualTo(SqlToken.Step)
            from token in Token.EqualTo(SqlToken.NumberLiteral)
            select (decimal?)ParseDecimalLiteral(token.ToStringValue())
        ).OptionalOrDefault()
        from unit in (
            from kw in Token.EqualTo(SqlToken.Unit)
            from token in Token.EqualTo(SqlToken.StringLiteral)
            select (string?)UnquoteString(token)
        ).OptionalOrDefault()
        from description in (
            from kw in Token.EqualTo(SqlToken.Comment)
            from token in Token.EqualTo(SqlToken.StringLiteral)
            select (string?)UnquoteString(token)
        ).OptionalOrDefault()
        select new UdfParameter(
            name,
            typeName,
            isNotNull,
            defaultValue,
            checkExpr,
            step,
            unit,
            description);

    /// <summary>
    /// Parses a numeric literal token's text directly to <see cref="decimal"/>.
    /// Used by parameter-metadata clauses (<c>STEP 0.05</c>) where the
    /// surrounding parser already constrains the token shape; keeps the
    /// decimal exact through to the AST so author intent (e.g. <c>0.1</c>)
    /// survives round-tripping the catalog.
    /// </summary>
    private static decimal ParseDecimalLiteral(string text) =>
        decimal.Parse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Parses <c>OR REPLACE</c> or <c>OR ALTER</c> as an optional overwrite
    /// modifier for <c>CREATE FUNCTION</c> / <c>CREATE PROCEDURE</c>. Both
    /// spellings are accepted as synonyms — <c>OR REPLACE</c> follows the
    /// PostgreSQL convention, <c>OR ALTER</c> follows the T-SQL convention.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> OrReplaceParser =
        (from orKw in Token.EqualTo(SqlToken.Or)
         from overwriteKw in Token.EqualTo(SqlToken.Replace)
                                  .Or(Token.EqualTo(SqlToken.Alter))
         select true
        ).OptionalOrDefault();

    /// <summary>
    /// Parses
    /// <c>CREATE [OR REPLACE | OR ALTER] [PURE] FUNCTION [IF NOT EXISTS] name(@p1 TYPE [IS NOT NULL] [, @p2 TYPE [IS NOT NULL] ...]) [RETURNS TYPE [IS NOT NULL]] {AS expression | BEGIN ... RETURN expr; ... END}</c>.
    /// Two body shapes are accepted:
    /// <list type="bullet">
    ///   <item><description><b>Macro</b> (<c>AS expression</c>): the body is any
    ///   scalar expression and may reference the parameters as <c>@name</c>,
    ///   plus any normal column / function names — the planner inlines the
    ///   body at every call site so name resolution happens in the caller's
    ///   scope.</description></item>
    ///   <item><description><b>Procedural</b> (<c>BEGIN…END</c>): the body is a
    ///   sequence of procedural statements terminated by <c>RETURN expr</c>.
    ///   The body runs once per call against a fresh procedural frame —
    ///   <c>RETURNS T</c> is required so the type system has a concrete
    ///   return shape without analysing the body.</description></item>
    /// </list>
    /// The optional <c>PURE</c> modifier is meaningful for procedural UDFs
    /// (it asserts referential transparency, allowing CSE to dedupe call
    /// sites with identical arguments). <c>PURE</c> is rejected on macro
    /// bodies because macros are inlined and CSE already operates on the
    /// substituted expression.
    /// </summary>
    /// <summary>
    /// The disambiguating prefix for <c>CREATE FUNCTION</c>:
    /// <c>CREATE [OR REPLACE | OR ALTER] [PURE] FUNCTION</c>. Wrapped in
    /// <c>.Try()</c> so it backtracks cleanly when the input is actually
    /// <c>CREATE PROCEDURE</c> / <c>CREATE TEMP TABLE</c> / etc. — the
    /// <c>FUNCTION</c> token is what commits us. Once the prefix matches,
    /// the rest of <see cref="CreateFunctionParser"/> runs without a
    /// surrounding <c>.Try()</c>, so a parse error inside the body
    /// (e.g. <c>DECLARE x</c> missing the <c>@</c> prefix) propagates
    /// with deep <c>Remainder.Position</c> and surfaces at the bad
    /// statement instead of collapsing to "unexpected CREATE at column 1".
    /// </summary>
    private static readonly TokenListParser<SqlToken, (bool OrReplace, bool IsPure)> CreateFunctionPrefix =
        (from createKw in Token.EqualTo(SqlToken.Create)
         from orReplace in OrReplaceParser
         from isPure in (
             from pureKw in Token.EqualTo(SqlToken.Pure)
             select true
         ).OptionalOrDefault()
         from functionKw in Token.EqualTo(SqlToken.Function)
         select (orReplace, isPure))
        .Try();

    private static readonly TokenListParser<SqlToken, Statement> CreateFunctionParser =
        from prefix in CreateFunctionPrefix
        from ifNotExists in IfNotExistsParser
        // UDF names are permissive: accept any keyword that can serve as an
        // unquoted name (so e.g. CREATE FUNCTION add(...) doesn't trip on
        // ADD being tokenized as a keyword). The QualifiedTableNameParser
        // accepts both `fn` and `schema.fn` shapes — schema (when present)
        // lands in CreateFunctionStatement.SchemaName.
        from qualifiedName in QualifiedTableNameParser
        from open in Token.EqualTo(SqlToken.LeftParen)
        from parameters in UdfParameterParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from returnAnnotation in (
            from returnsKw in Token.EqualTo(SqlToken.Returns)
            from typeName in TypeNameParser
            from isNotNull in (
                from isKw in Token.EqualTo(SqlToken.Is)
                from notKw in Token.EqualTo(SqlToken.Not)
                from nullKw in Token.EqualTo(SqlToken.Null)
                select true
            ).OptionalOrDefault()
            select (TypeName: (string?)typeName, IsNotNull: isNotNull)
        ).OptionalOrDefault((TypeName: (string?)null, IsNotNull: false))
        from body in
            // Three body shapes: `AS expression` (macro), `AS BEGIN…END`
            // (T-SQL procedural), and `BEGIN…END` (bare procedural). The
            // dispatch commits as soon as enough lookahead is consumed to
            // disambiguate, so a parse error inside a BEGIN…END body
            // (e.g. `DECLARE x` missing the `@` prefix) propagates with
            // deep Remainder and surfaces at the bad statement rather
            // than collapsing to "unexpected AS / BEGIN" at the body
            // boundary.
            //
            // After AS, BlockStatementParser gets first dibs: it fails
            // without consuming when the next token isn't BEGIN, so the
            // inner `.Or(ExpressionParser)` falls through cleanly to the
            // macro form. Once BEGIN is consumed, BlockStatementParser
            // commits — any inner failure is committed and propagates
            // upward.
            (from asKw in Token.EqualTo(SqlToken.As)
             from result in SP.Ref(() => BlockStatementParser!).Select(blk =>
                     (Expr: (Expression?)null,
                      Stmts: (IReadOnlyList<Statement>?)((BlockStatement)blk).Statements))
                 .Or(SP.Ref(() => ExpressionParser!).Select(expr =>
                     (Expr: (Expression?)expr,
                      Stmts: (IReadOnlyList<Statement>?)null)))
             select result)
            // Bare BEGIN…END (no AS). This branch only fires when the first
            // post-RETURNS token is BEGIN; the AS-led branch above already
            // failed without consuming (no AS).
            .Or(from blk in SP.Ref(() => BlockStatementParser!)
                select (Expr: (Expression?)null,
                        Stmts: (IReadOnlyList<Statement>?)((BlockStatement)blk).Statements))
        select (Statement)BuildCreateFunctionStatement(
            qualifiedName.TableName,
            parameters,
            returnAnnotation.TypeName,
            returnAnnotation.IsNotNull,
            expressionBody: body.Expr,
            statementBody: body.Stmts,
            prefix.IsPure,
            ifNotExists,
            prefix.OrReplace,
            schemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Constructs a <see cref="CreateFunctionStatement"/> from the parsed
    /// pieces, applying body-shape-specific validation that the grammar
    /// can't express directly:
    /// <list type="bullet">
    ///   <item><description><c>PURE</c> requires a procedural body (macros are
    ///   inlined; CSE on the substituted expression already deduplicates
    ///   identical macro call sites).</description></item>
    ///   <item><description>Procedural bodies must declare <c>RETURNS T</c>
    ///   — without it the type system has no return shape because the body
    ///   is opaque to the planner.</description></item>
    ///   <item><description>Procedural bodies must end with a
    ///   <see cref="ReturnStatement"/> at the top level so the function has a
    ///   defined scalar result on every path the body completes.</description></item>
    ///   <item><description>Procedural bodies must not contain top-level
    ///   <see cref="QueryStatement"/>s (bare <c>SELECT</c> producing rows).
    ///   Subqueries in expressions remain legal — only result-emitting
    ///   statements are rejected.</description></item>
    /// </list>
    /// </summary>
    private static CreateFunctionStatement BuildCreateFunctionStatement(
        string name,
        IReadOnlyList<UdfParameter> parameters,
        string? returnTypeName,
        bool returnIsNotNull,
        Expression? expressionBody,
        IReadOnlyList<Statement>? statementBody,
        bool isPure,
        bool ifNotExists,
        bool orReplace,
        string? schemaName = null)
    {
        if (statementBody is not null)
        {
            if (returnTypeName is null)
            {
                throw new FormatException(
                    $"CREATE FUNCTION {name}: procedural functions (BEGIN…END body) " +
                    "must declare a return type with RETURNS T.");
            }

            ValidateProceduralBody(name, statementBody);
        }
        else if (isPure)
        {
            throw new FormatException(
                $"CREATE FUNCTION {name}: PURE applies only to procedural functions " +
                "(BEGIN…END body); macro UDFs (AS expression) are inlined and CSE " +
                "already deduplicates identical call sites.");
        }

        return new CreateFunctionStatement(
            name,
            parameters,
            returnTypeName,
            ExpressionBody: expressionBody,
            StatementBody: statementBody,
            IsPure: isPure,
            IfNotExists: ifNotExists,
            OrReplace: orReplace,
            Span: null,
            ReturnIsNotNull: returnIsNotNull,
            SchemaName: schemaName);
    }

    /// <summary>
    /// Walks the procedural-body statement sequence and rejects shapes that
    /// can't be a scalar function: top-level <c>SELECT</c> statements (which
    /// would produce rows the function has no place to send) and bodies that
    /// don't end in a <see cref="ReturnStatement"/> on every path. The walk
    /// is shallow on the top level and recurses into <c>IF</c> branches so
    /// "every branch ends with RETURN" is enforced for simple control flow;
    /// loops and TRY blocks are accepted as terminators when the function
    /// body's last statement is a <c>RETURN</c>.
    /// </summary>
    private static void ValidateProceduralBody(string name, IReadOnlyList<Statement> body)
    {
        if (body.Count == 0)
        {
            throw new FormatException(
                $"CREATE FUNCTION {name}: procedural function body is empty; " +
                "a RETURN statement is required.");
        }

        foreach (Statement stmt in body)
        {
            RejectQueryStatementsRecursively(name, stmt);
        }

        if (!IsTerminatingStatement(body[^1]))
        {
            throw new FormatException(
                $"CREATE FUNCTION {name}: every control-flow path through the body " +
                "must end with RETURN expr (the last top-level statement must be a " +
                "RETURN, a BEGIN…END whose final statement RETURNs, or an IF whose " +
                "branches all RETURN).");
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="stmt"/> guarantees
    /// the procedural function body's control flow exits via <c>RETURN</c>.
    /// Three shapes qualify: a literal <see cref="ReturnStatement"/>; a
    /// <see cref="BlockStatement"/> whose final statement is itself
    /// terminating; and an <see cref="IfStatement"/> where both the
    /// <c>Then</c> and <c>Else</c> branches are terminating (so neither path
    /// can fall through to whatever sits after the IF). Loops and TRY blocks
    /// are deliberately excluded — a <c>WHILE</c> body might never execute,
    /// and a <c>TRY</c>'s exception path is hard to reason about at parse
    /// time.
    /// </summary>
    private static bool IsTerminatingStatement(Statement stmt)
    {
        return stmt switch
        {
            ReturnStatement => true,
            BlockStatement block => block.Statements.Count > 0
                && IsTerminatingStatement(block.Statements[^1]),
            IfStatement ifStmt => ifStmt.Else is not null
                && IsTerminatingStatement(ifStmt.Then)
                && IsTerminatingStatement(ifStmt.Else),
            _ => false,
        };
    }

    /// <summary>
    /// Recursively walks a procedural statement and throws when it encounters
    /// a top-level <see cref="QueryStatement"/>. Subquery expressions inside
    /// <c>RETURN</c>, <c>SET</c>, <c>IF</c> predicates, etc. are not visited
    /// — only statement-position SELECTs are rejected.
    /// </summary>
    private static void RejectQueryStatementsRecursively(string name, Statement stmt)
    {
        switch (stmt)
        {
            case QueryStatement:
                throw new FormatException(
                    $"CREATE FUNCTION {name}: top-level SELECT statements are not allowed " +
                    "in procedural function bodies (a function returns one scalar value, " +
                    "not a result set). Use SELECT in expression position instead, e.g. " +
                    "RETURN (SELECT ...).");
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    RejectQueryStatementsRecursively(name, inner);
                }
                break;
            case IfStatement ifStmt:
                RejectQueryStatementsRecursively(name, ifStmt.Then);
                if (ifStmt.Else is not null)
                {
                    RejectQueryStatementsRecursively(name, ifStmt.Else);
                }
                break;
            case WhileStatement whileStmt:
                RejectQueryStatementsRecursively(name, whileStmt.Body);
                break;
            case ForCounterStatement forC:
                RejectQueryStatementsRecursively(name, forC.Body);
                break;
            case ForInStatement forIn:
                RejectQueryStatementsRecursively(name, forIn.Body);
                break;
            case TryStatement tryStmt:
                RejectQueryStatementsRecursively(name, tryStmt.TryBody);
                RejectQueryStatementsRecursively(name, tryStmt.CatchBody);
                if (tryStmt.FinallyBody is not null)
                {
                    RejectQueryStatementsRecursively(name, tryStmt.FinallyBody);
                }
                break;
        }
    }

    /// <summary>
    /// Parses <c>DROP FUNCTION [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropFunctionParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from functionKw in Token.EqualTo(SqlToken.Function)
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new DropFunctionStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses
    /// <c>CREATE [OR REPLACE | OR ALTER] PROCEDURE [IF NOT EXISTS] name(@p1 TYPE [IS NOT NULL], ...) AS BEGIN ... END</c>.
    /// The body is required to be a <c>BEGIN ... END</c> block — procedures
    /// are about composing multiple statements, so a single-statement body
    /// would defeat the point. Parameters use the same <see cref="UdfParameterParser"/>
    /// shape as UDFs, including the <c>@</c>-prefix and optional
    /// <c>IS NOT NULL</c> annotation.
    /// </summary>
    /// <summary>
    /// Disambiguating prefix for <c>CREATE PROCEDURE</c>:
    /// <c>CREATE [OR REPLACE | OR ALTER] PROCEDURE</c>. Same pattern as
    /// <see cref="CreateFunctionPrefix"/> — wrapped in <c>.Try()</c> so
    /// it backtracks cleanly when the input is actually CREATE FUNCTION
    /// or CREATE TEMP TABLE; the rest of <see cref="CreateProcedureParser"/>
    /// runs without a surrounding <c>.Try()</c> so body failures
    /// (BEGIN…END parse errors) propagate with deep Remainder.Position.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> CreateProcedurePrefix =
        (from createKw in Token.EqualTo(SqlToken.Create)
         from orReplace in OrReplaceParser
         from procedureKw in Token.EqualTo(SqlToken.Procedure)
         select orReplace)
        .Try();

    private static readonly TokenListParser<SqlToken, Statement> CreateProcedureParser =
        from orReplace in CreateProcedurePrefix
        from ifNotExists in IfNotExistsParser
        from qualifiedName in QualifiedTableNameParser
        from open in Token.EqualTo(SqlToken.LeftParen)
        from parameters in UdfParameterParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        from asKw in Token.EqualTo(SqlToken.As)
        from body in SP.Ref(() => BlockStatementParser!)
        select (Statement)new CreateProcedureStatement(
            qualifiedName.TableName,
            parameters,
            (BlockStatement)body,
            ifNotExists,
            orReplace,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>DROP PROCEDURE [IF EXISTS] name</c>.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropProcedureParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from procedureKw in Token.EqualTo(SqlToken.Procedure)
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new DropProcedureStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// <c>CREATE [OR REPLACE] VIEW</c> prefix. VIEW is recognised as a
    /// contextual identifier (same pattern as MODEL) so user identifiers
    /// named <c>view</c> still parse outside this position.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> CreateViewPrefix =
        (from createKw in Token.EqualTo(SqlToken.Create)
         from orReplace in OrReplaceParser
         from viewKw in Token.EqualTo(SqlToken.Identifier)
             .Where(t => GetTokenText(t).Equals("VIEW", StringComparison.OrdinalIgnoreCase), "VIEW")
         select orReplace)
        .Try();

    /// <summary>
    /// Parses <c>CREATE [OR REPLACE] VIEW [IF NOT EXISTS] name AS SELECT ...</c>.
    /// The body is a single <see cref="SelectStatement"/> — compound
    /// queries (UNION / INTERSECT) aren't supported through views yet.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateViewParser =
        from orReplace in CreateViewPrefix
        from ifNotExists in IfNotExistsParser
        from qualifiedName in QualifiedTableNameParser
        from asKw in Token.EqualTo(SqlToken.As)
        from body in SP.Ref(() => SelectStatementParser!)
        select (Statement)new CreateViewStatement(
            qualifiedName.TableName,
            body,
            ifNotExists,
            orReplace,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>DROP VIEW [IF EXISTS] name</c>. VIEW is a contextual
    /// identifier; outside this position the bare token <c>view</c>
    /// remains a usable name.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropViewParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        from viewKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("VIEW", StringComparison.OrdinalIgnoreCase), "VIEW")
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new DropViewStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// <c>CREATE [OR REPLACE] MODEL</c> prefix. Mirrors
    /// <see cref="CreateProcedurePrefix"/> with the MODEL keyword; the
    /// <c>.Try()</c> wrapper backs off when the input is actually a
    /// different CREATE statement so the dispatcher's <c>.Or()</c> chain
    /// can route correctly.
    /// </summary>
    private static readonly TokenListParser<SqlToken, bool> CreateModelPrefix =
        (from createKw in Token.EqualTo(SqlToken.Create)
         from orReplace in OrReplaceParser
         // MODEL is a contextual identifier (mirrors USING) — keeping it
         // as a regular identifier outside this position lets tables,
         // columns, and aliases named `model` continue to parse.
         from modelKw in Token.EqualTo(SqlToken.Identifier)
             .Where(t => GetTokenText(t).Equals("MODEL", StringComparison.OrdinalIgnoreCase), "MODEL")
         select orReplace)
        .Try();

    /// <summary>
    /// Parses <c>CREATE [OR REPLACE] MODEL [IF NOT EXISTS] name(args)
    /// RETURNS T USING 'path' [AS] BEGIN ... END</c>. The body shape is
    /// always procedural — there's no expression-body form because the
    /// only valid use of a model body is to call <c>infer()</c>, which
    /// requires a procedural context to bind to.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CreateModelParser =
        from orReplace in CreateModelPrefix
        from ifNotExists in IfNotExistsParser
        from qualifiedName in QualifiedTableNameParser
        from open in Token.EqualTo(SqlToken.LeftParen)
        from parameters in UdfParameterParser.ManyDelimitedBy(Token.EqualTo(SqlToken.Comma))
        from close in Token.EqualTo(SqlToken.RightParen)
        // RETURNS is required for models — the planner needs a known
        // scalar shape, and a model body without a return type couldn't
        // dispatch through `infer()` meaningfully.
        from returnsKw in Token.EqualTo(SqlToken.Returns)
        from returnTypeName in TypeNameParser
        from returnIsNotNull in (
            from isKw in Token.EqualTo(SqlToken.Is)
            from notKw in Token.EqualTo(SqlToken.Not)
            from nullKw in Token.EqualTo(SqlToken.Null)
            select true
        ).OptionalOrDefault()
        // IMPLEMENTS is a contextual identifier (same pattern as USING),
        // followed by a bare identifier naming the task contract this
        // model satisfies (e.g. ImageClassifier, TextEmbedder). Optional;
        // when absent the model registers with task = NULL and skips the
        // contract enforcement. Listed before USING so the surface reads
        // "interface metadata, then storage."
        from implementsTask in (
            from implementsKw in Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("IMPLEMENTS", StringComparison.OrdinalIgnoreCase), "IMPLEMENTS")
            from taskName in Token.EqualTo(SqlToken.Identifier)
            select (string?)GetTokenText(taskName)
        ).OptionalOrDefault()
        // USING is a contextual identifier (matches existing
        // CREATE INDEX USING pattern). Three accepted shapes:
        //   - Single-session legacy:  USING 'path'
        //   - Multi-session aliased:  USING 'a' AS x, 'b' AS y[, ...]
        //   - Absent:                  no USING clause at all — the body
        //     produces its result by delegating to another model or a UDF
        //     and binds no sessions of its own.
        // The parser captures the full list in `usingEntries`; legacy
        // single-string form is a one-entry list with the implicit
        // "default" alias, kept null on the Alias field so the registrar
        // can route through the same back-compat path as today. The
        // absent form yields an empty list which BuildCreateModelStatement
        // translates to UsingPath: null + UsingFiles: null.
        from usingEntries in (
            from usingKw in Token.EqualTo(SqlToken.Identifier)
                .Where(t => GetTokenText(t).Equals("USING", StringComparison.OrdinalIgnoreCase), "USING")
            from entries in ParseUsingEntries()
            select entries
        ).Try().OptionalOrDefault((IReadOnlyList<(string Path, string? Alias)>)Array.Empty<(string, string?)>())
        // Body: optionally preceded by AS, always a BEGIN…END block.
        from body in
            (from asKw in Token.EqualTo(SqlToken.As)
             from blk in SP.Ref(() => BlockStatementParser!)
             select ((BlockStatement)blk).Statements)
            .Or(from blk in SP.Ref(() => BlockStatementParser!)
                select ((BlockStatement)blk).Statements)
        select (Statement)BuildCreateModelStatement(
            qualifiedName.TableName,
            parameters,
            returnTypeName,
            usingEntries,
            body,
            ifNotExists,
            orReplace,
            returnIsNotNull,
            qualifiedName.SchemaName,
            implementsTask);

    /// <summary>
    /// Parses the USING clause's entry list. Accepts either a single bare
    /// string literal (legacy single-session form) or a comma-separated
    /// list of <c>'path' AS alias</c> entries (multi-session form). The
    /// returned list always has at least one entry; legacy single-string
    /// form yields one entry whose <c>Alias</c> is <see langword="null"/>,
    /// distinguishing it from an explicitly aliased single-file entry.
    /// The registrar treats the null-alias case as the implicit
    /// <c>"default"</c> session and is responsible for downstream
    /// resolution rules.
    /// </summary>
    private static TokenListParser<SqlToken, IReadOnlyList<(string Path, string? Alias)>> ParseUsingEntries()
    {
        // One entry: 'path' AS alias  OR  'path' (alias inferred null).
        //
        // The `.Try()` on the AS+identifier pair is load-bearing: the
        // legacy body opener `USING 'path' AS BEGIN ... END` shares the
        // `AS` keyword with the alias clause. Without backtracking, the
        // parser commits to "alias coming" after consuming `AS`, then
        // fails on `BEGIN` (not an Identifier) without rolling back —
        // breaking every single-file CREATE MODEL in the codebase. The
        // `.Try()` resets the cursor when the alias-identifier check
        // fails, letting the outer body parser see the original `AS`.
        TokenListParser<SqlToken, (string Path, string? Alias)> entry =
            from pathToken in Token.EqualTo(SqlToken.StringLiteral)
            from alias in
                (from asKw in Token.EqualTo(SqlToken.As)
                 from aliasToken in Token.EqualTo(SqlToken.Identifier)
                 select (string?)GetTokenText(aliasToken))
                .Try()
                .OptionalOrDefault()
            select (UnquoteString(pathToken), alias);

        return
            from first in entry
            from rest in (
                from comma in Token.EqualTo(SqlToken.Comma)
                from next in entry
                select next
            ).Many()
            select (IReadOnlyList<(string, string?)>)(new[] { first }.Concat(rest).ToList());
    }

    /// <summary>
    /// Constructs a <see cref="CreateModelStatement"/>, applying the same
    /// procedural-body validation as procedural UDFs (must end with
    /// <c>RETURN</c>, no top-level result-emitting statements). Throws
    /// <see cref="FormatException"/> on validation failure so the bad
    /// statement surfaces as a clean parse error rather than a runtime
    /// crash.
    /// <para>
    /// <c>usingEntries</c> is the output of <see cref="ParseUsingEntries"/>
    /// — always non-empty. A single entry with a null alias is the legacy
    /// <c>USING 'path'</c> form: surfaces as <c>UsingPath = path</c> +
    /// <c>UsingFiles = null</c>. Anything else (multiple entries, or one
    /// entry with an explicit alias) surfaces as
    /// <c>UsingPath = entries[0].Path</c> +
    /// <c>UsingFiles = explicit list with aliases</c> for the registrar.
    /// </para>
    /// </summary>
    private static CreateModelStatement BuildCreateModelStatement(
        string name,
        IReadOnlyList<UdfParameter> parameters,
        string returnTypeName,
        IReadOnlyList<(string Path, string? Alias)> usingEntries,
        IReadOnlyList<Statement> body,
        bool ifNotExists,
        bool orReplace,
        bool returnIsNotNull,
        string? schemaName,
        string? implementsTaskName)
    {
        // No USING clause at all: a delegating model whose body produces
        // its result by calling into another model or UDF, with no
        // weights of its own. UsingPath / UsingFiles both stay null so
        // the registrar binds zero sessions.
        if (usingEntries.Count == 0)
        {
            ValidateProceduralBody(name, body);
            return new CreateModelStatement(
                Name: name,
                Parameters: parameters,
                ReturnTypeName: returnTypeName,
                UsingPath: null,
                StatementBody: body,
                IfNotExists: ifNotExists,
                OrReplace: orReplace,
                Span: null,
                ReturnIsNotNull: returnIsNotNull,
                SchemaName: schemaName,
                ImplementsTaskName: implementsTaskName,
                UsingFiles: null);
        }

        foreach ((string path, _) in usingEntries)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new FormatException(
                    $"CREATE MODEL {name}: USING clause file path must be non-empty.");
            }
        }

        // Distinguish legacy from multi-session at AST construction time.
        // Legacy: exactly one entry, null alias → no UsingFiles attached;
        // the existing single-session code paths (registrar, system.models
        // serializer, etc.) flow unchanged.
        bool isLegacy = usingEntries.Count == 1 && usingEntries[0].Alias is null;
        IReadOnlyList<UsingFileSpec>? usingFiles = null;
        if (!isLegacy)
        {
            // Duplicate-alias detection. The implicit "default" alias is
            // only used for legacy single-session bundles; any aliased
            // declaration requires every entry to carry an explicit alias.
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            List<UsingFileSpec> files = new(usingEntries.Count);
            foreach ((string path, string? alias) in usingEntries)
            {
                if (alias is null)
                {
                    throw new FormatException(
                        $"CREATE MODEL {name}: every USING entry must have an explicit alias " +
                        $"(`'path' AS alias`) when more than one file is declared, or when any " +
                        $"entry uses AS.");
                }
                if (!seen.Add(alias))
                {
                    throw new FormatException(
                        $"CREATE MODEL {name}: USING alias '{alias}' is declared more than once.");
                }
                files.Add(new UsingFileSpec(path, alias));
            }
            usingFiles = files;
        }

        ValidateProceduralBody(name, body);

        return new CreateModelStatement(
            Name: name,
            Parameters: parameters,
            ReturnTypeName: returnTypeName,
            UsingPath: usingEntries[0].Path,
            StatementBody: body,
            IfNotExists: ifNotExists,
            OrReplace: orReplace,
            Span: null,
            ReturnIsNotNull: returnIsNotNull,
            SchemaName: schemaName,
            ImplementsTaskName: implementsTaskName,
            UsingFiles: usingFiles);
    }

    /// <summary>
    /// Parses <c>DROP MODEL [IF EXISTS] name</c>. Deferred for v1 — the
    /// parser is wired but DROP-MODEL execution lands later. Including
    /// the grammar now means existing scripts can use it without a
    /// parser change later.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> DropModelParser =
        from dropKw in Token.EqualTo(SqlToken.Drop)
        // MODEL is a contextual identifier here for the same reason as
        // CreateModelPrefix — see the comment there.
        from modelKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("MODEL", StringComparison.OrdinalIgnoreCase), "MODEL")
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new DropModelStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>EVICT MODEL [IF EXISTS] name</c>. EVICT and MODEL are
    /// both contextual identifiers — they're only meaningful in this
    /// position, so we don't promote either to a reserved keyword and
    /// break user scripts that bind <c>evict</c> as a column name.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> EvictModelParser =
        from evictKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("EVICT", StringComparison.OrdinalIgnoreCase), "EVICT")
        from modelKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("MODEL", StringComparison.OrdinalIgnoreCase), "MODEL")
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new EvictModelStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>RESET CALIBRATION [IF EXISTS] name</c>. RESET and
    /// CALIBRATION are contextual identifiers — see <see cref="EvictModelParser"/>
    /// for the rationale on not reserving them.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> ResetCalibrationParser =
        from resetKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("RESET", StringComparison.OrdinalIgnoreCase), "RESET")
        from calibrationKw in Token.EqualTo(SqlToken.Identifier)
            .Where(t => GetTokenText(t).Equals("CALIBRATION", StringComparison.OrdinalIgnoreCase), "CALIBRATION")
        from ifExists in IfExistsParser
        from qualifiedName in QualifiedTableNameParser
        select (Statement)new ResetCalibrationStatement(
            qualifiedName.TableName,
            ifExists,
            Span: null,
            SchemaName: qualifiedName.SchemaName);

    /// <summary>
    /// Parses <c>CALL namespace.functionname(arg1, arg2, ...)</c>.
    /// The function call expression after CALL is parsed by the same
    /// <see cref="FunctionCall"/> combinator used for inline expressions,
    /// so namespaced calls (<c>udf.shout('hello')</c>) and all argument
    /// forms are supported. OVER and WITHIN GROUP are accepted by the
    /// combinator but carry no meaning in a statement context.
    /// </summary>
    private static readonly TokenListParser<SqlToken, Statement> CallStatementParser =
        from callKw in Token.EqualTo(SqlToken.Call)
        from call in FunctionCall
        select (Statement)new CallStatement(call, ToSpan(callKw));

}
