namespace DatumIngest.LanguageServer;

using DatumIngest.Parsing.Tokens;

/// <summary>
/// Single source of truth for SQL keyword completions. Owns two mappings:
/// <list type="bullet">
///   <item><b>Zone → keywords</b>: which keyword strings each <see cref="CompletionZoneKind"/> offers.
///         <see cref="CompletionProvider"/> reads this to populate keyword items.</item>
///   <item><b>SqlToken → completion strings</b>: which completion string(s) represent each keyword token.
///         Coverage tests verify every <see cref="SqlToken"/> keyword is mapped and appears in at least one zone.</item>
/// </list>
/// </summary>
internal static class KeywordRegistry
{
    // ───────────────────── Expression keyword building blocks ─────────────────────

    private static readonly string[] ExpressionKeywords =
    [
        "AND", "OR", "NOT", "IN", "BETWEEN", "LIKE", "ILIKE", "REGEXP", "ESCAPE",
        "IS", "NULL", "TRUE", "FALSE", "CAST", "CASE", "EXISTS", "DISTINCT", "EXTRACT",
        "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP",
        "AT TIME ZONE",
        "WHEN", "THEN", "ELSE", "END",
    ];

    // ───────────────────── Clause continuation building blocks ─────────────────────

    /// <summary>Join keywords offered after a FROM source or at other clause boundaries.</summary>
    private static readonly string[] JoinKeywords =
    [
        "JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN", "LATERAL",
    ];

    /// <summary>Clause keywords available from the WHERE position onward.</summary>
    private static readonly string[] PostWhereKeywords =
    [
        "CROSS VALIDATE", "GROUP BY", "HAVING", "QUALIFY",
        "ORDER BY", "LIMIT", "OFFSET", "INTO",
        "UNION", "INTERSECT", "EXCEPT",
        "PIVOT", "UNPIVOT", "ASSERT",
    ];

    /// <summary>Clause keywords available from the GROUP BY position onward.</summary>
    private static readonly string[] PostGroupByKeywords =
    [
        "HAVING", "QUALIFY", "ASSERT",
        "ORDER BY", "LIMIT", "OFFSET", "INTO",
        "UNION", "INTERSECT", "EXCEPT",
        "PIVOT", "UNPIVOT",
    ];

    /// <summary>Clause keywords available from the HAVING position onward.</summary>
    private static readonly string[] PostHavingKeywords =
    [
        "QUALIFY", "ASSERT",
        "ORDER BY", "LIMIT", "OFFSET", "INTO",
        "UNION", "INTERSECT", "EXCEPT",
        "PIVOT", "UNPIVOT",
    ];

    /// <summary>Clause keywords available from the QUALIFY position onward.</summary>
    private static readonly string[] PostQualifyKeywords =
    [
        "ASSERT",
        "ORDER BY", "LIMIT", "OFFSET", "INTO",
        "UNION", "INTERSECT", "EXCEPT",
        "PIVOT", "UNPIVOT",
    ];

    /// <summary>Clause keywords available from the ORDER BY position onward.</summary>
    private static readonly string[] PostOrderByKeywords =
    [
        "LIMIT", "OFFSET", "INTO",
        "UNION", "INTERSECT", "EXCEPT",
    ];

    // ───────────────────── Shared keyword lists ─────────────────────

    /// <summary>
    /// Date part field names offered inside <c>EXTRACT(</c> completions.
    /// PostgreSQL-compatible fields plus DatumIngest extensions.
    /// </summary>
    internal static readonly string[] DatePartFieldNames =
    [
        "YEAR", "MONTH", "DAY", "HOUR", "MINUTE", "SECOND",
        "QUARTER", "WEEK", "DOW", "DOY",
        "ISODOW", "ISOYEAR",
        "EPOCH", "CENTURY", "DECADE", "MILLENNIUM", "JULIAN",
        "MILLISECOND", "MICROSECOND",
        "TIMEZONE", "TIMEZONE_HOUR", "TIMEZONE_MINUTE",
    ];

