using DatumIngest.Indexing;

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

    /// <summary>Gets or sets whether to co-generate a source index alongside INTO output.</summary>
    public bool WithIndex { get; set; }

    /// <summary>Gets or sets the chunk size for index building (rows per chunk).</summary>
    public int ChunkSize { get; set; } = Indexing.IndexConstants.DefaultChunkSize;

    /// <summary>Gets or sets the column names to build bloom filters for during index creation.</summary>
    public HashSet<string> BloomColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the column names to build sorted value indexes for during index creation.</summary>
    public HashSet<string> IndexColumns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets whether to compute pairwise column interactions during manifest generation.</summary>
    public bool WithInteractions { get; set; }

    /// <summary>Gets or sets the manifest file paths for the cross-manifest command.</summary>
    public List<string> ManifestPaths { get; set; } = new();

    /// <summary>
    /// Parses command-line arguments into a CliOptions instance.
    /// </summary>
    public static CliOptions Parse(string[] args)
    {
        CliOptions options = new();

        if (args.Length < 1)
        {
            throw new ArgumentException("Usage: datum-ingest <command> [<sql>] [--catalog <path>] [--source <source>...] [--limit <n>] [--analyze] [--output <path>] [--checkpoint] [--index <path>...] [--with-index] [--with-interactions] [--chunk-size <n>]");
        }

        options.Command = args[0].ToLowerInvariant();

        // The 'index' and 'manifest-schema' commands do not require a SQL argument.
        int argStart;

        if (options.Command is "index" or "index-manifest" or "manifest-schema" or "shell" or "cross-manifest")
        {
            argStart = 1;
        }
        else
        {
            if (args.Length < 2)
            {
                throw new ArgumentException("Usage: datum-ingest <command> <sql> [...options]");
            }

            options.Sql = args[1];
            argStart = 2;
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

                case "--with-index":
                    options.WithIndex = true;
                    break;

                case "--with-interactions":
                    options.WithInteractions = true;
                    break;

                case "--chunk-size":
                    if (i + 1 >= args.Length || !int.TryParse(args[i + 1], out int chunkSize))
                    {
                        throw new ArgumentException("--chunk-size requires a numeric argument");
                    }
                    options.ChunkSize = chunkSize;
                    i++;
                    break;

                case "--bloom-columns":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--bloom-columns requires a comma-separated list of column names");
                    }
                    foreach (string column in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        options.BloomColumns.Add(column);
                    }
                    break;

                case "--index-columns":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--index-columns requires a comma-separated list of column names");
                    }
                    foreach (string column in args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        options.IndexColumns.Add(column);
                    }
                    break;

                case "--output":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--output requires a path argument");
                    }
                    options.OutputPath = args[++i];
                    break;

                case "--manifest":
                    if (i + 1 >= args.Length)
                    {
                        throw new ArgumentException("--manifest requires a path argument");
                    }
                    options.ManifestPaths.Add(args[++i]);
                    break;

                default:
                    throw new ArgumentException($"Unknown argument: {args[i]}");
            }
        }

        if (options.CatalogPath is null && options.Sources.Count == 0 && options.ManifestPaths.Count == 0)
        {
            throw new ArgumentException("At least one of --catalog, --source, or --manifest is required.");
        }

        return options;
    }
}
