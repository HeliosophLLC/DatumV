using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>inference.onnx_inspect_meta(path) → table</c>. One-row TVF returning
/// top-level metadata read from an ONNX file's protobuf header: producer
/// tool + version, opset version, the distinct operator types the graph
/// references, and the on-disk file size. Answers "what is this thing and
/// is it new enough for what I have installed?" without loading any
/// weights.
/// </summary>
/// <remarks>
/// Pairs with <c>inference.onnx_inspect()</c> (which shows the IO signature)
/// and <c>inference.infer_compatibility()</c> (which compares the file's
/// opset against each backend's supported ceiling). Together they answer
/// "should I even bother CREATE MODELing this file?" before the user types
/// the body.
/// </remarks>
public sealed class OnnxInspectMetaFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ColumnLookupCached = new(
        ["producer_name", "producer_version", "opset", "ir_version", "required_ops", "file_size_bytes"]);

    private static readonly Schema OutputSchema = new(
    [
        new ColumnInfo("producer_name", DataKind.String, nullable: false),
        new ColumnInfo("producer_version", DataKind.String, nullable: false),
        new ColumnInfo("opset", DataKind.Int32, nullable: false),
        new ColumnInfo("ir_version", DataKind.Int64, nullable: false),
        new ColumnInfo("required_ops", DataKind.String, nullable: false) { IsArray = true },
        new ColumnInfo("file_size_bytes", DataKind.Int64, nullable: false),
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "onnx_inspect_meta";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Reads ONNX file metadata (producer, opset, ir_version, distinct op types, file size): " +
        "inference.onnx_inspect_meta(path). Path resolution mirrors CREATE MODEL USING. " +
        "Doesn't load weights — parses only the file's protobuf header, so a 7B-param file inspects in milliseconds.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: OutputSchema),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires exactly one argument: onnx_inspect_meta(path STRING).");
        }
        if (argumentKinds[0] != DataKind.String)
        {
            throw new FunctionArgumentException(Name,
                $"argument 1 must be STRING, got {argumentKinds[0]}.");
        }
        return OutputSchema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length != 1)
        {
            throw new ArgumentException("onnx_inspect_meta() requires exactly one argument: onnx_inspect_meta(path STRING).");
        }

        string rawPath = arguments[0].AsString();
        string resolvedPath = ModelCatalog.ResolveFilePath(
            rawPath, context.Catalog.Models, $"inference.{Name}");

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"ONNX model file not found at '{resolvedPath}'.", resolvedPath);
        }

        long fileSize = new FileInfo(resolvedPath).Length;
        OnnxFileMetadata meta = OnnxFileMetadataReader.Read(resolvedPath);

        RowBatch batch = context.RentRowBatch(ColumnLookupCached);
        batch.Add(
        [
            DataValue.FromString(meta.ProducerName, context.Store),
            DataValue.FromString(meta.ProducerVersion, context.Store),
            DataValue.FromInt32(meta.OpsetVersion),
            DataValue.FromInt64(meta.IrVersion),
            BuildStringArray(meta.RequiredOps, context.Store),
            DataValue.FromInt64(fileSize),
        ]);
        yield return batch;
        await Task.CompletedTask;
    }

    private static DataValue BuildStringArray(IReadOnlyList<string> values, IValueStore store)
    {
        string[] arr = values is string[] s ? s : values.ToArray();
        return DataValue.FromStringArray(arr, store);
    }
}
