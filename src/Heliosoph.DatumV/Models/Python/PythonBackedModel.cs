using System.Text.Json.Nodes;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Models.Python;

/// <summary>
/// <see cref="IModel"/> backed by a Python subprocess. Bridges the engine's
/// async batch-inference contract to a long-running Python worker that
/// loads a model once and serves NDJSON requests over stdin/stdout.
/// </summary>
/// <remarks>
/// <para>
/// <strong>When to use.</strong> The framework exists for models whose
/// upstream tooling is Python-only and where converting the weights to
/// ONNX/GGUF is more work than the experiment is worth — Bark, XTTS-v2,
/// StyleTTS2, F5-TTS, MusicGen, and similar HuggingFace transformers
/// pipelines. Once a model proves valuable enough to justify a native
/// integration, you replace the Python-backed entry with an ONNX- or
/// LlamaSharp-backed equivalent and delete the worker script.
/// </para>
/// <para>
/// <strong>Process per model instance.</strong> Each <see cref="PythonBackedModel"/>
/// owns one Python subprocess, started lazily on the first inference call
/// and shared across subsequent calls. The residency manager's eviction
/// triggers <see cref="IDisposable.Dispose"/>, which kills the subprocess.
/// </para>
/// <para>
/// <strong>Value encoding.</strong> Inputs and outputs flow as JSON. The
/// supported kinds are the ones an experimentation pipeline actually
/// needs — String, Image / Audio / Video (byte payloads), byte arrays (UInt8 + IsArray), and
/// numerics/booleans. Encoding rules:
/// <list type="bullet">
///   <item><description>Null values map to JSON <c>null</c>.</description></item>
///   <item><description>Strings map to JSON strings.</description></item>
///   <item><description>Byte payloads (Image, byte arrays) map to <c>{"_bytes": "&lt;base64&gt;"}</c>.</description></item>
///   <item><description>Booleans map to JSON <c>true</c>/<c>false</c>.</description></item>
///   <item><description>Numerics map to JSON numbers (integer or floating-point as appropriate).</description></item>
/// </list>
/// Other kinds (Struct, Array, Vector, Matrix, etc.) throw
/// <see cref="NotSupportedException"/> at the boundary — extend the
/// encoder/decoder when a real model needs them.
/// </para>
/// </remarks>
public sealed class PythonBackedModel : IModel, IDisposable
{
    /// <summary>
    /// Default ready-handshake timeout. Bark and XTTS-v2 both load in well
    /// under a minute on warm caches; the conservative ceiling here
    /// accommodates first-run model downloads from HuggingFace.
    /// </summary>
    public static readonly TimeSpan DefaultReadyTimeout = TimeSpan.FromSeconds(120);

    private readonly string _scriptPath;
    private readonly IPythonEnvironmentManager _environments;
    private readonly string _venvName;
    private readonly string _pythonVersion;
    private readonly IReadOnlyList<string> _requirements;
    private readonly string? _modelDirectory;
    private readonly IReadOnlyList<string>? _scriptArgs;
    private readonly TimeSpan _readyTimeout;
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private PythonProcessHost? _host;
    private bool _disposed;

