using System.Diagnostics;
using System.Text;
using DatumIngest.Catalog;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;
using DatumIngest.Serialization;

// execute-once <datum-file> "<sql>"
//
//   Parses, plans, and executes a SQL query against a .datum file,
//   printing the result rows as tab-separated values. Equivalent to
//   `explain-once` but actually runs the query.
//
// Optional flags:
//   --table <name>       Override the table name (default: derived from file path)
//   --sql-file <path>    Read SQL from a file
//   --limit <N>          Cap printed rows (default: 100; use --all to disable)
//   --all                Print every row returned by the query (no cap)

Console.OutputEncoding = Encoding.UTF8;

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
    Console.Error.WriteLine("  execute-once <datum-file> \"<sql>\" [--table <name>] [--limit <N>] [--all]");
    Console.Error.WriteLine("  execute-once <datum-file> --sql-file <path> [--table <name>] [--limit <N>] [--all]");
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

using CancellationTokenSource cts = new();
Console.CancelKeyPress += (_, e) => { cts.Cancel(); e.Cancel = true; };

using LocalBufferPool localBufferPool = new(backing);
DatumIngest.Execution.ExecutionContext executionContext = new(
    cts.Token, functions, catalog, localBufferPool);

Console.WriteLine($"Table:  {tableName}");
Console.WriteLine($"Source: {opts.DatumPath}");
Console.WriteLine($"SQL:    {sql.Trim().ReplaceLineEndings(" ")}");
Console.WriteLine();

Stopwatch sw = Stopwatch.StartNew();
long totalRows = 0;
long printedRows = 0;
bool headerPrinted = false;
bool truncated = false;

try
{
    await foreach (RowBatch batch in plan.ExecuteAsync(executionContext).WithCancellation(cts.Token))
    {
        try
        {
            for (int i = 0; i < batch.Count; i++)
            {
                Row row = batch[i];
                totalRows++;

                if (!headerPrinted)
                {
                    Console.WriteLine(string.Join('\t', row.ColumnNames));
                    Console.WriteLine(new string('─', 80));
                    headerPrinted = true;
                }

                if (opts.PrintAll || printedRows < opts.Limit)
                {
                    Console.WriteLine(FormatRow(row, batch.Arena));
                    printedRows++;
                }
                else
                {
                    truncated = true;
                }
            }
        }
        finally
        {
            pool.ReturnRowBatch(batch);
        }
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"(cancelled after {sw.Elapsed.TotalSeconds:F2}s)");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine($"Execution error: {ex.Message}");
    return 1;
}

sw.Stop();

Console.WriteLine();
if (!headerPrinted)
{
    Console.WriteLine("(0 rows)");
}
else if (truncated)
{
    Console.WriteLine($"({printedRows:N0} of {totalRows:N0} rows — add --all to show all)");
}
else
{
    Console.WriteLine($"({totalRows:N0} rows)");
}
Console.WriteLine($"Elapsed: {sw.Elapsed.TotalSeconds:F3}s");

return 0;

static string FormatRow(Row row, Arena arena)
{
    StringBuilder builder = new();
    for (int i = 0; i < row.FieldCount; i++)
    {
        if (i > 0) builder.Append('\t');
        builder.Append(FormatValue(row[i], arena));
    }
    return builder.ToString();
}

static string FormatValue(DataValue value, Arena arena)
{
    if (value.IsNull) return "NULL";

    return value.Kind switch
    {
        DataKind.Boolean => value.AsBoolean() ? "true" : "false",
        DataKind.UInt8 => value.AsUInt8().ToString(),
        DataKind.Int8 => value.AsInt8().ToString(),
        DataKind.UInt16 => value.AsUInt16().ToString(),
        DataKind.Int16 => value.AsInt16().ToString(),
        DataKind.UInt32 => value.AsUInt32().ToString(),
        DataKind.Int32 => value.AsInt32().ToString(),
        DataKind.UInt64 => value.AsUInt64().ToString(),
        DataKind.Int64 => value.AsInt64().ToString(),
        DataKind.Float32 => value.AsFloat32().ToString("G"),
        DataKind.Float64 => value.AsFloat64().ToString("G"),
        DataKind.Date => value.AsDate().ToString("yyyy-MM-dd"),
        DataKind.DateTime => value.AsDateTime().ToString("yyyy-MM-dd HH:mm:ss"),
        DataKind.Time => value.AsTime().ToString("HH:mm:ss"),
        DataKind.Duration => value.AsDuration().ToString(),
        DataKind.Uuid => value.AsUuid().ToString(),
        DataKind.String => value.IsInline ? value.AsString() : value.AsString(arena),
        DataKind.JsonValue => value.IsInline ? value.AsString() : value.AsString(arena),
        _ => $"<{value.Kind}>",
    };
}

sealed record Options(
    string DatumPath,
    string? Sql,
    string? SqlFile,
    string? TableName,
    int Limit,
    bool PrintAll)
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
        int limit = 100;
        bool printAll = false;

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

                case "--limit":
                    if (++i >= args.Length) throw new ArgumentException("--limit requires a count.");
                    if (!int.TryParse(args[i], out limit) || limit < 0)
                    {
                        throw new ArgumentException($"--limit must be a non-negative integer, got '{args[i]}'.");
                    }
                    break;

                case "--all":
                    printAll = true;
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

        return new Options(datumPath, sql, sqlFile, tableName, limit, printAll);
    }
}
