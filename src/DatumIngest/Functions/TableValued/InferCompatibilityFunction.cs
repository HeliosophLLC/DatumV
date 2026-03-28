using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>inference.infer_compatibility(path) → table</c>. One row per
/// registered backend, telling the user whether that backend can load the
/// ONNX file at <c>path</c> based on the file's declared opset vs the
/// backend's supported ceiling. Doesn't actually load — reads only the
/// file header.
/// </summary>
/// <remarks>
/// <para>
/// <strong>v1 scope.</strong> The check is opset-vs-ceiling only. Per-op
/// support checking (which would catch "graph uses an op the backend
/// doesn't implement") is a follow-up — ORT doesn't expose its supported-
/// op set through the C# bindings, so that requires either an offline
/// table or an actual load attempt.
/// </para>
/// </remarks>
public sealed class InferCompatibilityFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ColumnLookupCached = new(
        ["backend", "supported", "opset_required", "opset_supported", "notes"]);

    private static readonly Schema OutputSchema = new(
    [
        new ColumnInfo("backend", DataKind.String, nullable: false),
        new ColumnInfo("supported", DataKind.Boolean, nullable: false),
        new ColumnInfo("opset_required", DataKind.Int32, nullable: false),
        new ColumnInfo("opset_supported", DataKind.Int32, nullable: false),
        new ColumnInfo("notes", DataKind.String, nullable: false),
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "infer_compatibility";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Reports whether each registered inference backend can load an ONNX file based on its declared opset: " +
        "inference.infer_compatibility(path). One row per backend. " +
        "Compares the file's ai.onnx opset against the backend's supported ceiling; " +
        "per-op compatibility checking is a follow-up.";

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
                "requires exactly one argument: infer_compatibility(path STRING).");
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
            throw new ArgumentException("infer_compatibility() requires exactly one argument: infer_compatibility(path STRING).");
        }

        IInferenceDispatcher? dispatcher = context.Catalog.InferenceDispatcher;
        if (dispatcher is null)
        {
            throw new InvalidOperationException(
                $"inference.{Name}: no InferenceDispatcher is configured on this host. " +
                "Wire TableCatalog.InferenceDispatcher before invoking.");
        }

        string rawPath = arguments[0].AsString();
        string resolvedPath = ModelCatalog.ResolveFilePath(
            rawPath, context.Catalog.Models, $"inference.{Name}");

        if (!File.Exists(resolvedPath))
        {
            throw new FileNotFoundException(
                $"ONNX model file not found at '{resolvedPath}'.", resolvedPath);
        }

        OnnxFileMetadata meta = OnnxFileMetadataReader.Read(resolvedPath);
        int fileOpset = meta.OpsetVersion;

        RowBatch batch = context.RentRowBatch(ColumnLookupCached);
        foreach (IInferenceBackend backend in dispatcher.Backends)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            int supported = backend.MaxSupportedOpset;
            bool ok = fileOpset >= 0 && fileOpset <= supported;
            string notes = fileOpset < 0
                ? "ONNX file does not declare an ai.onnx opset."
                : ok
                    ? ""
                    : $"File opset {fileOpset} exceeds backend ceiling {supported}. Re-export with an older opset or wait for a backend bump.";

            batch.Add(
            [
                DataValue.FromString(backend.Id.ToString(), context.Store),
                DataValue.FromBoolean(ok),
                DataValue.FromInt32(fileOpset),
                DataValue.FromInt32(supported),
                DataValue.FromString(notes, context.Store),
            ]);
            if (batch.IsFull)
            {
                yield return batch;
                batch = context.RentRowBatch(ColumnLookupCached);
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
        else
        {
            context.Pool.ReturnRowBatch(batch);
        }

        await Task.CompletedTask;
    }
}
