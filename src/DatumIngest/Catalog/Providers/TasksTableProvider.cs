using System.Runtime.CompilerServices;

using DatumIngest.Catalog.Registries;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Pooling;

namespace DatumIngest.Catalog.Providers;

/// <summary>
/// Virtual table surfacing the engine-defined task contract vocabulary
/// (<see cref="TaskTypeRegistry.Entries"/>) as a SQL-queryable view at
/// <c>datum_catalog.tasks</c>. Users discover what <c>IMPLEMENTS</c>
/// accepts:
/// <code>
/// SELECT name, input_signature, return_signature, description
/// FROM datum_catalog.tasks
/// ORDER BY name;
/// </code>
/// </summary>
/// <remarks>
/// <para>
/// Schema (5 columns):
/// <list type="table">
///   <item><term>name</term><description>Canonical contract identifier (<c>"ImageClassifier"</c>, <c>"TextEmbedder"</c>, …).</description></item>
///   <item><term>family</term><description>Coarse grouping — <c>"Text"</c>, <c>"Image"</c>, <c>"Audio"</c>, <c>"Video"</c>, <c>"Multimodal"</c>, <c>"Structured"</c>. Used by the model-browser filter UI.</description></item>
///   <item><term>input_signature</term><description>Parenthesised, comma-separated input slot list — e.g. <c>"(Image)"</c>, <c>"(Image, Array&lt;String&gt;)"</c>.</description></item>
///   <item><term>return_signature</term><description>The return slot — e.g. <c>"ScoredClass"</c>, <c>"Array&lt;OcrLine&gt;"</c>, <c>"Float32"</c>.</description></item>
///   <item><term>description</term><description>One-line human summary.</description></item>
/// </list>
/// </para>
/// <para>
/// Lives under <c>datum_catalog.*</c> (engine-defined catalog metadata,
/// alongside <c>datum_catalog.functions</c> and <c>datum_catalog.function_parameters</c>),
/// not under <c>system.*</c> — task contracts are the typed interface
/// layer for models, parallel to function signatures.
/// </para>
/// </remarks>
internal sealed class TasksTableProvider : NonSeekableTableProviderBase
{
    /// <summary>The conventional table name registered in the catalog.</summary>
    public const string TableName = "datum_catalog.tasks";

    private static readonly Schema _schema = BuildSchema();

    /// <summary>
    /// Creates a provider over the static task contract vocabulary. No
    /// per-instance state — every scan walks
    /// <see cref="TaskTypeRegistry.Entries"/> directly.
    /// </summary>
    public TasksTableProvider(Pool pool)
        : base(pool, QualifiedName.Parse(TableName))
    {
    }

    /// <inheritdoc/>
    public override long GetRowCount() => TaskTypeRegistry.Entries.Count;

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

        ColumnLookup lookup = new(_schema.Columns.Select(c => c.Name).ToArray());
        RowBatch? batch = null;

        foreach (TaskTypeRegistry.TaskContract contract in TaskTypeRegistry.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= Pool.RentRowBatch(lookup, DefaultBatchSize, targetArena);

            string inputSig = "(" + string.Join(", ", contract.InputKinds.Select(s => s.ToString())) + ")";

            DataValue[] cells = Pool.RentDataValues(_schema.Columns.Count);
            cells[0] = DataValue.FromString(contract.Name, batch.Arena);
            cells[1] = DataValue.FromString(contract.Family.ToString(), batch.Arena);
            cells[2] = DataValue.FromString(inputSig, batch.Arena);
            cells[3] = DataValue.FromString(contract.ReturnKind.ToString(), batch.Arena);
            cells[4] = DataValue.FromString(contract.Description, batch.Arena);
            batch.Add(cells);

            if (batch.IsFull) { yield return batch; batch = null; }
        }

        if (batch is not null) { yield return batch; }

        await Task.CompletedTask;
    }

    private static Schema BuildSchema() => new(
    [
        new ColumnInfo("name",             DataKind.String, nullable: false),
        new ColumnInfo("family",           DataKind.String, nullable: false),
        new ColumnInfo("input_signature",  DataKind.String, nullable: false),
        new ColumnInfo("return_signature", DataKind.String, nullable: false),
        new ColumnInfo("description",      DataKind.String, nullable: false),
    ]);
}