    /// <summary>
    /// Column / variable / parameter type keywords offered in CREATE TABLE,
    /// ALTER TABLE ADD COLUMN, DECLARE, CREATE FUNCTION parameter, and
    /// CAST AS contexts. Mirrors the runtime <c>DataKind</c> enum plus the
    /// <c>Array</c> wrapper.
    /// </summary>
    internal static readonly string[] ColumnTypeKeywords =
    [
        "Boolean",
        "UInt8", "UInt16", "UInt32", "UInt64", "UInt128",
        "Int8", "Int16", "Int32", "Int64", "Int128",
        "Float16", "Float32", "Float64", "Decimal",
        "Date", "Time", "DateTime", "Duration",
        "String", "Uuid",
        "Image", "Audio", "Video", "Json",
        "Struct",
        "Array",
    ];

    // ───────────────────── Zone → keyword completions ─────────────────────

    private static readonly Dictionary<CompletionZoneKind, string[]> ZoneKeywords = new()
    {
        [CompletionZoneKind.StatementStart] =
        [
            // Query / DML / DDL statements
            "SELECT", "WITH", "CREATE", "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "ANALYZE", "REINDEX", "CALL",
            // Procedural-batch statements. Reachable both at the top of a
            // batch and after IF/WHILE/FOR/TRY/CATCH/ELSE/BEGIN — wherever
            // the parser expects a fresh statement.
            "BEGIN", "DECLARE", "SET", "IF", "WHILE", "FOR",
            "TRY", "RAISE", "ASSERT", "PRINT", "BREAK", "CONTINUE",
        ],

        [CompletionZoneKind.AfterSelect] =
        [
            "FROM", "AS", "CAST", "CASE", "LET", "SCAN", "ASSERT", "DEFINE", "DISTINCT",
            "WITHIN GROUP", "EXTRACT",
            "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "LOCALTIME", "LOCALTIMESTAMP",
        ],

        [CompletionZoneKind.AfterFrom] =
            ["UNION", "INTERSECT", "EXCEPT"],

        [CompletionZoneKind.AfterFromSource] =
        [
            .. JoinKeywords,
            "WHERE", "CROSS VALIDATE", "GROUP BY", "HAVING", "QUALIFY",
            "ORDER BY", "LIMIT", "OFFSET", "INTO",
            "TABLESAMPLE", "AS",
            "UNION", "INTERSECT", "EXCEPT",
            "PIVOT", "UNPIVOT", "ASSERT",
        ],

        [CompletionZoneKind.AfterJoin] =
            ["UNION", "INTERSECT", "EXCEPT"],

        [CompletionZoneKind.AfterJoinSource] =
        [
            "ON",
            .. JoinKeywords,
            "WHERE", "CROSS VALIDATE", "GROUP BY", "HAVING", "QUALIFY",
            "ORDER BY", "LIMIT", "OFFSET", "INTO",
            "UNION", "INTERSECT", "EXCEPT",
            "PIVOT", "UNPIVOT", "ASSERT",
        ],

        [CompletionZoneKind.AfterWhere] =
            [.. ExpressionKeywords, .. PostWhereKeywords],

        [CompletionZoneKind.AfterOn] =
        [
            .. ExpressionKeywords,
            .. JoinKeywords,
            .. PostWhereKeywords,
        ],

        [CompletionZoneKind.Expression] = ExpressionKeywords,

        // Procedural expression (IF / WHILE predicates, FOR bounds, DECLARE
        // initializers, PRINT / RAISE arguments). Same operator/literal
        // keyword set as a query expression — the difference is that
        // CompletionProvider doesn't add column names here.
        [CompletionZoneKind.ProceduralExpression] = ExpressionKeywords,

        [CompletionZoneKind.AfterGroupBy] =
            ["ALL", .. PostGroupByKeywords],

        [CompletionZoneKind.AfterHaving] =
            [.. ExpressionKeywords, .. PostHavingKeywords],

        [CompletionZoneKind.AfterQualify] =
            [.. ExpressionKeywords, .. PostQualifyKeywords],

        [CompletionZoneKind.AfterAssert] =
            [.. ExpressionKeywords, "MESSAGE", "ON FAIL"],

        [CompletionZoneKind.InsideDefineBlock] =
            ["LET", "ASSERT", "}"],

        [CompletionZoneKind.AfterOrderBy] =
            ["ASC", "DESC", .. PostOrderByKeywords],

        [CompletionZoneKind.InsideOver] =
        [
            "PARTITION BY", "ORDER BY",
            "ROWS BETWEEN", "RANGE BETWEEN",
            "UNBOUNDED PRECEDING", "CURRENT ROW", "UNBOUNDED FOLLOWING",
        ],

        [CompletionZoneKind.InsideExtract] = DatePartFieldNames,

        [CompletionZoneKind.AfterSetOperation] =
            ["ALL", "SELECT"],

        [CompletionZoneKind.AfterCreate] =
            ["TEMP", "TEMPORARY", "TABLE", "INDEX", "UNIQUE INDEX", "FUNCTION", "PROCEDURE", "MODEL", "OR REPLACE"],

        [CompletionZoneKind.AfterDrop] =
            ["TABLE", "INDEX", "FUNCTION", "PROCEDURE", "MODEL", "IF EXISTS"],

        [CompletionZoneKind.AfterCreateTableColumns] =
            [.. ColumnTypeKeywords,
             "PRIMARY KEY",
             "NOT NULL",
             // Bare `NULL` is the explicit counterpart of `NOT NULL`.
             // Constraints may appear in any order — see the column-
             // constraint folding in SqlParser.FoldColumnConstraints.
             "NULL",
             "DEFAULT",
             // PG-canonical IDENTITY forms. Bare `IDENTITY` is still valid
             // syntax (alias for GENERATED ALWAYS AS IDENTITY) but isn't
             // surfaced — nudges users toward the standard form.
             "GENERATED ALWAYS AS IDENTITY",
             "GENERATED BY DEFAULT AS IDENTITY"],

        // After `DECLARE @x ⌷` and inside `CREATE FUNCTION/PROCEDURE foo(@x ⌷`
        // we want only type names (no PRIMARY KEY / DEFAULT — those belong
        // to CREATE TABLE columns, not procedural bindings).
        [CompletionZoneKind.AfterDeclareType] = ColumnTypeKeywords,

        [CompletionZoneKind.AfterInsertTable] =
            ["VALUES", "SELECT"],

        [CompletionZoneKind.AfterUpdateSet] =
            ["WHERE", "FROM"],

        // ALTER TABLE [IF EXISTS] name — IF EXISTS only meaningful here
        // when no table name has been typed yet, but offering it
        // alongside the verbs is harmless and matches PG.
        [CompletionZoneKind.AfterAlterTable] =
            ["ADD", "DROP", "ALTER", "IF EXISTS"],

        // ALTER TABLE name DROP {COLUMN | CONSTRAINT} … — offer both verbs
        // plus IF EXISTS for both shapes.
        [CompletionZoneKind.AfterAlterTableDrop] =
            ["COLUMN", "CONSTRAINT", "IF EXISTS"],

        // ALTER TABLE name ALTER — only COLUMN is supported as the next
        // token in v1 (per-table-attribute alterations aren't on the roadmap).
        [CompletionZoneKind.AfterAlterTableAlter] =
            ["COLUMN"],

        // ALTER TABLE name ALTER COLUMN col DROP — droppable column
        // attributes. NOT NULL is wired via the per-page HasNullBitmap
        // flag (historical pages stay no-bitmap, new pages carry one).
        [CompletionZoneKind.AfterAlterColumnDrop] =
            ["IDENTITY", "DEFAULT", "NOT NULL", "IF EXISTS"],

        // ALTER TABLE ADD COLUMN accepts the same constraint set as CREATE
        // TABLE columns (PRIMARY KEY, NULL/NOT NULL, DEFAULT, GENERATED …)
        // in any order — see SqlParser.AlterAddColumnConstraintParser.
        [CompletionZoneKind.AfterAlterTableAdd] =
            ["COLUMN", .. ColumnTypeKeywords,
             "PRIMARY KEY",
             "NOT NULL",
             "NULL",
             "DEFAULT",
             "GENERATED ALWAYS AS IDENTITY",
             "GENERATED BY DEFAULT AS IDENTITY"],

        // After the column list of CREATE INDEX closes. Both clauses are
        // optional (a bare `CREATE INDEX idx ON t (c)` is a default composite
        // B+Tree); the completions just surface that the optional suffixes
        // exist.
        [CompletionZoneKind.AfterCreateIndexColumns] =
            ["USING", "WITH"],

        // After `CREATE INDEX ... USING `, offer the available index methods.
        // BTREE is the implicit default and isn't surfaced — typing it is
        // equivalent to omitting USING entirely.
        [CompletionZoneKind.AfterCreateIndexUsing] =
            ["FTS"],

        // Inside `CREATE INDEX ... WITH (`, offer the option keys. The set is
        // method-specific (analyzer is only meaningful for FTS), but the
        // detector doesn't currently distinguish — we just offer the union of
        // known keys.
        [CompletionZoneKind.InsideCreateIndexWithOptions] =
            ["analyzer"],

        [CompletionZoneKind.AfterInto] =
            ["SHARD"],

        [CompletionZoneKind.AfterTablesampleMethodArg] =
        [
            "ON", "REPEATABLE",
            .. JoinKeywords,
            "WHERE", .. PostWhereKeywords,
        ],

        // Zones with no keyword completions: AfterDot, InFunctionArguments,
        // AfterAs, InsideTablesampleArg, AfterInsertInto, AfterUpdate,
        // AfterDeleteFrom — these are handled by schema/table completions only.
    };

