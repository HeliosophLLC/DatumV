using DatumIngest.Inference;
using DatumIngest.Manifest;
using DatumIngest.Model;
using ExecutionContext = DatumIngest.Execution.ExecutionContext;

namespace DatumIngest.Functions.TableValued;

/// <summary>
/// <c>inference.devices() → table</c>. Walks the registered inference
/// backends and emits one row per <c>(backend, device)</c> the backend
/// recognises — including devices unreachable on this machine, with a
/// human-readable <c>reason</c>. Powers the editor's "what hardware can I
/// target?" affordance and answers <c>WHERE available = true</c> for
/// scripted dispatch decisions.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Output columns.</strong>
/// <c>backend</c> is the backend's <see cref="InferenceBackendId"/> name
/// (e.g. <c>"OnnxRuntime"</c>). <c>device</c> is the device enum name with
/// the backend prefix stripped (<c>"Cpu"</c> rather than <c>"OnnxRuntimeCpu"</c>)
/// — the backend column already carries that distinction.
/// <c>available</c> reflects whether the backend's probe attached the EP
/// successfully; <c>reason</c> carries the failure message when not.
/// <c>estimated_vram_mb</c> is <c>0</c> in v1 — a follow-up will wire real
/// CUDA / DirectML memory queries when the surface is needed.
/// </para>
/// <para>
/// <strong>No ModelCatalog dependency.</strong> Unlike
/// <c>inference.onnx_inspect</c>, this function only reads dispatcher
/// metadata, so it works on a catalog without <c>TableCatalog.Models</c>
/// wired.
/// </para>
/// </remarks>
public sealed class DevicesFunction : ITableValuedFunctionMetadata, ITableValuedFunction
{
    private static readonly ColumnLookup ColumnLookupCached = new(
        ["backend", "device", "available", "reason", "estimated_vram_mb"]);

    private static readonly Schema OutputSchema = new(
    [
        new ColumnInfo("backend", DataKind.String, nullable: false),
        new ColumnInfo("device", DataKind.String, nullable: false),
        new ColumnInfo("available", DataKind.Boolean, nullable: false),
        new ColumnInfo("reason", DataKind.String, nullable: false),
        new ColumnInfo("estimated_vram_mb", DataKind.Int32, nullable: false),
    ]);

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Name => "devices";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static FunctionCategory Category => FunctionCategory.Table;

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static string Description =>
        "Lists every (backend, device) pair the inference layer recognises: " +
        "inference.devices(). Columns: (backend, device, available, reason, estimated_vram_mb). " +
        "Unreachable devices carry a reason (platform mismatch, missing driver, EP not built). " +
        "Filter `WHERE available = true` for runnable targets.";

    /// <inheritdoc cref="ITableValuedFunctionMetadata"/>
    public static IReadOnlyList<TableValuedFunctionSignatureVariant> Signatures { get; } =
    [
        new TableValuedFunctionSignatureVariant(
            Parameters: [],
            FixedOutputSchema: OutputSchema),
    ];

    string ITableValuedFunction.Name => Name;

    /// <inheritdoc />
    public Schema ValidateArguments(
        ReadOnlySpan<DataKind> argumentKinds,
        ReadOnlySpan<DataValue?> constantArguments,
        CancellationToken cancellationToken)
    {
        if (argumentKinds.Length != 0)
        {
            throw new FunctionArgumentException(Name,
                "takes no arguments: inference.devices().");
        }
        return OutputSchema;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RowBatch> ExecuteAsync(ValueRef[] arguments, ExecutionContext context)
    {
        if (arguments.Length != 0)
        {
            throw new ArgumentException("inference.devices() takes no arguments.");
        }

        IInferenceDispatcher? dispatcher = context.Catalog.InferenceDispatcher;
        if (dispatcher is null)
        {
            throw new InvalidOperationException(
                "inference.devices(): no InferenceDispatcher is configured on this host. " +
                "Wire TableCatalog.InferenceDispatcher before invoking.");
        }

        RowBatch batch = context.RentRowBatch(ColumnLookupCached);
        try
        {
            foreach (IInferenceBackend backend in dispatcher.Backends)
            {
                string backendName = backend.Id.ToString();
                foreach (DeviceProbeResult probe in backend.ProbeAllDevices())
                {
                    context.CancellationToken.ThrowIfCancellationRequested();

                    string deviceName = StripBackendPrefix(probe.Device.ToString(), backendName);
                    batch.Add(
                    [
                        DataValue.FromString(backendName, context.Store),
                        DataValue.FromString(deviceName, context.Store),
                        DataValue.FromBoolean(probe.Available),
                        DataValue.FromString(probe.Reason, context.Store),
                        DataValue.FromInt32(0),
                    ]);
                    if (batch.IsFull)
                    {
                        yield return batch;
                        batch = context.RentRowBatch(ColumnLookupCached);
                    }
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
            if (batch is not null && batch.Count == 0)
            {
                context.Pool.ReturnRowBatch(batch);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Removes the backend prefix from a device enum name so the SQL output
    /// reads "Cpu" rather than "OnnxRuntimeCpu" when the backend column
    /// already carries that distinction. Returns the original string when
    /// no prefix match (defensive: new device kinds added without prefix
    /// naming still display, just unstripped).
    /// </summary>
    private static string StripBackendPrefix(string deviceName, string backendName)
        => deviceName.StartsWith(backendName, StringComparison.Ordinal)
            ? deviceName[backendName.Length..]
            : deviceName;
}
