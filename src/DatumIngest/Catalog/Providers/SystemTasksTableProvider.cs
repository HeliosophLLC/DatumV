using System.Runtime.CompilerServices;

using DatumIngest.Catalog.Registries;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table surfacing the catalog's task-dispatch state at
/// <c>system.tasks</c>. One row per (task contract × model identifier
/// implementing it). Powers the answer to "what does
/// <c>tasks.depth_metric(image)</c> actually resolve to right now, and
/// what alternatives are available?":
/// <code>
/// SELECT task, model, recommended, installed
/// FROM system.tasks
/// WHERE task = 'DepthEstimatorMetric'
/// ORDER BY recommended DESC, installed DESC, model;
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Distinct from <see cref="TaskContractsTableProvider"/>
/// (<c>system.task_contracts</c>) — that table is the static type
/// interface registry (one row per <see cref="TaskTypeRegistry"/>
/// contract); this one is the dynamic dispatch view (one row per
/// candidate model implementing each contract).
/// </para>
/// <para>
/// Schema (4 columns):
/// <list type="table">
///   <item><term>task</term><description>Canonical contract identifier (matches <c>system.task_contracts.name</c>).</description></item>
///   <item><term>model</term><description>Candidate model identifier the catalog declares implements the contract.</description></item>
///   <item><term>recommended</term><description><see langword="true"/> when this row matches <c>catalog.tasks.recommended[task]</c> — the dispatcher target for <c>tasks.&lt;task&gt;(...)</c>.</description></item>
///   <item><term>installed</term><description><see langword="true"/> when the model identifier currently has a live registration in <see cref="ModelCatalog"/> or <see cref="ModelRegistry"/>. Mirrors <c>system.models.residency = 'callable'</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// Rows materialise on every scan against the live
/// <see cref="ICatalogVocabulary"/> + <see cref="ModelCatalog"/> +
/// <see cref="ModelRegistry"/> registries — install/uninstall churn is
/// reflected in the next query without cache invalidation.
/// </para>
/// </remarks>
public sealed class SystemTasksTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional fully-qualified table name registered in the catalog.</summary>
    public const string TableName = "system.tasks";

    /// <summary>The canonical <see cref="QualifiedName"/> for this provider.</summary>
    public static readonly QualifiedName QualifiedTableName = new("system", "tasks");

    private static readonly Schema _schema = BuildSchema();

    private readonly ICatalogVocabulary _vocabulary;
    private readonly ModelCatalog _modelCatalog;
    private readonly ModelRegistry? _declaredModels;

    /// <summary>
    /// Creates a provider over the live dispatch view.
    /// </summary>
    /// <param name="pool">Buffer pool for renting row batches.</param>
    /// <param name="vocabulary">The catalog vocabulary surface (carries the task → candidate-model join from <c>catalog.json</c>).</param>
    /// <param name="modelCatalog">The built-in model registry (provides callable-residency for builtin identifiers).</param>
    /// <param name="declaredModels">Optional SQL-defined registry (provides callable-residency for user CREATE MODEL identifiers).</param>
    public SystemTasksTableProvider(Pool pool, ICatalogVocabulary vocabulary, ModelCatalog modelCatalog, ModelRegistry? declaredModels = null)
        : base(pool, QualifiedTableName)
    {
        _vocabulary = vocabulary;
        _modelCatalog = modelCatalog;
        _declaredModels = declaredModels;
    }

    /// <inheritdoc/>
    public override long GetRowCount()
    {
        long n = 0;
        foreach (TaskTypeRegistry.TaskContract contract in TaskTypeRegistry.Entries)
        {
            n += _vocabulary.CandidatesForTask(contract.Name).Count;
        }
        return n;
    }

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

        // Snapshot the callable identifier set once for the scan. Same
        // grain as ModelsTableProvider — builtin + declared rows are
        // both callable.
        HashSet<string> callable = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelCatalogEntry e in _modelCatalog.Entries.Values)
        {
            callable.Add(e.Name);
        }
        if (_declaredModels is not null)
        {
            foreach (ModelDescriptor d in _declaredModels.Entries)
            {
                callable.Add(d.Name);
            }
        }

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        // Walk every contract in dictionary order so the table is stable
        // across runs. Within a contract, candidates appear as the
        // vocabulary materialised them (which is iteration order of
        // catalog.models[].versions[].models[]).
        foreach (TaskTypeRegistry.TaskContract contract in TaskTypeRegistry.Entries)
        {
            IReadOnlyList<CatalogTaskCandidate> candidates = _vocabulary.CandidatesForTask(contract.Name);
            if (candidates.Count == 0) continue;

            foreach (CatalogTaskCandidate cand in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

                DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
                cells[0] = DataValue.FromString(cand.Task, batch.Arena);
                cells[1] = DataValue.FromString(cand.ModelIdentifier, batch.Arena);
                cells[2] = DataValue.FromBoolean(cand.IsRecommended);
                cells[3] = DataValue.FromBoolean(callable.Contains(cand.ModelIdentifier));
                batch.Add(cells);

                if (batch.IsFull) { yield return batch; batch = null; }
            }
        }

        if (batch is not null) { yield return batch; }

        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("task",        DataKind.String,  nullable: false),
        new ColumnInfo("model",       DataKind.String,  nullable: false),
        new ColumnInfo("recommended", DataKind.Boolean, nullable: false),
        new ColumnInfo("installed",   DataKind.Boolean, nullable: false),
    ]);
}
