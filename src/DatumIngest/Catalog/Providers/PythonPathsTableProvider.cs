using System.Runtime.CompilerServices;

using DatumIngest.Model;
using DatumIngest.Models.Python;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Single-row virtual table exposing the engine-managed Python
/// toolchain's on-disk layout: data root, uv binary path, Python
/// install dir, venvs dir, and aggregate disk usage. Companion to
/// <see cref="PythonEnvironmentsTableProvider"/> which lists
/// per-venv detail.
/// </summary>
/// <remarks>
/// <para>
/// Surfaces what users need to answer "where is the engine putting
/// Python stuff on my disk?" without digging through Settings. Read
/// from <see cref="PythonEnvironmentManager"/>'s
/// <see cref="PythonEnvironmentManager.GetPathsSummary"/> on every
/// scan; values reflect live disk state.
/// </para>
/// <para>
/// Schema (6 columns):
/// <list type="table">
///   <item><term>data_root</term><description>Root directory the engine treats as its Python-toolchain home.</description></item>
///   <item><term>uv_binary_path</term><description>Cached uv executable path. Exists when <c>uv_installed</c> is true.</description></item>
///   <item><term>uv_installed</term><description>True when the uv binary is on disk; false on fresh systems where no Python-backed model has been used yet.</description></item>
///   <item><term>python_install_dir</term><description>Where managed Python interpreters live.</description></item>
///   <item><term>venvs_dir</term><description>Where per-model venvs live.</description></item>
///   <item><term>total_bytes</term><description>Logical sum of file sizes across uv + python + venvs subtrees. Overstates actual usage because hardlinks count multiply.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class PythonPathsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.python_paths";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "python_paths");

    private static readonly Schema _schema = BuildSchema();

    private readonly PythonEnvironmentManager _python;

    /// <summary>
    /// Creates a provider surfacing <paramref name="python"/>'s
    /// paths + disk usage summary.
    /// </summary>
    public PythonPathsTableProvider(Pool pool, PythonEnvironmentManager python)
        : base(pool, QualifiedTableName)
    {
        _python = python;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => 1;

    /// <inheritdoc/>
    public override Schema GetSchema() => _schema;

    /// <inheritdoc/>
    public override async IAsyncEnumerable<RowBatch> ScanAsync(
        IReadOnlySet<string>? requiredColumns,
        Expression? filterHint,
        Arena? targetArena,
        [EnumeratorCancellation] CancellationToken cancellationToken,
        Model.TypeIdTranslationTable? typeIdTranslations = null)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        _ = requiredColumns;
        _ = filterHint;

        PythonEnvironmentManager.PythonPathsSummary summary = _python.GetPathsSummary();

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch batch = Pool.RentRowBatch(lookup, capacity: 1, targetArena);
        DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);

        cells[0] = DataValue.FromString(summary.DataRoot, batch.Arena);
        cells[1] = DataValue.FromString(summary.UvBinaryPath, batch.Arena);
        cells[2] = DataValue.FromBoolean(summary.UvInstalled);
        cells[3] = DataValue.FromString(summary.PythonInstallDir, batch.Arena);
        cells[4] = DataValue.FromString(summary.VenvsDir, batch.Arena);
        cells[5] = DataValue.FromInt64(summary.TotalBytes);

        batch.Add(cells);
        yield return batch;
        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("data_root",          DataKind.String,  nullable: false),
        new ColumnInfo("uv_binary_path",     DataKind.String,  nullable: false),
        new ColumnInfo("uv_installed",       DataKind.Boolean, nullable: false),
        new ColumnInfo("python_install_dir", DataKind.String,  nullable: false),
        new ColumnInfo("venvs_dir",          DataKind.String,  nullable: false),
        new ColumnInfo("total_bytes",        DataKind.Int64,   nullable: false),
    ]);
}
