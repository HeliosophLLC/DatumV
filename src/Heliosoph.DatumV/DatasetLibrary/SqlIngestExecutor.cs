// TODO: fold proper XML doc comments + a JsonSerializerContext into a follow-up PR.
#pragma warning disable CS1591 // missing XML comment for publicly visible type or member

using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.DatumFile.Sidecar;
using Heliosoph.DatumV.DatumFile.V2;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Ingestion;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.DatasetLibrary;

/// <summary>
/// Executes a SELECT statement against the live <see cref="TableCatalog"/>
/// and streams the resulting <see cref="RowBatch"/>es into a fresh
/// <c>.datum</c> file at a caller-supplied path. The result schema is
/// discovered from the first batch (via <see cref="SchemaDetector"/>),
/// the writer is initialised, and remaining batches stream straight
/// through.
/// </summary>
/// <remarks>
/// <para>
/// This is the SQL-shaped counterpart to <see cref="Ingester"/>. Where
/// the classic ingester reads source files via an
/// <see cref="Heliosoph.DatumV.Serialization.IFormatDeserializer"/>, the SQL
/// executor consumes the planner's operator-tree output instead — any
/// expression the SQL surface supports (TVFs like <c>open_archive</c>,
/// computed columns, joins, filters) becomes a valid ingest definition.
/// </para>
/// <para>
/// Stats / sample-preview collection are deliberately skipped in this
/// version. The <c>.datum</c> file is still valid and queryable;
/// downstream consumers that need per-column stats or thumbnail
/// previews will see the same empty manifest as a freshly-created
/// table with no statistics gathered.
/// </para>
/// </remarks>
internal sealed class SqlIngestExecutor
{
    private readonly TableCatalog _catalog;
    private readonly Pool _pool;
    private readonly ILogger<SqlIngestExecutor> _logger;

    // Row-count threshold for the progress callback. Fires every N rows
    // so the UI download chip can show steady forward motion without
    // hitting the SignalR hub on every batch boundary.
    private const long ProgressEmitThreshold = 1000;

    public SqlIngestExecutor(TableCatalog catalog, Pool pool, ILogger<SqlIngestExecutor> logger)
    {
        _catalog = catalog;
        _pool = pool;
        _logger = logger;
    }

    /// <summary>
    /// Parses + binds + plans + executes <paramref name="sql"/>, writing
    /// the row stream to <paramref name="destPath"/> as a <c>.datum</c>
    /// file. The companion <c>.datum-blob</c> sidecar is created
    /// alongside (lazily — only materialises if a column writes blob
    /// payloads).
    /// </summary>
    /// <param name="sql">A single SELECT statement.</param>
    /// <param name="parameters">Bound at parse time before planning.</param>
    /// <param name="destPath">Absolute path to the <c>.datum</c> file to create.</param>
    /// <param name="onRowProgress">
    /// Optional callback fired every <see cref="ProgressEmitThreshold"/> rows
    /// (and once at completion) with the cumulative row count. The download
    /// service wires this to its progress reporter.
    /// </param>
    /// <param name="ct">Cancellation token threaded through the plan's batch enumeration.</param>
    /// <returns>The total row + batch count actually written.</returns>
    public async Task<SqlIngestResult> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, ParameterValue> parameters,
        string destPath,
        Action<long>? onRowProgress,
        CancellationToken ct)
    {
        Statement parsed = SqlParser.ParseStatement(sql);
        if (parsed is not QueryStatement)
        {
            throw new InvalidOperationException(
                "Dataset ingest SQL must be a single SELECT statement.");
        }

        Statement bound = ParameterBinder.Bind(parsed, parameters);
        StatementPlan plan = await _catalog
            .PlanAsync(bound, sourceText: sql)
            .ConfigureAwait(false);

        string sidecarPath = IngesterHelpers.SidecarPathFor(destPath);
        await using FileStream output = File.Create(destPath);
        SidecarWriteStore sidecar = new(sidecarPath);
        DatumFileWriterV2 writer = new(output, sidecar);

        SchemaDetector schemaDetector = new();
        bool initialized = false;
        long rowCount = 0;
        long batchCount = 0;
        long lastProgressEmit = 0;

        try
        {
            await foreach (RowBatch batch in _catalog.ExecuteAsync(plan, ct).ConfigureAwait(false))
            {
                if (!schemaDetector.IsDetected)
                {
                    schemaDetector.Detect(batch);
                    if (schemaDetector.IsDetected)
                    {
                        writer.Initialize(IngesterHelpers.ToV2Descriptors(schemaDetector.Schema));
                        initialized = true;
                    }
                }

                writer.WriteRowBatch(batch);
                rowCount += batch.Count;
                batchCount++;
                // NOTE: don't call _pool.ReturnRowBatch(batch) here — the
                // query plan manages batch lifecycle internally (its
                // ExecuteAsync's finally block returns the previous batch
                // before yielding the next), so a second return throws
                // ObjectDisposedException. This is the divergence from
                // Ingester's pattern, where the deserializer hands raw
                // batches the caller is responsible for returning.

                if (rowCount - lastProgressEmit >= ProgressEmitThreshold)
                {
                    onRowProgress?.Invoke(rowCount);
                    lastProgressEmit = rowCount;
                }
            }

            if (!initialized)
            {
                // Zero-row result: still emit a valid (empty) .datum so the
                // downstream binder doesn't see a missing file.
                writer.Initialize(IngesterHelpers.ToV2Descriptors(new Schema([])));
            }

            writer.FinalizeWriter();
        }
        finally
        {
            writer.Dispose();
            sidecar.Dispose();
        }

        onRowProgress?.Invoke(rowCount);
        _logger.LogInformation(
            "SQL ingest finished: {Rows} rows in {Batches} batches → {DestPath}",
            rowCount, batchCount, destPath);
        return new SqlIngestResult(RowCount: rowCount, BatchCount: batchCount);
    }
}

/// <summary>
/// Row + batch counts produced by a <see cref="SqlIngestExecutor"/> run.
/// </summary>
public sealed record SqlIngestResult(long RowCount, long BatchCount);
