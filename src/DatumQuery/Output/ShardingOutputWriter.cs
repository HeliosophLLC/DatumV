namespace Axon.QueryEngine.Output;

using Axon.QueryEngine.Model;

/// <summary>
/// Wraps an output writer factory to automatically create new shards
/// when a sample count or byte size threshold is reached.
/// Shard files are named: {base}_shard_{N:D5}.{ext}
/// </summary>
public sealed class ShardingOutputWriter : IOutputWriter
{
    private readonly Func<string, IOutputWriter> _writerFactory;
    private readonly ShardStrategy _strategy;
    private readonly string _basePath;
    private readonly string _extension;

    private IOutputWriter? _currentWriter;
    private Schema? _schema;
    private int _shardIndex;
    private long _currentShardRows;
    private long _currentShardBytes;
    private long _totalRowsWritten;
    private readonly List<string> _allFiles = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="ShardingOutputWriter"/> class.
    /// </summary>
    /// <param name="writerFactory">Factory function that creates an output writer for a given file path.</param>
    /// <param name="strategy">The sharding strategy to use.</param>
    /// <param name="basePath">The base output file path (without extension for sharding).</param>
    public ShardingOutputWriter(
        Func<string, IOutputWriter> writerFactory,
        ShardStrategy strategy,
        string basePath)
    {
        _writerFactory = writerFactory;
        _strategy = strategy;
        _extension = Path.GetExtension(basePath);
        _basePath = Path.Combine(
            Path.GetDirectoryName(basePath) ?? ".",
            Path.GetFileNameWithoutExtension(basePath));
    }

    /// <inheritdoc />
    public async Task InitializeAsync(Schema schema, CancellationToken cancellationToken = default)
    {
        _schema = schema;
        await StartNewShardAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task WriteRowAsync(Row row, CancellationToken cancellationToken = default)
    {
        if (_currentWriter is null || _schema is null)
        {
            throw new InvalidOperationException("Writer not initialized. Call InitializeAsync first.");
        }

        bool shouldRotate = _strategy.Mode switch
        {
            ShardMode.SampleCount => _currentShardRows >= _strategy.Threshold,
            ShardMode.ByteSize => _currentShardBytes >= _strategy.Threshold,
            _ => false
        };

        if (shouldRotate)
        {
            await FinalizeCurrentShardAsync(cancellationToken);
            await StartNewShardAsync(cancellationToken);
        }

        await _currentWriter.WriteRowAsync(row, cancellationToken);
        _currentShardRows++;
        _totalRowsWritten++;

        // Estimate byte size per row (rough approximation)
        if (_strategy.Mode == ShardMode.ByteSize)
        {
            _currentShardBytes += EstimateRowSize(row);
        }
    }

    /// <inheritdoc />
    public async Task<OutputSummary> FinalizeAsync(CancellationToken cancellationToken = default)
    {
        await FinalizeCurrentShardAsync(cancellationToken);

        long totalBytes = 0;
        foreach (string file in _allFiles)
        {
            if (File.Exists(file))
            {
                totalBytes += new FileInfo(file).Length;
            }
        }

        return new OutputSummary(_totalRowsWritten, totalBytes, _allFiles.AsReadOnly());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_currentWriter is not null)
        {
            await _currentWriter.DisposeAsync();
            _currentWriter = null;
        }
    }

    private async Task StartNewShardAsync(CancellationToken cancellationToken)
    {
        string shardPath = $"{_basePath}_shard_{_shardIndex:D5}{_extension}";
        _currentWriter = _writerFactory(shardPath);
        _allFiles.Add(shardPath);
        await _currentWriter.InitializeAsync(_schema!, cancellationToken);
        _currentShardRows = 0;
        _currentShardBytes = 0;
        _shardIndex++;
    }

    private async Task FinalizeCurrentShardAsync(CancellationToken cancellationToken)
    {
        if (_currentWriter is not null)
        {
            await _currentWriter.FinalizeAsync(cancellationToken);
            await _currentWriter.DisposeAsync();
            _currentWriter = null;
        }
    }

    private static long EstimateRowSize(Row row)
    {
        long size = 0;
        for (int i = 0; i < row.FieldCount; i++)
        {
            DataValue value = row[i];
            if (value.IsNull)
            {
                continue;
            }

            size += value.Kind switch
            {
                DataKind.Scalar => 4,
                DataKind.UInt8 => 1,
                DataKind.String => value.AsString().Length * 2,
                DataKind.Vector => value.AsVector().Length * 4,
                DataKind.UInt8Array => value.AsUInt8Array().Length,
                _ => 8
            };
        }

        return size;
    }
}
