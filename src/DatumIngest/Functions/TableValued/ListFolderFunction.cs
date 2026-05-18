using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DatumIngest.Execution;
using DatumIngest.Manifest;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>list_folder(source [, recursion_depth [, path_pattern]]) → table</c>.
/// Yields one row per regular file under a filesystem directory with only
/// the per-entry metadata (<c>path</c>, <c>size</c>, <c>modified</c>) — the
/// no-body counterpart to <see cref="OpenFolderFunction"/>. Use when you
/// only need to know <em>what's there</em>: pre-flight checks, "how many
/// files match this pattern", "what's the largest file", or generating a
/// manifest to feed into a subsequent <c>open_folder</c> + <c>JOIN</c>.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Signature and walk semantics parity with <c>open_folder</c></strong>:
/// same <c>recursion_depth</c> (default <c>0</c>, <c>-1</c> = unlimited,
/// <c>N</c> = N levels deep), same <c>path_pattern</c> SQL LIKE filter, same
/// forward-slashed relative-to-source <c>path</c> column. Recipes can swap
/// between the two by changing the function name when they decide whether
/// they need bytes.
/// </para>
/// <para>
/// <strong>Listed-but-not-readable handling differs from <c>open_folder</c>.</strong>
/// Because no file body is opened, kernel-locked files (<c>DumpStack.log.tmp</c>,
/// <c>pagefile.sys</c>) and ACL-denied files still appear in the row stream —
/// their size and modification time live in the directory entry, which is
/// readable. This is the deliberate counterpart to <c>open_folder</c>'s
/// silent skip: <c>list_folder</c> shows you the locked files exist; users
/// who want to filter them out can <c>WHERE NOT path LIKE 'DumpStack%'</c>.
/// Directory-level permissions errors are still silently skipped (the walk
/// can't enumerate inside a no-read directory).
/// </para>
/// <para>
/// <strong>Cheap rows.</strong> Each entry is path + size + modified — no
/// arena pressure from file bytes, no LOH concerns, no per-file disk read.
/// A <c>list_folder('C:\\Users\\me', recursion_depth := -1)</c> over hundreds
/// of thousands of files completes in seconds, dominated by directory
/// enumeration rather than IO.
/// </para>
/// </remarks>
public sealed class ListFolderFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup =
        new(["path", "size", "modified"]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "list_folder";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Walks a filesystem directory and yields one row per regular file with " +
        "metadata only (no bytes): list_folder(source [, recursion_depth [, path_pattern]]). " +
        "The no-body counterpart to open_folder — use for listings, counts, size " +
        "audits, and pre-flight checks before committing to byte reads. Columns: " +
        "(path STRING, size INT64, modified TIMESTAMPTZ). Same recursion / pattern " +
        "semantics as open_folder; recipes swap function names when they decide " +
        "whether they need bytes.";

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
        if (argumentKinds.Length is < 1 or > 3)
        {
            throw new FunctionArgumentException(Name,
                "requires 1 to 3 arguments: list_folder(source [, recursion_depth [, path_pattern]]).");
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
                "list_folder requires 1 to 3 arguments: (source [, recursion_depth [, path_pattern]]).");
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

        string root = Path.GetFullPath(source);
        Regex pathMatcher = ExpressionEvaluator.BuildLikeRegex(pathPattern);

        CancellationToken cancellationToken = context.CancellationToken;
        RowBatch? batch = null;

        await foreach (string filePath in EnumerateFilesAsync(root, recursionDepth, cancellationToken)
                       .ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string relativePath = Path.GetRelativePath(root, filePath).Replace('\\', '/');
            if (!pathMatcher.IsMatch(relativePath)) continue;

            // Pull size and mtime from the directory-entry-resident FileInfo —
            // no file open, so kernel-locked files (DumpStack.log.tmp etc.) and
            // ACL-denied files still surface metadata. Truly malformed directory
            // entries (corrupt filesystem, deleted-mid-walk races) fail with
            // IOException; we skip those rather than aborting the whole walk.
            if (!TryReadMetadata(filePath, out long size, out DateTimeOffset? modified))
            {
                continue;
            }

            batch ??= context.RentRowBatch(OutputColumnLookup);

            DataValue modifiedValue = modified is { } mt
                ? DataValue.FromTimestampTz(mt)
                : DataValue.Null(DataKind.TimestampTz);

            batch.Add(
            [
                DataValue.FromString(relativePath, batch.Arena),
                DataValue.FromInt64(size),
                modifiedValue,
            ]);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null && batch.Count > 0)
        {
            yield return batch;
        }
    }

    /// <summary>
    /// Yields absolute paths of files under <paramref name="root"/> respecting
    /// <paramref name="depth"/>. Mirrors <see cref="OpenFolderFunction"/>'s walk
    /// so the row order and depth semantics match between the two TVFs.
    /// </summary>
    private static async IAsyncEnumerable<string> EnumerateFilesAsync(
        string root,
        int depth,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

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
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }

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
        catch (UnauthorizedAccessException) { yield break; }
        catch (DirectoryNotFoundException) { yield break; }

        foreach (string subdir in subdirs)
        {
            foreach (string file in WalkSync(subdir, maxDepth, currentDepth + 1))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Reads <paramref name="filePath"/>'s size and last-write time from the
    /// directory entry — no file open required. Returns <c>false</c> on
    /// corrupt-entry / mid-walk-deleted races so the caller skips the row
    /// rather than aborting the walk.
    /// </summary>
    private static bool TryReadMetadata(string filePath, out long size, out DateTimeOffset? modified)
    {
        try
        {
            FileInfo info = new(filePath);
            size = info.Length;
            try
            {
                modified = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            }
            catch (IOException)
            {
                // Some filesystems / AV scenarios can fail mtime even when size
                // succeeds — surface NULL rather than dropping the row entirely.
                modified = null;
            }
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            size = 0;
            modified = null;
            return false;
        }
    }
}
