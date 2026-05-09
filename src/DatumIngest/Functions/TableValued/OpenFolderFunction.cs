using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>open_folder(source [, recursion_depth [, path_pattern]]) → table</c>.
/// Yields one row per regular file under a filesystem directory, with body
/// bytes streamed into the query arena — the on-disk-folder analogue of
/// <see cref="OpenArchiveFunction"/>. Designed for recipes that ingest a
/// loose tree of files (already-extracted dataset, scratch directory of
/// in-progress media, drop folder) without first packaging them into an
/// archive.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Schema parity with <c>open_archive</c>.</strong> Same output
/// columns (<c>path STRING, size INT64, modified TIMESTAMPTZ, bytes Array&lt;UInt8&gt;</c>)
/// so any recipe that consumes <c>open_archive</c> can be retargeted at a
/// directory by swapping the call. The <c>path</c> column is relative to
/// the <c>source</c> directory and forward-slashed, so the join shapes
/// recipes use against archive entries (<c>WHERE path = 'wavs/' || id || '.wav'</c>)
/// work unchanged.
/// </para>
/// <para>
/// <strong>Recursion depth.</strong> <c>0</c> (default) walks only the
/// direct children of <c>source</c>. <c>N &gt; 0</c> walks
/// <c>N</c> levels of subdirectories (so <c>1</c> includes <c>source/*</c>
/// and <c>source/*/*</c>). <c>-1</c> walks the entire subtree without
/// limit. The depth check happens at directory-traversal time, not after
/// reading file bytes, so a shallow recursion on a deep tree skips
/// untouched-subtree IO entirely.
/// </para>
/// <para>
/// <strong><c>path_pattern</c> behaviour</strong> mirrors
/// <c>open_archive</c>: a SQL LIKE pattern (<c>%</c> matches zero-or-more,
/// <c>_</c> matches one) applied <em>before</em> the file body is read.
/// Use it to keep the row stream lean when the recursion finds more than
/// you care about. Default <c>'%'</c> emits every file.
/// </para>
/// <para>
/// <strong>What's skipped silently.</strong> Directory entries (we yield
/// the files within them, not the directories themselves), symbolic links
/// to directories (avoided by the enumerator's default; symlinks to files
/// are followed). What's <em>not</em> silently skipped: leading-dot files,
/// <c>__MACOSX/</c>-style metadata directories, <c>thumbs.db</c>,
/// <c>desktop.ini</c>. The raw-scan contract matches <c>open_archive</c> —
/// filter via SQL if you want them gone.
/// </para>
/// <para>
/// <strong>Streaming + arena bounds.</strong> Files are streamed into the
/// query arena via <see cref="Arena.AppendFromStream"/> (no managed
/// <c>byte[]</c> per file, no LOH pressure on large media). Batches flush
/// once their row capacity fills or the per-batch arena watermark
/// (16 MB) is crossed — same shape as <c>open_archive</c>.
/// </para>
/// </remarks>
public sealed class OpenFolderFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    /// <summary>
    /// Per-batch byte watermark. Matches <see cref="OpenArchiveFunction"/> so the
    /// memory profile of a folder ingest looks identical to an archive ingest
    /// to downstream operators.
    /// </summary>
    private const long BatchByteWatermark = 16L * 1024 * 1024;

    private static readonly ColumnLookup OutputColumnLookup =
        new(["path", "size", "modified", "bytes"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_folder";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Walks a filesystem directory and yields one row per regular file: " +
        "open_folder(source [, recursion_depth [, path_pattern]]). Default depth " +
        "0 = top-level only; -1 = unlimited; N = N levels deep. path_pattern is a " +
        "SQL LIKE filter applied before reading file bytes. Columns: " +
        "(path STRING, size INT64, modified TIMESTAMPTZ, bytes Array<UInt8>). " +
        "Schema and semantics parallel open_archive so recipes port cleanly.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters:
            [
                new ParameterSpec("source", DataKindMatcher.Exact(DataKind.String)),
                new ParameterSpec("recursion_depth", DataKindMatcher.Family(DataKindFamily.NumericScalar),
                    IsOptional: true),
                new ParameterSpec("path_pattern", DataKindMatcher.Exact(DataKind.String),
                    IsOptional: true),
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
    public Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length is < 1 or > 3)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 to 3 arguments: open_folder(source [, recursion_depth [, path_pattern]]).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 1 (source) must be STRING.");
        }
        if (argumentKinds.Length >= 2 && !DataValueComparer.IsNumericScalar(argumentKinds[1]))
        {
            throw new FunctionArgumentException(Name,
                "argument 2 (recursion_depth) must be an integer.");
        }
        if (argumentKinds.Length >= 3 && argumentKinds[2] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                "argument 3 (path_pattern) must be STRING (a SQL LIKE pattern).");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length is < 1 or > 3)
        {
            throw new ArgumentException(
                "open_folder requires 1 to 3 arguments: (source [, recursion_depth [, path_pattern]]).");
        }

        string source = arguments[0].AsString();
        int recursionDepth = arguments.Length >= 2 ? (int)arguments[1].ToInt64() : 0;
        string pathPattern = arguments.Length >= 3 ? arguments[2].AsString() : "%";

        if (recursionDepth < -1)
        {
            throw new FunctionArgumentException(Name,
                $"recursion_depth must be -1 (unlimited) or non-negative; got {recursionDepth}.");
        }
        if (!Directory.Exists(source))
        {
            throw new FunctionArgumentException(Name,
                $"source directory '{source}' does not exist or is not accessible.");
        }

        // Normalise the root so per-file relative paths are predictable across
        // trailing-slash / case-folded inputs.
        string root = Path.GetFullPath(source);
        Regex pathMatcher = ExpressionEvaluator.BuildLikeRegex(pathPattern);

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;
        long batchStartBytes = 0;

        await foreach (string filePath in EnumerateFilesAsync(root, recursionDepth, cancellationToken)
                       .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Compute the source-relative, forward-slashed path used for both the
            // LIKE filter and the emitted `path` column. Keeping the filter on the
            // relative form means patterns ('LJSpeech-1.1/wavs/%.wav') compose
            // against directory recipes the same way they do against archive
            // recipes — no leading-absolute-path skew.
            string relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');

            if (!pathMatcher.IsMatch(relativePath)) continue;

            FileInfo info = new(filePath);
            long byteLength = info.Length;
            if (byteLength < 0 || byteLength > int.MaxValue)
            {
                throw new InvalidDataException(
                    $"open_folder entry '{relativePath}' has an invalid length ({byteLength}). " +
                    $"File-bytes payloads must be representable as int32-sized arrays.");
            }
            int length = (int)byteLength;

            if (batch is null)
            {
                batch = context.RentRowBatch(OutputColumnLookup);
                batchStartBytes = batch.Arena.BytesWritten;
            }

            // Stream the bytes directly into the query arena — no managed byte[]
            // per file, so a folder ingest of 10 GB of FLACs doesn't churn Gen2 GC.
            // Files we can't open (kernel-locked system files like DumpStack.log.tmp,
            // pagefile.sys, hiberfil.sys; ACL-denied files; files held with no-share
            // semantics by other processes) are skipped silently — matches the
            // permissions-denied directory branch above and keeps an exploratory
            // `open_folder('C:\\')` from aborting on the first system file.
            if (!TryReadFileIntoArena(filePath, length, batch.Arena, out long offset, out int actualLength))
            {
                continue;
            }

            DataValue modifiedValue = TryGetUtcWriteTime(info, out DateTimeOffset mt)
                ? DataValue.FromTimestampTz(mt)
                : DataValue.Null(DataKind.TimestampTz);

            batch.Add(
            [
                DataValue.FromString(relativePath, batch.Arena),
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

    /// <summary>
    /// Yields absolute paths of files under <paramref name="root"/> respecting
    /// <paramref name="depth"/>: <c>0</c> = direct children only, <c>N</c> = N
    /// levels deep, <c>-1</c> = unlimited. Directories yield their files first,
    /// then descend (depth-first), so the row order is stable per-directory.
    /// </summary>
    private static async IAsyncEnumerable<string> EnumerateFilesAsync(
        string root,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield(); // async iterator surface; the enumeration is sync but the boundary stays awaitable

        foreach (string file in WalkSync(root, depth, currentDepth: 0))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return file;
        }
    }

    private static IEnumerable<string> WalkSync(string dir, int maxDepth, int currentDepth)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break; // surface a permissions error as "no rows from this branch" rather than aborting the whole walk
        }
        catch (DirectoryNotFoundException)
        {
            yield break; // raced with a delete — same handling
        }

        foreach (string file in files)
        {
            yield return file;
        }

        if (maxDepth != -1 && currentDepth >= maxDepth) yield break;

        IEnumerable<string> subdirs;
        try
        {
            subdirs = Directory.EnumerateDirectories(dir);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }
        catch (DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (string subdir in subdirs)
        {
            foreach (string file in WalkSync(subdir, maxDepth, currentDepth + 1))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Opens <paramref name="filePath"/> for read and streams up to
    /// <paramref name="length"/> bytes into <paramref name="arena"/>. Returns
    /// <c>true</c> with <c>(offset, actualLength)</c> from
    /// <see cref="Arena.AppendFromStream"/> on success.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Opens with <see cref="FileShare.ReadWrite"/> + <see cref="FileShare.Delete"/>
    /// to coexist with files actively being written or marked for deletion by other
    /// processes. Files held with stricter share semantics (kernel-owned dumps, live
    /// database files) and files with denied read ACLs surface as <c>false</c>
    /// rather than aborting the walk — the caller skips the row, matching the
    /// permissions-denied behaviour of the directory enumerator.
    /// </para>
    /// </remarks>
    private static bool TryReadFileIntoArena(
        string filePath,
        int length,
        Arena arena,
        out long offset,
        out int actualLength)
    {
        try
        {
            using FileStream fs = new(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536);
            (offset, actualLength) = arena.AppendFromStream(fs, length);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            offset = 0;
            actualLength = 0;
            return false;
        }
    }

    /// <summary>
    /// Reads the file's last-write time as a UTC-aware
    /// <see cref="DateTimeOffset"/>. Some filesystems / antivirus scenarios can
    /// fail this with a transient IO error; in those cases we surface NULL
    /// rather than failing the whole row.
    /// </summary>
    private static bool TryGetUtcWriteTime(FileInfo info, out DateTimeOffset modified)
    {
        try
        {
            modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            return true;
        }
        catch (IOException)
        {
            modified = default;
            return false;
        }
    }
}