    // ───────────────────── SqlToken → completion string(s) ─────────────────────

    /// <summary>
    /// Maps every keyword <see cref="SqlToken"/> to the completion string(s) that represent it.
    /// Coverage tests verify every keyword token has an entry and that non-empty entries
    /// appear in at least one zone. Component-only tokens map to an empty array.
    /// </summary>
    private static readonly Dictionary<SqlToken, string[]> TokenCompletionMap = new()
    {
        [SqlToken.Select] = ["SELECT"],
        [SqlToken.Into] = ["INTO"],
        [SqlToken.From] = ["FROM"],
        [SqlToken.Join] = ["JOIN", "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN"],
        [SqlToken.Left] = ["LEFT JOIN"],
        [SqlToken.Right] = ["RIGHT JOIN"],
        [SqlToken.Full] = ["FULL JOIN"],
        [SqlToken.Outer] = [],         // Component: part of LEFT/RIGHT/FULL OUTER JOIN
        [SqlToken.Cross] = ["CROSS JOIN", "CROSS VALIDATE"],
        [SqlToken.Inner] = ["INNER JOIN"],
        [SqlToken.Lateral] = ["LATERAL"],
        [SqlToken.Apply] = [],          // Component: part of CROSS APPLY / OUTER APPLY (handled via LATERAL)
        [SqlToken.On] = ["ON"],
        [SqlToken.Where] = ["WHERE"],
        [SqlToken.And] = ["AND"],
        [SqlToken.Or] = ["OR"],
        [SqlToken.Not] = ["NOT", "NOT NULL"],
        [SqlToken.In] = ["IN"],
        [SqlToken.Between] = ["BETWEEN"],
        [SqlToken.Like] = ["LIKE"],
        [SqlToken.ILike] = ["ILIKE"],
        [SqlToken.Regexp] = ["REGEXP"],
        [SqlToken.Escape] = ["ESCAPE"],
        [SqlToken.Is] = ["IS"],
        [SqlToken.Null] = ["NULL", "NOT NULL"],
        [SqlToken.As] = ["AS"],
        [SqlToken.Shard] = ["SHARD"],
        [SqlToken.Group] = ["GROUP BY"],
        [SqlToken.Having] = ["HAVING"],
        [SqlToken.Qualify] = ["QUALIFY"],
        [SqlToken.Order] = ["ORDER BY"],
        [SqlToken.By] = [],             // Component: part of GROUP BY, ORDER BY, PARTITION BY
        [SqlToken.Asc] = ["ASC"],
        [SqlToken.Desc] = ["DESC"],
        [SqlToken.Limit] = ["LIMIT"],
        [SqlToken.Offset] = ["OFFSET"],
        [SqlToken.Cast] = ["CAST"],
        [SqlToken.Extract] = ["EXTRACT"],
        [SqlToken.CurrentDate] = ["CURRENT_DATE"],
        [SqlToken.CurrentTime] = ["CURRENT_TIME"],
        [SqlToken.CurrentTimestamp] = ["CURRENT_TIMESTAMP"],
        [SqlToken.LocalTime] = ["LOCALTIME"],
        [SqlToken.LocalTimestamp] = ["LOCALTIMESTAMP"],
        [SqlToken.True] = ["TRUE"],
        [SqlToken.False] = ["FALSE"],
        [SqlToken.Case] = ["CASE"],
        [SqlToken.When] = ["WHEN"],
        [SqlToken.Then] = ["THEN"],
        [SqlToken.Else] = ["ELSE"],
        [SqlToken.End] = ["END"],
        [SqlToken.Over] = [],           // Component: syntax trigger for window specs, not a standalone completion
        [SqlToken.Partition] = ["PARTITION BY"],
        [SqlToken.Within] = ["WITHIN GROUP"],
        [SqlToken.Rows] = ["ROWS BETWEEN"],
        [SqlToken.Range] = ["RANGE BETWEEN"],
        [SqlToken.Unbounded] = ["UNBOUNDED PRECEDING", "UNBOUNDED FOLLOWING"],
        [SqlToken.Preceding] = [],      // Component: part of UNBOUNDED PRECEDING / N PRECEDING
        [SqlToken.Following] = [],      // Component: part of UNBOUNDED FOLLOWING / N FOLLOWING
        [SqlToken.Current] = ["CURRENT ROW", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP"],
        [SqlToken.Exists] = ["EXISTS"],
        [SqlToken.Distinct] = ["DISTINCT"],
        [SqlToken.Ignore] = [],         // Component: part of IGNORE NULLS (window function modifier)
        [SqlToken.Respect] = [],        // Component: part of RESPECT NULLS (window function modifier)
        [SqlToken.Nulls] = [],          // Component: part of IGNORE NULLS / RESPECT NULLS
        [SqlToken.With] = ["WITH"],
        [SqlToken.Recursive] = [],      // Component: follows WITH in CTE preamble (WITH RECURSIVE)
        [SqlToken.Materialized] = [],   // Component: follows AS in CTE (AS MATERIALIZED / AS NOT MATERIALIZED)
        [SqlToken.Union] = ["UNION"],
        [SqlToken.All] = ["ALL"],
        [SqlToken.Intersect] = ["INTERSECT"],
        [SqlToken.Except] = ["EXCEPT"],
        [SqlToken.Replace] = [],        // Component: part of SELECT * REPLACE (column replacement syntax)
        [SqlToken.Let] = ["LET"],
        [SqlToken.Pivot] = ["PIVOT"],
        [SqlToken.Unpivot] = ["UNPIVOT"],
        [SqlToken.For] = [],            // Component: part of PIVOT/UNPIVOT FOR syntax
        [SqlToken.Include] = [],        // Component: part of UNPIVOT INCLUDE NULLS
        [SqlToken.Tablesample] = ["TABLESAMPLE"],
        [SqlToken.Repeatable] = ["REPEATABLE"],
        [SqlToken.Create] = ["CREATE"],
        [SqlToken.Table] = ["TABLE"],
        [SqlToken.Temp] = ["TEMP"],
        [SqlToken.Temporary] = ["TEMPORARY"],
        [SqlToken.Drop] = ["DROP"],
        [SqlToken.Insert] = ["INSERT"],
        [SqlToken.Values] = ["VALUES"],
        [SqlToken.Update] = ["UPDATE"],
        [SqlToken.Set] = [],            // Component: part of UPDATE SET (not independently completable)
        [SqlToken.Delete] = ["DELETE"],
        [SqlToken.Analyze] = ["ANALYZE"],
        [SqlToken.Reindex] = ["REINDEX"],
        [SqlToken.Index] = ["INDEX"],
        [SqlToken.Unique] = ["UNIQUE INDEX"],
        [SqlToken.Alter] = ["ALTER"],
        [SqlToken.Add] = ["ADD"],
        [SqlToken.Column] = ["COLUMN"],
        [SqlToken.Constraint] = ["CONSTRAINT"],
        [SqlToken.Default] = ["DEFAULT"],
        [SqlToken.Primary] = ["PRIMARY KEY"],
        [SqlToken.Key] = [],            // Component: part of PRIMARY KEY
        [SqlToken.Identity] = [],       // Component: part of GENERATED [ALWAYS|BY DEFAULT] AS IDENTITY (or legacy bare IDENTITY)
        [SqlToken.Generated] = ["GENERATED ALWAYS AS IDENTITY", "GENERATED BY DEFAULT AS IDENTITY"],
        [SqlToken.Always] = [],         // Component: part of GENERATED ALWAYS AS IDENTITY / GENERATED ALWAYS AS (expr)
        [SqlToken.If] = ["IF EXISTS"],
        [SqlToken.Function] = ["FUNCTION"],
        [SqlToken.Procedure] = ["PROCEDURE"],
        [SqlToken.Returns] = [],        // Component: part of CREATE FUNCTION ... RETURNS
        [SqlToken.Returning] = [],      // Component: part of INSERT ... RETURNING (surface as standalone completion is a follow-up)
        [SqlToken.Call] = ["CALL"],
        [SqlToken.Assert] = ["ASSERT"],
        [SqlToken.Message] = ["MESSAGE"],
        [SqlToken.Define] = ["DEFINE"],
        [SqlToken.Scan] = ["SCAN"],
        [SqlToken.Init] = [],           // Component: part of SCAN ... INIT (accumulator initializer)
        [SqlToken.At] = ["AT TIME ZONE"],
        [SqlToken.Time] = [],           // Component: part of AT TIME ZONE
        [SqlToken.Zone] = [],           // Component: part of AT TIME ZONE
        [SqlToken.TypeKeyword] = [],    // Type keywords are handled via ColumnTypeKeywords, not as SqlToken completions

        // Procedural keywords — not currently surfaced as standalone keyword
        // completions because there is no procedural-statement-start zone yet.
        // Mapped to empty arrays so the coverage test sees an entry without
        // demanding a zone slot. Promote to non-empty entries when a
        // ProcedureBody / LoopBody completion zone is added.
        [SqlToken.Begin] = [],
        [SqlToken.While] = [],
        [SqlToken.Declare] = [],
        [SqlToken.To] = [],
        [SqlToken.Break] = [],
        [SqlToken.Continue] = [],
        [SqlToken.Return] = [],         // Procedural-UDF body terminator; only meaningful inside BEGIN…END.
        [SqlToken.Pure] = [],           // CREATE [OR REPLACE] PURE FUNCTION modifier; component of the DDL prefix.
        [SqlToken.Print] = [],
        [SqlToken.Try] = [],
        [SqlToken.Catch] = [],
        [SqlToken.Finally] = [],
        [SqlToken.Raise] = [],
    };

    // ───────────────────── Public API ─────────────────────

    /// <summary>
    /// Returns the keyword completion strings for the given zone, or an empty list
    /// if the zone has no keyword completions.
    /// </summary>
    public static IReadOnlyList<string> GetKeywords(CompletionZoneKind zone)
        => ZoneKeywords.GetValueOrDefault(zone, []);

    /// <summary>
    /// Returns whether the given <see cref="SqlToken"/> has an entry in the token completion map.
    /// Used by coverage tests to verify every keyword token is mapped.
    /// </summary>
    public static bool HasMapping(SqlToken token)
        => TokenCompletionMap.ContainsKey(token);

    /// <summary>
    /// Returns the completion strings for a given <see cref="SqlToken"/>, or an empty list
    /// if the token is not mapped or is component-only.
    /// </summary>
    public static IReadOnlyList<string> GetCompletionStrings(SqlToken token)
        => TokenCompletionMap.GetValueOrDefault(token, []);

    /// <summary>
    /// Returns all distinct completion strings across all zone keyword lists.
    /// Used by coverage tests to verify that every mapped token actually appears somewhere.
    /// </summary>
    public static IReadOnlySet<string> GetAllZoneCompletionStrings()
    {
        HashSet<string> all = new(StringComparer.OrdinalIgnoreCase);
        foreach (string[] keywords in ZoneKeywords.Values)
        {
            foreach (string keyword in keywords)
            {
                all.Add(keyword);
            }
        }

        return all;
    }
}
