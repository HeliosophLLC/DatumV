using System.Runtime.CompilerServices;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Serialization.Hdf5;
using PureHDF;
using ExecutionContext = Heliosoph.DatumV.Execution.ExecutionContext;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// <c>open_h5_meta(path) → table</c>. Opens an HDF5 file and yields one
/// row per object in the file tree (the root group, every descendant
/// group, every dataset), with typed metadata plus the object's
/// attributes folded into a JSON column. The interrogation TVF for
/// HDF5: call this first to see the structure of an unfamiliar file
/// before pulling rows out with <c>open_h5_dataset</c>.
/// </summary>
/// <remarks>
/// <para>
/// HDF5 files are hierarchical — datasets live at paths inside groups,
/// like a tiny in-file filesystem. Real datasets out of Python / ML
/// pipelines commonly nest 3-5 levels deep (e.g. <c>/embeddings/train/x</c>,
/// <c>/metadata/instrument/filter</c>). The walker visits every node
/// depth-first, so the row stream mirrors the file's logical layout.
/// </para>
/// <para>
/// The <c>element_kind</c> / <c>dimensions</c> / <c>is_scalar</c>
/// columns are only meaningful for datasets — group rows leave them
/// <c>NULL</c>. The <c>is_supported</c> flag tells you which datasets
/// <c>open_h5_dataset</c> can read in v1 (any of the standard scalar
/// integer / float / string dtypes). Compound and other uncommon
/// dtypes show up as <c>is_supported = false</c>; they parse the
/// metadata cleanly but their rows aren't pullable until follow-up
/// work lands.
/// </para>
/// </remarks>
public sealed class OpenH5MetaFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup OutputColumnLookup = new(
    [
        "path", "kind", "element_kind", "dimensions",
        "is_scalar", "is_supported", "attribute_count", "attributes",
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "open_h5_meta";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Opens an HDF5 file and yields one row per group / dataset in the tree: " +
        "open_h5_meta(path). Columns: (path STRING, kind STRING, element_kind STRING?, " +
        "dimensions INT64[]?, is_scalar BOOLEAN, is_supported BOOLEAN, attribute_count INT32, " +
        "attributes JSON). Read this first to see what's inside an HDF5 file.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: new Schema(
            [
                new ColumnInfo("path", DataKind.String, nullable: false),
                new ColumnInfo("kind", DataKind.String, nullable: false),
                new ColumnInfo("element_kind", DataKind.String, nullable: true),
                new ColumnInfo("dimensions", DataKind.Int64, nullable: true) { IsArray = true },
                new ColumnInfo("is_scalar", DataKind.Boolean, nullable: false),
                new ColumnInfo("is_supported", DataKind.Boolean, nullable: false),
                new ColumnInfo("attribute_count", DataKind.Int32, nullable: false),
                new ColumnInfo("attributes", DataKind.Json, nullable: false),
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
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name, "requires 1 argument: open_h5_meta(path).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name, "argument 1 (path) must be STRING.");
        }
        return Signatures[0].FixedOutputSchema!;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(
        ValueRef[] arguments,
        ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("open_h5_meta requires 1 argument: (path).");
        }

        string path = arguments[0].AsString();
        await foreach (RowBatch batch in StreamRowsAsync(path, context).ConfigureAwait(false))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<RowBatch> StreamRowsAsync(
        string path,
        ExecutionContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken = context.CancellationToken;

        // PureHDF doesn't have a transparent .h5.gz wrapper — gzipped HDF5
        // files would have to be staged to a temp path. They're not common
        // in the wild (HDF5 has its own internal compression), so we open
        // the path directly and the descriptor's gzip handling does not
        // apply.
        using var file = H5File.OpenRead(path);

        RowBatch? batch = null;
        await Task.Yield();

        foreach (Hdf5ObjectDescriptor obj in Hdf5ObjectWalker.Walk(file))
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch ??= context.RentRowBatch(OutputColumnLookup);
            DataValue[] row = BuildRow(obj, batch.Arena, context);
            batch.Add(row);

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

    private static DataValue[] BuildRow(
        Hdf5ObjectDescriptor obj,
        IValueStore arena,
        ExecutionContext context)
    {
        DataValue[] row = context.Pool.RentDataValues(OutputColumnLookup.Count);

        row[0] = DataValue.FromString(obj.Path, arena);
        row[1] = DataValue.FromString(KindToString(obj.Kind), arena);

        if (obj.DatasetType is { } dt)
        {
            row[2] = DataValue.FromString(dt.IsSupported ? dt.ElementKind.ToString() : "Unknown", arena);
            long[] dims = new long[dt.Dimensions.Count];
            for (int i = 0; i < dims.Length; i++) dims[i] = checked((long)dt.Dimensions[i]);
            row[3] = DataValue.FromArenaArray<long>(dims, DataKind.Int64, arena);
            row[4] = DataValue.FromBoolean(dt.IsScalar);
            row[5] = DataValue.FromBoolean(dt.IsSupported);
        }
        else
        {
            row[2] = DataValue.Null(DataKind.String);
            row[3] = DataValue.NullArrayOf(DataKind.Int64);
            row[4] = DataValue.FromBoolean(false);
            row[5] = DataValue.FromBoolean(true);
        }

        row[6] = DataValue.FromInt32(obj.Attributes.Count);
        row[7] = DataValue.FromJson(Hdf5AttributesJson.Build(obj.Attributes), arena);
        return row;
    }

    private static string KindToString(Hdf5ObjectKind kind) =>
        kind switch
        {
            Hdf5ObjectKind.Group => "group",
            Hdf5ObjectKind.Dataset => "dataset",
            _ => "unknown",
        };
}
