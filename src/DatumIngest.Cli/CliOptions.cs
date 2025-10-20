namespace DatumIngest.Cli;

/// <summary>
/// Parsed CLI arguments.
/// </summary>
internal sealed class CliOptions
{
    /// <summary>Gets or sets the command to execute: query, explore, or stats.</summary>
    public string Command { get; set; } = "explore";

    /// <summary>Gets or sets the SQL query string.</summary>
    public string Sql { get; set; } = "";

    /// <summary>
    /// Gets or sets the path to a file containing the SQL query.
    /// Use <c>"-"</c> to read from standard input.
    /// When set, takes precedence over the inline <see cref="Sql"/> positional argument.
    /// </summary>
    public string? SqlFile { get; set; }

    /// <summary>Gets or sets the catalog file path.</summary>
    public string? CatalogPath { get; set; }

    /// <summary>Gets or sets the inline source definitions.</summary>
    public List<string> Sources { get; set; } = new();

    /// <summary>Gets or sets the row limit for explore mode.</summary>
    public int Limit { get; set; } = 10;

    /// <summary>Gets or sets whether to run EXPLAIN ANALYZE (with execution metrics).</summary>
    public bool Analyze { get; set; }

    /// <summary>Gets or sets the output file path for the manifest command.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets whether to enable checkpoint-based resume for sharded writes.</summary>
    public bool Checkpoint { get; set; }

    /// <summary>Gets or sets the paths to pre-built index files to load for query execution.</summary>
    public List<string> IndexPaths { get; set; } = new();

    /// <summary>Gets or sets the output directory for the ingest command. When <c>null</c>,
    /// output files are written alongside the source file.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>
    /// Gets or sets the memory budget in bytes for operators that support spill-to-disk.
    /// Defaults to half of the available physical memory, so that hash join build tables
    /// and GROUP BY aggregation state can coexist in RAM on typical workstations without
    /// triggering unnecessary spill-to-disk. Pass <c>--memory-budget 0</c> to remove the
    /// budget entirely (fully in-memory). Accepts raw byte counts or human-readable
    /// suffixes (e.g. 512MB, 2GB, 16GB).
    /// </summary>
    public long? MemoryBudgetBytes { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2;

    /// <summary>Gets or sets the named parameter bindings (name → raw string value).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the number of times to repeat the query. Used for warm-pool benchmarking.</summary>
    public int Repeat { get; set; } = 1;

    /// <summary>
    /// Parses command-line arguments into a CliOptions instance.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        CliOptions options = new();

        if (args.Length < 1)
        {
            throw new ArgumentException("Usage: datum-ingest <command> [<sql>] [--catalog <path>] [--source <source>...] [--limit <n>] [--analyze] [--output <path>] [--checkpoint] [--index <path>...]");
        }

        options.Command = args[0].ToLowerInvariant();

        // Commands that do not require a SQL argument.
        int argStart;

        if (options.Command is "ingest" or "manifest-schema" or "shell" or "star-schema")
        {
            argStart = 1;
        }
        else if (args.Length >= 2 && !args[1].StartsWith("--", StringComparison.Ordinal))
        {
            // Positional SQL argument.
            options.Sql = args[1];
            argStart = 2;
        }
        else
        {
            // No positional SQL — expect --sql-file later in the argument list.
            argStart = 1;
        }

        // Special handling: "explain" can have "--analyze" before or after sql
        // but otherwise needs the same source args.

        for (int i = argStart; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--catalog":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--catalog requires a path argument");
                    }
                    options.CatalogPath = args[++i];
                    break;

                case "--source":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--source requires a definition argument");
                    }
                    options.Sources.Add(args[++i]);
                    break;

                case "--limit":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int limit))
                    {
                        throw new ArgumentException("--limit requires a numeric argument");
                    }
                    options.Limit = limit;
                    i++;
                    break;

                case "--analyze":
                    options.Analyze = true;
                    break;

                case "--checkpoint":
                    options.Checkpoint = true;
                    break;

                case "--index":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--index requires a path argument");
                    }
                    options.IndexPaths.Add(args[++i]);
                    break;

                case "--memory-budget":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--memory-budget requires a size argument (e.g. 512MB, 2GB, 0 to disable)");
                    }
                    {
                        long parsed = ParseByteSize(args[++i]);
                        // Zero or negative means: remove the budget (fully in-memory joins).
                        options.MemoryBudgetBytes = parsed > 0 ? parsed : null;
                    }
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output requires a path argument");
                    }
                    options.OutputPath = args[++i];
                    break;

                case "--output-dir":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output-dir requires a directory path argument");
                    }
                    options.OutputDirectory = args[++i];
                    break;

                case "--sql-file":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--sql-file requires a path argument (or '-' for stdin)");
                    }
                    options.SqlFile = args[++i];
                    break;

                case "--param":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--param requires a key=value argument");
                    }
                    string paramArg = args[++i];
                    int equalsIndex = paramArg.IndexOf('=');
                    if (equalsIndex <= 0)
                    {
                        throw new ArgumentException($"--param value must be in key=value format, got: {paramArg}");
                    }
                    string paramName = paramArg[..equalsIndex];
                    string paramValue = paramArg[(equalsIndex + 1)..];
                    options.Parameters[paramName] = paramValue;
                    break;

                case "--repeat":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--repeat requires a count argument");
                    }
                    options.Repeat = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        // Require SQL from either the positional argument or --sql-file for SQL commands.
        bool isSqlCommand = options.Command is not ("ingest" or "manifest-schema" or "shell" or "star-schema");
        if (isSqlCommand && string.IsNullOrEmpty(options.Sql) && options.SqlFile is null)
        {
            throw new ArgumentException("Usage: datum-ingest <command> <sql> [...options] (or use --sql-file <path>)");
        }

        if (options.CatalogPath is null && options.Sources.Count == 0)
        {
            throw new ArgumentException("At least one of --catalog, --source, or --manifest is required.");
        }

        return options;
    }

    /// <summary>
    /// Parses a human-readable byte size string (e.g. "512MB", "2GB", "1048576")
    /// into a <see langword="long"/> byte count.
    /// </summary>
    private static long ParseByteSize(string input)
    {
        string trimmed = input.Trim();

        if (long.TryParse(trimmed, out long rawBytes))
        {
            return rawBytes;
        }

        if (trimmed.Length < 3)
        {
            throw new ArgumentException($"Invalid byte size: '{input}'. Use a number or a suffixed value like 512MB, 2GB.");
        }

        string suffix = trimmed[^2..].ToUpperInvariant();
        string numberPart = trimmed[..^2];

        if (!double.TryParse(numberPart, out double number))
        {
            throw new ArgumentException($"Invalid byte size: '{input}'. Use a number or a suffixed value like 512MB, 2GB.");
        }

        long multiplier = suffix switch
        {
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => throw new ArgumentException($"Unknown size suffix '{suffix}'. Use KB, MB, GB, or TB."),
        };

        return (long)(number * multiplier);
    }
}
