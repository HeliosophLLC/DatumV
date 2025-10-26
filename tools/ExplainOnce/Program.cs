using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

// ExplainPlanNode.Render uses └─/├─/│ box-drawing characters.
Console.OutputEncoding = Encoding.UTF8;

// explain-once <datum-file> "<sql>"
//
//   Plans a SQL query against a .datum file and prints the EXPLAIN tree
//   without executing it. Useful for inspecting planner decisions
//   (index usage, pushdown, join strategies, cardinality estimates)
//   without needing a running server.
//
// Optional flags:
//   --table <name>       Override the table name (default: derived from the file path)
//   --sql-file <path>    Read the SQL from a file instead of the command line

Options opts;
try
{
    opts = Options.Parse(args);
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine("Usage:");
    Console.Error.WriteLine("  explain-once <datum-file> \"<sql>\" [--table <name>]");
    Console.Error.WriteLine("  explain-once <datum-file> --sql-file <path> [--table <name>]");
    return 1;
}

if (!File.Exists(opts.DatumPath))
{
    Console.Error.WriteLine($"Datum file not found: {opts.DatumPath}");
    return 1;
}

string sql = opts.SqlFile is not null
    ? await File.ReadAllTextAsync(opts.SqlFile)
    : opts.Sql!;

string tableName = opts.TableName ?? PathDetector.DeriveTableName(opts.DatumPath);

PoolBacking backing = new();
Pool pool = new(backing);
using TableCatalog catalog = new(pool);
catalog.Add(new TableDescriptor(Name: tableName, FilePath: opts.DatumPath));

FunctionRegistry functions = FunctionRegistry.CreateDefault();

QueryExpression query;
try
{
    query = SqlParser.Parse(sql);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Parse error: {ex.Message}");
    return 1;
}

QueryPlanner planner = new(catalog, functions);

IQueryOperator plan;
try
{
    plan = planner.Plan(query);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Planning error: {ex.Message}");
    return 1;
}

ExplainPlanNode tree = QueryExplainer.Explain(plan);

Console.WriteLine($"Table:  {tableName}");
Console.WriteLine($"Source: {opts.DatumPath}");
Console.WriteLine($"SQL:    {sql.Trim().ReplaceLineEndings(" ")}");
Console.WriteLine();
Console.Write(tree.Render());

return 0;

sealed record Options(
    string DatumPath,
    string? Sql,
    string? SqlFile,
    string? TableName)
{
    public static Options Parse(string[] args)
    {
        if (args.Length < 1)
        {
            throw new ArgumentException("Missing <datum-file>.");
        }

        string datumPath = args[0];
        string? sql = null;
        string? sqlFile = null;
        string? tableName = null;

        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "--table":
                    if (++i >= args.Length) throw new ArgumentException("--table requires a name.");
                    tableName = args[i];
                    break;

                case "--sql-file":
                    if (++i >= args.Length) throw new ArgumentException("--sql-file requires a path.");
                    sqlFile = args[i];
                    break;

                default:
                    if (sql is not null)
                    {
                        throw new ArgumentException($"Unexpected argument: {arg}");
                    }
                    sql = arg;
                    break;
            }
        }

        if (sql is null && sqlFile is null)
        {
            throw new ArgumentException("Missing SQL — pass it as a positional argument or via --sql-file.");
        }

        if (sql is not null && sqlFile is not null)
        {
            throw new ArgumentException("Specify either positional SQL or --sql-file, not both.");
        }

        return new Options(datumPath, sql, sqlFile, tableName);
    }
}
