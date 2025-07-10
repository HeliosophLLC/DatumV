namespace DatumQuery.Cli;

/// <summary>
/// Parsed CLI arguments.
/// </summary>
internal sealed class CliOptions
{
    /// <summary>Gets or sets the command to execute: query, explore, or stats.</summary>
    public string Command { get; set; } = "explore";

    /// <summary>Gets or sets the SQL query string.</summary>
    public string Sql { get; set; } = "";

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

    /// <summary>
    /// Parses command-line arguments into a CliOptions instance.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        CliOptions options = new();

        if (args.Length < 2)
        {
            throw new ArgumentException("Usage: dq <command> <sql> [--catalog <path>] [--source <source>...] [--limit <n>] [--analyze] [--output <path>]");
        }

        options.Command = args[0].ToLowerInvariant();
        options.Sql = args[1];

        // Special handling: "explain" can have "--analyze" before or after sql
        // but otherwise needs the same source args.

        for (int i = 2; i < args.Length; i++)
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

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output requires a path argument");
                    }
                    options.OutputPath = args[++i];
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (options.CatalogPath is null && options.Sources.Count == 0)
        {
            throw new ArgumentException("At least one of --catalog or --source is required.");
        }

        return options;
    }
}
