using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Serialization.MediaBag;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_archive(source [, path_pattern]) → table</c>. Opens a ZIP / TAR /
/// TAR.GZ / TAR.BZ2 archive and yields one row per regular-file entry, streaming
/// N rows per batch with the body bytes materialized into the query arena. The
/// load-bearing primitive behind SQL-recipe dataset ingestion — recipes compose
/// CTAS / INSERT SELECT over this TVF with the existing CSV / JSON readers and
/// media accessors to produce shaped tables from raw archives.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Path-pattern filter.</strong> The optional <c>path_pattern</c>
/// argument is a SQL LIKE pattern (<c>%</c> matches zero-or-more characters,
/// <c>_</c> matches one). Entries whose <c>path</c> does not match the pattern
/// are skipped <em>without</em> decompressing their body bytes — material for
/// recipes that walk the same archive twice (transcripts + media). When the
/// argument is omitted the default <c>'%'</c> matches every entry.
/// </para>
/// <para>
/// <strong>No automatic metadata filtering.</strong> OS / editor metadata
/// entries (<c>__MACOSX/</c>, <c>.DS_Store</c>, <c>thumbs.db</c>,
/// <c>desktop.ini</c>, leading-dot files) are returned in the row stream —
/// recipes that want them dropped add the appropriate <c>WHERE path NOT LIKE …</c>
/// clauses. This is the deliberate raw-scan contract; the homogeneous-media-bag
/// pipeline (<see cref="MediaBagDeserializer"/>) applies the filter at its own
/// layer.
/// </para>
/// <para>
/// <strong>Streaming.</strong> Batches are bounded by either the operator's
/// row capacity or a 16 MB arena-bytes watermark, whichever comes first. For a
/// 10 GB FLAC archive that means ~50 entries per batch (at typical ~300 KB
/// FLACs), the body bytes flow into a per-batch arena, the batch flushes to
/// the downstream writer, and the next batch rents a fresh recycled arena.
/// </para>
/// <para>
/// <strong>Arena isolation.</strong> This TVF deliberately bypasses the
/// one-arena-per-query model and rents its own arena per yielded batch. A
/// multi-GB media archive would otherwise pile every entry's bytes into the
/// shared per-query Store and blow the per-arena anonymous-reservation cap
/// long before reaching the writer. With per-batch arenas the downstream
/// consumer's <c>ReturnRowBatch</c> drops the arena's last reference and the
/// bytes recycle back to the pool; only the live in-flight batch's worth of
/// bytes (the 16 MB watermark) is resident at any moment.
/// </para>
/// </remarks>
public sealed class OpenArchiveFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <summary>
    /// Per-batch byte watermark. Flushes the batch once the query arena has
    /// absorbed this many bytes of entry payload during the current batch.
    /// Matches the ingest-pipeline default; small enough to keep memory bounded
    /// for multi-MB-per-entry archives without making row-overhead the
    /// bottleneck on small-entry archives.
    /// </summary>
    private const long BatchByteWatermark = 16L * 1024 * 1024;

    private static readonly ColumnLookup OutputColumnLookup =
        new(["path", "size", "modified", "bytes"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_archive";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an archive (zip, tar, tar.gz, tar.bz2) and yields one row per regular-file " +
        "entry: open_archive(source [, path_pattern]). path_pattern is a SQL LIKE filter " +
        "applied before body decompression — entries not matching are skipped without " +
        "reading their bytes. Columns: (path STRING, size INT64, modified TIMESTAMPTZ, bytes Array<UInt8>).";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("path_pattern", DataKindMatcher.Exact(DataKind.String), IsOptional: true),
            ],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("path", DataKind.String, nullable: false),
                new ColumnInfo("size", DataKind.Int64, nullable: false),
                new ColumnInfo("modified", DataKind.TimestampTz, nullable: true),
                new ColumnInfo("bytes", DataKind.UInt8, nullable: false) { IsArray = true },
            ])),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        IValueStore constantStore,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length is not (1 or 2))
        {
            throw new FunctionArgumentException(Name,
                "requires 1 or 2 arguments: open_archive(source [, path_pattern]).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (source) must be STRING.");
        }
        if (argumentKinds.Length == 2 && argumentKinds[1] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (path_pattern) must be STRING (a SQL LIKE pattern).");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length is not (1 or 2))
        {
            throw new ArgumentException(
                "open_archive requires 1 or 2 arguments: (source [, path_pattern]).");
        }

        string source = arguments[0].AsString();
        string pathPattern = arguments.Length == 2 ? arguments[1].AsString() : "%";

        // Pre-compile the LIKE → Regex once per invocation. Since the TVF processes
        // one pattern per call, the per-evaluator cache isn't load-bearing here —
        // we go straight to the canonical translator.
        Regex pathMatcher = ExpressionEvaluator.BuildLikeRegex(pathPattern);

        IMediaBagReader reader = OpenArchiveReader.Open(source);
        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        long batchStartBytes = 0;

        await foreach (MediaBagEntry entry in reader.EnumerateAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!pathMatcher.IsMatch(entry.FullName))
            {
                // Filtered out — do NOT touch entry.Body, so the underlying ZIP/TAR
                // reader skips body decompression for this entry. This is the whole
                // reason path_pattern is an explicit arg rather than a WHERE clause.
                continue;
            }

            long byteLength = entry.Length;
            if (byteLength < 0 || byteLength > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"open_archive entry '{entry.FullName}' has an invalid uncompressed " +
                    $"length ({byteLength}). Archive entries must be representable as int32-sized payloads.");
            }
            int length = (int)byteLength;

            if (batch is null)
            {
                // Per-batch arena (see "Arena isolation" remark): rent a fresh
                // arena from the pool instead of the per-query Store, so the
                // entry bytes streamed into this batch are released the moment
                // the downstream consumer returns the batch.
                batch = context.Pool.RentRowBatch(OutputColumnLookup, context.BatchSize, arena: null);
                batch.Types = context.Types;
                batch.TypeIdTranslations = context.TypeIdTranslations;
                batchStartBytes = batch.Arena.BytesWritten;
            }

            // Stream the body bytes directly into the query arena — no managed
            // byte[] allocation per entry, no LOH pressure on archives with
            // multi-MB image / audio entries.
            (long offset, int actualLength) = batch.Arena.AppendFromStream(entry.Body, length);

            DataValue modifiedValue = entry.Modified is { } mt
                ? DataValue.FromTimestampTz(mt)
                : DataValue.Null(DataKind.TimestampTz);

            batch.Add(
            [
                DataValue.FromString(entry.FullName, batch.Arena),
                DataValue.FromInt64(actualLength),
                modifiedValue,
                DataValue.FromByteArrayAtOffset(offset, actualLength),
            ]);

            if (batch.IsFull || (batch.Arena.BytesWritten - batchStartBytes) >= BatchByteWatermark)
            {
                yield return batch;
                batch = null;
                batchStartBytes = 0;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }
}
