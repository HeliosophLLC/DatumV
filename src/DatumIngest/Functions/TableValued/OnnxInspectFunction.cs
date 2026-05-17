using DatumIngest.Inference;
using DatumIngest.Inference.OnnxRuntime;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Models;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>inference.onnx_inspect(path) → table</c>. Opens an ONNX model and emits
/// one row per input + output tensor with columns
/// <c>(kind, name, dtype, shape, is_dynamic)</c> — the introspection surface
/// users need when authoring a <c>CREATE MODEL</c> body.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Path resolution.</strong> Mirrors <c>CREATE MODEL USING</c>: a
/// <c>file://</c> prefix marks an absolute path anywhere on disk; otherwise
/// the path is treated as relative to
/// <see cref="ModelCatalog.ModelDirectory"/> (so the same string a user types
/// in <c>CREATE MODEL ... USING 'foo.onnx'</c> works here too).
/// </para>
/// <para>
/// <strong>Dynamic dimensions.</strong> ONNX represents dynamic axes (batch,
/// sequence length, image resolution) as named symbols in the graph. This
/// function flattens them to <c>-1</c> in the <c>shape INT[]</c> column —
/// matching the ONNX wire convention so <c>WHERE shape[1] = -1</c> finds
/// dynamic batch dims. The boolean <c>is_dynamic</c> column is <c>true</c>
/// when any dim in the tensor is dynamic.
/// </para>
/// <para>
/// <strong>Load cost.</strong> ORT has no metadata-only path in C#; even
/// inspection requires opening the full session. The function pins device to
/// CPU so a 7B-parameter ONNX file doesn't briefly camp on VRAM just because
/// a user wanted to peek at its signature.
/// </para>
/// </remarks>
public sealed class OnnxInspectFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ColumnLookupCached = new(
        ["kind", "name", "dtype", "shape", "is_dynamic"]);

    private static readonly Schema OutputSchema = new(
    [
        new ColumnInfo("kind", DataKind.String, nullable: false),
        new ColumnInfo("name", DataKind.String, nullable: false),
        new ColumnInfo("dtype", DataKind.String, nullable: false),
        new ColumnInfo("shape", DataKind.Int32, nullable: false) { IsArray = true },
        new ColumnInfo("is_dynamic", DataKind.Boolean, nullable: false),
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "onnx_inspect";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Introspects an ONNX model file and returns one row per input + output tensor: " +
        "onnx_inspect(path). Columns: (kind, name, dtype, shape, is_dynamic). " +
        "Path resolution mirrors CREATE MODEL USING — 'file://' absolute or relative to " +
        "ModelCatalog.ModelDirectory. Dynamic dims surface as -1 in the shape array.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [new ParameterSpec("path", DataKindMatcher.Exact(DataKind.String))],
            FixedOutputSchema: OutputSchema),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 1)
        {
            throw new FunctionArgumentException(Name,
                "requires exactly one argument: onnx_inspect(path STRING).");
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
            throw new ArgumentException("onnx_inspect() requires exactly one argument: onnx_inspect(path STRING).");
        }

        string rawPath = arguments[0].AsString();
        string resolvedPath = ModelCatalog.ResolveFilePath(
            rawPath, context.Catalog.Models, $"inference.{Name}");

        // CPU device explicitly — introspecting a big model shouldn't briefly
        // claim VRAM. The user is asking "what shape is this?", not "run it."
        OnnxRuntimeBackend backend = new();
        InferenceLoadRequest request = new(
            ModelFilePath: resolvedPath,
            SessionName: "inspect",
            Device: InferenceDevice.OnnxRuntimeCpu,
            Optimization: InferenceOptimization.None);

        using IInferenceSession session = (IInferenceSession)await backend.LoadAsync(request, context.CancellationToken);

        RowBatch batch = context.RentRowBatch(ColumnLookupCached);
        try
        {
            foreach (TensorSpec input in session.Inputs)
            {
                AddTensorRow(batch, context, kind: "input", input);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = context.RentRowBatch(ColumnLookupCached);
                }
            }
            foreach (TensorSpec output in session.Outputs)
            {
                AddTensorRow(batch, context, kind: "output", output);
                if (batch.IsFull)
                {
                    yield return batch;
                    batch = context.RentRowBatch(ColumnLookupCached);
                }
            }

            if (batch.Count > 0)
            {
                yield return batch;
                batch = null!;
            }
        }
        finally
        {
            // Defensive: if the iterator was abandoned mid-emit, return the
            // half-filled batch to the pool rather than leaking it.
            if (batch is not null && batch.Count == 0)
            {
                context.Pool.ReturnRowBatch(batch);
            }
        }
    }

    private static void AddTensorRow(
        RowBatch batch, ExecutionContext context, string kind, TensorSpec spec)
    {
        bool isDynamic = false;
        int[] shape = new int[spec.Shape.Count];
        for (int i = 0; i < spec.Shape.Count; i++)
        {
            int? dim = spec.Shape[i];
            if (dim is null)
            {
                isDynamic = true;
                shape[i] = -1;
            }
            else
            {
                shape[i] = dim.Value;
            }
        }

        batch.Add(
        [
            DataValue.FromString(kind, context.Store),
            DataValue.FromString(spec.Name, context.Store),
            DataValue.FromString(spec.ElementKind.ToString(), context.Store),
            DataValue.FromArenaArray<int>(shape, DataKind.Int32, context.Store),
            DataValue.FromBoolean(isDynamic),
        ]);
    }
}