    /// <summary>
    /// Creates a Python-backed model. The subprocess is not spawned —
    /// and the venv is not materialised — until the first
    /// <see cref="InferBatchAsync"/> call. The venv lifecycle (Python
    /// bootstrap, dep install) is owned by the supplied
    /// <see cref="PythonEnvironmentManager"/>; this class only knows
    /// "give me the python.exe and I'll spawn the worker."
    /// </summary>
    /// <param name="name">Catalog-visible name (the <c>X</c> in <c>models.X(...)</c>).</param>
    /// <param name="inputKinds">Per-column input kinds. The model invocation operator validates argument types against this list at plan time.</param>
    /// <param name="outputKind">The single per-row output kind.</param>
    /// <param name="isDeterministic">Whether identical inputs always produce identical outputs (drives planner CSE).</param>
    /// <param name="environments">Engine-managed Python toolchain. Supplies the venv-scoped interpreter.</param>
    /// <param name="venvName">Stable venv identifier — typically the model name. Maps to a directory under <see cref="PythonEnvironmentManager.VenvsDirectory"/>.</param>
    /// <param name="pythonVersion">Python major.minor (e.g. "3.11") to base the venv on.</param>
    /// <param name="requirements">PEP 508 requirement strings the venv must have installed before the worker can start.</param>
    /// <param name="scriptPath">Absolute path to the Python worker script.</param>
    /// <param name="scriptArgs">Extra CLI args appended after the script path.</param>
    /// <param name="readyTimeout">How long to wait for the worker to print the ready handshake; defaults to <see cref="DefaultReadyTimeout"/>.</param>
    /// <param name="preferredBatchSize">See <see cref="IModel.PreferredBatchSize"/>.</param>
    /// <param name="modelDirectory">
    /// Absolute path to the per-model directory under
    /// <c>DATUMV_MODELS</c> that <see cref="Heliosoph.DatumV.ModelLibrary.ModelDownloadService"/>
    /// populated with weight files. Forwarded to the worker as the
    /// <c>DATUMV_MODELS</c> environment variable so it can load
    /// weights via <c>from_pretrained(os.environ['DATUMV_MODELS'])</c>
    /// — keeping the model's on-disk footprint under the user-
    /// configured models directory rather than HuggingFace's default
    /// <c>~/.cache/huggingface/</c>. Optional: pass
    /// <see langword="null"/> for workers that don't load HF-shaped
    /// model directories (e.g. workers that take an explicit
    /// <c>--model-path</c> CLI arg).
    /// </param>
    public PythonBackedModel(
        string name,
        IReadOnlyList<DataKind> inputKinds,
        DataKind outputKind,
        bool isDeterministic,
        IPythonEnvironmentManager environments,
        string venvName,
        string pythonVersion,
        IReadOnlyList<string> requirements,
        string scriptPath,
        IReadOnlyList<string>? scriptArgs = null,
        TimeSpan? readyTimeout = null,
        int? preferredBatchSize = null,
        string? modelDirectory = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(scriptPath);
        ArgumentException.ThrowIfNullOrEmpty(venvName);
        ArgumentException.ThrowIfNullOrEmpty(pythonVersion);
        ArgumentNullException.ThrowIfNull(environments);
        ArgumentNullException.ThrowIfNull(requirements);

        Name = name;
        InputKinds = inputKinds;
        OutputKind = outputKind;
        IsDeterministic = isDeterministic;
        _environments = environments;
        _venvName = venvName;
        _pythonVersion = pythonVersion;
        _requirements = requirements;
        _scriptPath = scriptPath;
        _scriptArgs = scriptArgs;
        _readyTimeout = readyTimeout ?? DefaultReadyTimeout;
        _modelDirectory = modelDirectory;
        PreferredBatchSize = preferredBatchSize;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public bool IsDeterministic { get; }

    /// <inheritdoc />
    public IReadOnlyList<DataKind> InputKinds { get; }

    /// <inheritdoc />
    public DataKind OutputKind { get; }

    /// <inheritdoc />
    public int? PreferredBatchSize { get; }

    /// <inheritdoc />
    public async Task<IReadOnlyList<ValueRef>> InferBatchAsync(
        IReadOnlyList<IReadOnlyList<ValueRef>> inputs,
        IReadOnlyList<IReadOnlyList<ValueRef>> overrides,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        PythonProcessHost host = await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        JsonObject request = new()
        {
            ["inputs"] = EncodeBatch(inputs),
            ["overrides"] = EncodeBatch(overrides),
        };

        JsonObject response = await host.CallAsync(request, cancellationToken).ConfigureAwait(false);
        return DecodeOutputs(response, inputs.Count);
    }

    private async Task<PythonProcessHost> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_host is not null) return _host;

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_host is not null) return _host;

            // Materialise the venv if needed. The manager fast-paths
            // when the venv already exists with matching requirements;
            // first-ever-use downloads uv, installs Python, and
            // pip-installs the dep set (with progress streaming to the
            // wired IPythonEnvironmentReporter). Returns the absolute
            // path to the venv-scoped python.
            string pythonExecutable = await _environments
                .EnsureVenvAsync(_venvName, _pythonVersion, _requirements, cancellationToken)
                .ConfigureAwait(false);

            _host = await PythonProcessHost.StartAsync(
                pythonExecutable, _scriptPath, _scriptArgs, _readyTimeout, cancellationToken,
                extraPythonPath: ResolveBundledPythonPath(),
                modelDirectory: _modelDirectory)
                .ConfigureAwait(false);
            return _host;
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Returns the engine's bundled Python script directory
    /// (<c>{AppContext.BaseDirectory}/python</c>) wrapped as a single-entry
    /// PYTHONPATH list, or <see langword="null"/> if the directory doesn't
    /// exist (e.g. running from a stale build with the Content glob
    /// disabled). User-written worker scripts can then
    /// <c>from python_worker_host import run</c> regardless of where they
    /// live on disk.
    /// </summary>
    private static IReadOnlyList<string>? ResolveBundledPythonPath()
    {
        string engineScriptDir = Path.Combine(AppContext.BaseDirectory, "python");
        return Directory.Exists(engineScriptDir) ? [engineScriptDir] : null;
    }

    private static JsonArray EncodeBatch(IReadOnlyList<IReadOnlyList<ValueRef>> batch)
    {
        // Build via array constructors rather than .Add() — JsonArray.Add<T>(T)
        // and JsonValue.Create<T>(T) are both flagged RequiresUnreferencedCode
        // and would warn under IsTrimmable. The array-constructor path uses
        // only the trim-safe non-generic overloads.
        JsonNode?[] rowsArr = new JsonNode?[batch.Count];
        for (int r = 0; r < batch.Count; r++)
        {
            IReadOnlyList<ValueRef> row = batch[r];
            JsonNode?[] colsArr = new JsonNode?[row.Count];
            for (int c = 0; c < row.Count; c++)
            {
                colsArr[c] = EncodeValueRef(row[c]);
            }
            rowsArr[r] = new JsonArray(colsArr);
        }
        return new JsonArray(rowsArr);
    }

    private static JsonNode? EncodeValueRef(ValueRef value)
    {
        if (value.IsNull) return null;

        DataKind kind = value.Kind;

        // Byte payloads — encode as {"_bytes": "<base64>"}. Image/Audio/Video
        // and (UInt8 + IsArray) all share the byte-content shape; the kind
        // tag distinguishes them downstream.
        if (kind is DataKind.Image or DataKind.Audio or DataKind.Video
            || (kind == DataKind.UInt8 && value.IsArray))
        {
            byte[] bytes = value.AsBytes();
            return new JsonObject
            {
                ["_bytes"] = Convert.ToBase64String(bytes),
            };
        }

        // Strings.
        if (kind == DataKind.String)
        {
            return JsonValue.Create(value.AsString());
        }

        // Booleans.
        if (kind == DataKind.Boolean)
        {
            return JsonValue.Create(value.AsBoolean());
        }

        // Numerics — coerce to the largest sensible JSON type. JSON numbers
        // are double-typed, but System.Text.Json preserves long/decimal when
        // we hand them in as the right CLR type.
        return kind switch
        {
            DataKind.UInt8 => JsonValue.Create(value.AsUInt8()),
            DataKind.Int8 => JsonValue.Create(value.AsInt8()),
            DataKind.Int16 => JsonValue.Create(value.AsInt16()),
            DataKind.UInt16 => JsonValue.Create(value.AsUInt16()),
            DataKind.Int32 => JsonValue.Create(value.AsInt32()),
            DataKind.UInt32 => JsonValue.Create(value.AsUInt32()),
            DataKind.Int64 => JsonValue.Create(value.AsInt64()),
            DataKind.UInt64 => JsonValue.Create(value.AsUInt64()),
            DataKind.Float32 => JsonValue.Create(value.AsFloat32()),
            DataKind.Float64 => JsonValue.Create(value.AsFloat64()),
            _ => throw new NotSupportedException(
                $"PythonBackedModel cannot encode DataKind.{kind} (IsArray={value.IsArray}). "
                + "Supported kinds: String, Image / Audio / Video (byte payloads), byte arrays (UInt8 + IsArray), Boolean,"
                + "and integer/floating-point primitives."),
        };
    }

    private IReadOnlyList<ValueRef> DecodeOutputs(JsonObject response, int expectedCount)
    {
        if (response["outputs"] is not JsonArray outputs)
        {
            throw new PythonProcessException(
                $"Python worker '{Name}' response missing 'outputs' array. Body: {response.ToJsonString()}");
        }

        if (outputs.Count != expectedCount)
        {
            throw new PythonProcessException(
                $"Python worker '{Name}' returned {outputs.Count} outputs for {expectedCount} input rows.");
        }

        ValueRef[] result = new ValueRef[expectedCount];
        for (int i = 0; i < expectedCount; i++)
        {
            result[i] = DecodeValueRef(outputs[i], OutputKind, i);
        }
        return result;
    }

    private ValueRef DecodeValueRef(JsonNode? node, DataKind expectedKind, int rowIndex)
    {
        if (node is null) return ValueRef.Null(expectedKind);

        // Byte payloads come back as {"_bytes": "<base64>"}.
        if (node is JsonObject obj && obj["_bytes"] is JsonNode b64)
        {
            byte[] bytes = Convert.FromBase64String(b64.GetValue<string>());
            if (expectedKind is DataKind.Image or DataKind.Audio or DataKind.Video)
            {
                return ValueRef.FromBytes(expectedKind, bytes);
            }
            if (expectedKind == DataKind.UInt8)
            {
                return ValueRef.FromBytes(DataKind.UInt8, bytes, isArray: true);
            }
            throw new PythonProcessException(
                $"Python worker '{Name}' returned bytes for row {rowIndex} but OutputKind is {expectedKind}. " +
                "Set OutputKind to Image / Audio / Video, or UInt8 (with IsArray=true), for byte-returning models.");
        }

        // Scalar paths — pick the right factory by the catalog-declared kind.
        // We use the worker-supplied JSON value's CLR shape (string, bool,
        // number) to coerce, so the worker can hand us "42" or 42 and the
        // catalog kind disambiguates.
        switch (expectedKind)
        {
            case DataKind.String:
                return ValueRef.FromString(node.GetValue<string>());
            case DataKind.Boolean:
                return ValueRef.FromBoolean(node.GetValue<bool>());
            case DataKind.UInt8: return ValueRef.FromUInt8(node.GetValue<byte>());
            case DataKind.Int8: return ValueRef.FromInt8(node.GetValue<sbyte>());
            case DataKind.Int16: return ValueRef.FromInt16(node.GetValue<short>());
            case DataKind.UInt16: return ValueRef.FromUInt16(node.GetValue<ushort>());
            case DataKind.Int32: return ValueRef.FromInt32(node.GetValue<int>());
            case DataKind.UInt32: return ValueRef.FromUInt32(node.GetValue<uint>());
            case DataKind.Int64: return ValueRef.FromInt64(node.GetValue<long>());
            case DataKind.UInt64: return ValueRef.FromUInt64(node.GetValue<ulong>());
            case DataKind.Float32: return ValueRef.FromFloat32(node.GetValue<float>());
            case DataKind.Float64: return ValueRef.FromFloat64(node.GetValue<double>());
            default:
                throw new NotSupportedException(
                    $"PythonBackedModel '{Name}' cannot decode DataKind.{expectedKind} outputs. " +
                    "Supported kinds: String, Image / Audio / Video (byte payloads), byte arrays (UInt8 + IsArray), Boolean," +
                    "and integer/floating-point primitives.");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _host?.Dispose();
        _host = null;
        _startLock.Dispose();
    }
}
