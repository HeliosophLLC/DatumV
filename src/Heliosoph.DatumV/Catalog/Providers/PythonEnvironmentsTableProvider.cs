using System.Runtime.CompilerServices;

using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models.Python;
using Heliosoph.DatumV.Parsing.Ast;
using Heliosoph.DatumV.Pooling;

namespace Heliosoph.DatumV.Catalog.Providers;

/// <summary>
/// Virtual table listing every venv currently materialised under the
/// engine's managed Python directory. One row per venv with name,
/// Python version, absolute path, disk size, declared requirements,
/// and creation time. Companion to
/// <see cref="PythonPathsTableProvider"/> which surfaces the
/// directory-level summary.
/// </summary>
/// <remarks>
/// <para>
/// Use cases:
/// <list type="bullet">
///   <item>Disk audit (<c>SELECT name, size_bytes FROM system_python_environments ORDER BY size_bytes DESC</c>)</item>
///   <item>Dependency tracking (<c>SELECT name, requirements FROM system_python_environments WHERE name = 'nvidia_sana'</c>)</item>
///   <item>Inventory diff after model changes (compare snapshots across DDL commits)</item>
/// </list>
/// </para>
/// <para>
/// Per-row data is read by walking each venv directory on every scan.
/// Cheap when no venvs exist (empty result); for hosts with many large
/// venvs the scan is dominated by file-system traversal, not by SQL
/// machinery. Not optimised for high-frequency polling — this is a
/// status surface, not a hot-path operator.
/// </para>
/// <para>
/// Schema (6 columns):
/// <list type="table">
///   <item><term>name</term><description>Venv identifier — typically the model name it backs.</description></item>
///   <item><term>python_version</term><description>Version string read from <c>pyvenv.cfg</c>; "unknown" when the cfg is unreadable.</description></item>
///   <item><term>path</term><description>Absolute path of the venv directory.</description></item>
///   <item><term>size_bytes</term><description>Sum of file sizes inside the directory. Overstates actual disk because hardlinks count multiply.</description></item>
///   <item><term>requirements</term><description>Newline-separated requirement strings from the <c>datum-requirements.txt</c> sidecar.</description></item>
///   <item><term>created_at</term><description>UTC creation time of the venv directory.</description></item>
/// </list>
/// </para>
/// </remarks>
public sealed class PythonEnvironmentsTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name.</summary>
    public const string TableName = "system.python_environments";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "python_environments");

    private static readonly Schema _schema = BuildSchema();

    private readonly PythonEnvironmentManager _python;

    /// <summary>
    /// Creates a provider surfacing <paramref name="python"/>'s venv inventory.
    /// </summary>
    public PythonEnvironmentsTableProvider(Pool pool, PythonEnvironmentManager python)
        : base(pool, QualifiedTableName)
    {
        _python = python;
    }

    /// <inheritdoc/>
    public override long GetRowCount() => _python.EnumerateVenvs().Count;

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

        IReadOnlyList<PythonEnvironmentManager.VenvInfo> venvs = _python.EnumerateVenvs();

        _ = requiredColumns;
        _ = filterHint;

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        // Stable ordering: alphabetical by name. UIs that diff
        // snapshots benefit from consistent row positions.
        foreach (PythonEnvironmentManager.VenvInfo info in
            venvs.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
            cells[0] = DataValue.FromString(info.Name, batch.Arena);
            cells[1] = DataValue.FromString(info.PythonVersion, batch.Arena);
            cells[2] = DataValue.FromString(info.Path, batch.Arena);
            cells[3] = DataValue.FromInt64(info.SizeBytes);
            cells[4] = DataValue.FromString(info.Requirements, batch.Arena);
            cells[5] = DataValue.FromTimestampTz(info.CreatedAt);
            batch.Add(cells);

            if (batch.IsFull)
            {
                yield return batch;
                batch = null;
            }
        }

        if (batch is not null) yield return batch;
        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",           DataKind.String,      nullable: false),
        new ColumnInfo("python_version", DataKind.String,      nullable: false),
        new ColumnInfo("path",           DataKind.String,      nullable: false),
        new ColumnInfo("size_bytes",     DataKind.Int64,       nullable: false),
        new ColumnInfo("requirements",   DataKind.String,      nullable: false),
        new ColumnInfo("created_at",     DataKind.TimestampTz, nullable: false),
    ]);
}
