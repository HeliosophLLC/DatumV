using BenchmarkDotNet.Attributes;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Tokens;
using Superpower;

namespace DatumIngest.Benchmarks;

/// <summary>
/// Benchmarks for SQL tokenization and parsing at various complexity levels.
/// </summary>
[MemoryDiagnoser]
public class ParsingBenchmarks
{
    private const string SimpleQuery = "SELECT id, name FROM data";
    private const string WhereQuery = "SELECT id, name, value FROM data WHERE value > 10 AND category = 'alpha'";
    private const string JoinQuery = "SELECT a.id, a.name, b.description FROM data AS a INNER JOIN lookup AS b ON a.id = b.lookup_id WHERE a.value > 50";
    private const string ComplexQuery = "SELECT a.id, a.name, b.description, a.value, a.score FROM data AS a LEFT JOIN lookup AS b ON a.id = b.lookup_id WHERE a.value BETWEEN 10 AND 500 AND a.category IN ('alpha', 'beta', 'gamma') ORDER BY a.score DESC LIMIT 100";
    private const string SubqueryQuery = "SELECT id, name FROM (SELECT id, name, value FROM data WHERE value > 100) AS filtered WHERE name LIKE 'item_%'";
    private const string InSubqueryQuery = "SELECT id, name FROM data WHERE id IN (SELECT lookup_id FROM lookup WHERE weight > 10)";
    private const string ExistsSubqueryQuery = "SELECT id, name FROM data WHERE EXISTS (SELECT 1 FROM lookup WHERE lookup.lookup_id = data.id AND weight > 10)";
    private const string ScalarSubqueryQuery = "SELECT id, name, (SELECT MAX(weight) FROM lookup WHERE lookup.lookup_id = data.id) AS max_weight FROM data";
    private const string DistinctAggregateQuery = "SELECT category, COUNT(DISTINCT name) AS unique_names, SUM(DISTINCT value) AS unique_sum FROM data GROUP BY category";
    private const string SimpleCommonTableExpressionQuery = "WITH filtered AS (SELECT id, name, value FROM data WHERE value > 100) SELECT id, name FROM filtered";
    private const string RecursiveCommonTableExpressionQuery = "WITH RECURSIVE seq AS (SELECT 1 AS n UNION ALL SELECT n + 1 FROM seq WHERE n < 100) SELECT n FROM seq";
    private const string MultipleCommonTableExpressionQuery = "WITH high AS (SELECT id, name, value FROM data WHERE value > 500), ranked AS (SELECT id, name, ROW_NUMBER() OVER (ORDER BY value DESC) AS rank FROM high) SELECT id, name, rank FROM ranked WHERE rank <= 10";

    [Benchmark(Description = "Tokenize simple SELECT")]
    public void TokenizeSimple()
    {
        SqlTokenizer.Instance.Tokenize(SimpleQuery);
    }

    [Benchmark(Description = "Tokenize with WHERE")]
    public void TokenizeWhere()
    {
        SqlTokenizer.Instance.Tokenize(WhereQuery);
    }

    [Benchmark(Description = "Tokenize with JOIN")]
    public void TokenizeJoin()
    {
        SqlTokenizer.Instance.Tokenize(JoinQuery);
    }

    [Benchmark(Description = "Tokenize complex query")]
    public void TokenizeComplex()
    {
        SqlTokenizer.Instance.Tokenize(ComplexQuery);
    }

    [Benchmark(Description = "Parse simple SELECT")]
    public void ParseSimple()
    {
        SqlParser.Parse(SimpleQuery);
    }

    [Benchmark(Description = "Parse with WHERE")]
    public void ParseWhere()
    {
        SqlParser.Parse(WhereQuery);
    }

    [Benchmark(Description = "Parse with JOIN")]
    public void ParseJoin()
    {
        SqlParser.Parse(JoinQuery);
    }

    [Benchmark(Description = "Parse complex query")]
    public void ParseComplex()
    {
        SqlParser.Parse(ComplexQuery);
    }

    [Benchmark(Description = "Parse subquery")]
    public void ParseSubquery()
    {
        SqlParser.Parse(SubqueryQuery);
    }

    [Benchmark(Description = "Parse IN subquery")]
    public void ParseInSubquery()
    {
        SqlParser.Parse(InSubqueryQuery);
    }

    [Benchmark(Description = "Parse EXISTS subquery")]
    public void ParseExistsSubquery()
    {
        SqlParser.Parse(ExistsSubqueryQuery);
    }

    [Benchmark(Description = "Parse scalar subquery")]
    public void ParseScalarSubquery()
    {
        SqlParser.Parse(ScalarSubqueryQuery);
    }

    [Benchmark(Description = "Parse DISTINCT aggregate")]
    public void ParseDistinctAggregate()
    {
        SqlParser.Parse(DistinctAggregateQuery);
    }

    [Benchmark(Description = "Parse simple CTE")]
    public void ParseSimpleCommonTableExpression()
    {
        SqlParser.Parse(SimpleCommonTableExpressionQuery);
    }

    [Benchmark(Description = "Parse recursive CTE")]
    public void ParseRecursiveCommonTableExpression()
    {
        SqlParser.Parse(RecursiveCommonTableExpressionQuery);
    }

    [Benchmark(Description = "Parse multi-CTE")]
    public void ParseMultipleCommonTableExpressions()
    {
        SqlParser.Parse(MultipleCommonTableExpressionQuery);
    }
}
