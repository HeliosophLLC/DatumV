using System.Text.Json;

using DatumIngest.Model;
using DatumIngest.Models.Llama;
using DatumIngest.Models.Onnx;

namespace DatumIngest.Models;

/// <summary>
/// Helpers that register pre-known model entries with a <see cref="ModelCatalog"/>.
/// Until the SQL surface gains <c>CREATE MODEL</c>, callers stitch their catalog
/// together by calling these methods at startup. The same registration shape
/// will be reused by the eventual statement handler — this is just the pre-DDL
/// equivalent.
/// </summary>
public static class BuiltinModels
{
    /// <summary>
    /// Default filename for the MobileNetV2 ONNX file (the canonical
    /// <c>mobilenetv2-12.onnx</c> from the ONNX model zoo).
    /// </summary>
    public const string MobileNetV2DefaultFilename = "mobilenetv2-12.onnx";

    /// <summary>
    /// Default filename for the ImageNet-1k label vocabulary, looked up next to
    /// the ONNX file. Format is a JSON array of 1000 strings —
    /// <a href="https://raw.githubusercontent.com/anishathalye/imagenet-simple-labels/master/imagenet-simple-labels.json">imagenet-simple-labels.json</a>
    /// is the recommended drop-in.
    /// </summary>
    public const string ImageNetLabelsDefaultFilename = "imagenet-classes.json";

    /// <summary>
    /// Registers MobileNetV2 under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"classify"</c>). The ONNX file is resolved as
    /// <c>{ModelDirectory}/{modelFilename}</c>; ImageNet labels load from
    /// <c>{ModelDirectory}/{labelsFilename}</c> if present, otherwise predictions
    /// fall back to <c>class_&lt;index&gt;</c>.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(image)</c>). Defaults to
    /// <c>"classify"</c>.
    /// </param>
    /// <param name="modelFilename">
    /// ONNX filename relative to the catalog's <see cref="ModelCatalog.ModelDirectory"/>.
    /// Defaults to <see cref="MobileNetV2DefaultFilename"/>.
    /// </param>
    /// <param name="labelsFilename">
    /// JSON labels filename relative to the catalog's <see cref="ModelCatalog.ModelDirectory"/>.
    /// Defaults to <see cref="ImageNetLabelsDefaultFilename"/>. Pass
    /// <see langword="null"/> to skip labels entirely.
    /// </param>
    public static void RegisterMobileNetV2(
        ModelCatalog catalog,
        string modelName = "classify",
        string modelFilename = MobileNetV2DefaultFilename,
        string? labelsFilename = ImageNetLabelsDefaultFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                IReadOnlyList<string>? labels = labelsFilename is null
                    ? null
                    : TryLoadLabels(Path.Combine(ctx.ModelDirectory, labelsFilename));
                return new MobileNetV2Model(modelName, modelPath, labels);
            }));
    }

    /// <summary>
    /// Default filename for the Llama 3.1 8B Instruct GGUF (Q4_K_M
    /// imatrix-quantized variant from bartowski's HuggingFace repo).
    /// </summary>
    public const string Llama31_8BDefaultFilename = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Llama 3.1 8B Instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"llm"</c>). The GGUF file is
    /// resolved as <c>{ModelDirectory}/{modelFilename}</c> and loaded via
    /// LlamaSharp; all layers offload to GPU when a supported backend is
    /// installed (CUDA 12 by default in this build).
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(prompt)</c>). Defaults to
    /// <c>"llm"</c>.
    /// </param>
    /// <param name="modelFilename">
    /// GGUF filename relative to the catalog's <see cref="ModelCatalog.ModelDirectory"/>.
    /// Defaults to <see cref="Llama31_8BDefaultFilename"/>.
    /// </param>
    /// <param name="contextSize">
    /// Token context window the model is loaded with. Defaults to 4096.
    /// </param>
    /// <param name="maxTokens">
    /// Maximum new tokens to generate per call. Defaults to 256.
    /// </param>
    /// <param name="temperature">
    /// Sampling temperature. Defaults to 0.7.
    /// </param>
    public static void RegisterLlama31(
        ModelCatalog catalog,
        string modelName = "llm",
        string modelFilename = Llama31_8BDefaultFilename,
        uint contextSize = 4096,
        int maxTokens = 256,
        float temperature = 0.7f)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "llama",
            RelativePath: modelFilename,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Llama31, contextSize, maxTokens, temperature);
            },
            // Trailing optional positional args:
            //   [0] temperature (Float64)  — sampling temperature override
            //   [1] max_tokens  (Int32)    — max new tokens to generate
            // Order matters: callers supply a prefix. `models.llm(prompt, 0.9)`
            // overrides temperature only; `models.llm(prompt, 0.9, 64)` overrides
            // both. Adding a third (e.g. seed) tomorrow is a non-breaking append.
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32]));
    }

    /// <summary>
    /// Default filename for the Phi-3-mini-4k Instruct GGUF (Q4_K_M
    /// imatrix-quantized variant from bartowski's HuggingFace repo).
    /// </summary>
    public const string Phi3MiniDefaultFilename = "Phi-3-mini-4k-instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Microsoft Phi-3-mini-4k-instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"phi3"</c>). Same backend
    /// (LlamaSharp) as Llama 3.1, just with the Phi-3 chat template — making
    /// it a much smaller (~2.4 GB on disk, ~3 GB cold VRAM) drop-in for
    /// budget-tight setups or for verifying the engine handles multiple
    /// concurrent LLMs.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(prompt)</c>). Defaults to
    /// <c>"phi3"</c>.
    /// </param>
    /// <param name="modelFilename">
    /// GGUF filename relative to the catalog's <see cref="ModelCatalog.ModelDirectory"/>.
    /// Defaults to <see cref="Phi3MiniDefaultFilename"/>.
    /// </param>
    /// <param name="contextSize">Token context window. Defaults to 4096 (matches the model's training context).</param>
    /// <param name="maxTokens">Maximum new tokens per call. Defaults to 256.</param>
    /// <param name="temperature">Sampling temperature. Defaults to 0.7.</param>
    public static void RegisterPhi3(
        ModelCatalog catalog,
        string modelName = "phi3",
        string modelFilename = Phi3MiniDefaultFilename,
        uint contextSize = 4096,
        int maxTokens = 256,
        float temperature = 0.7f)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "llama",
            RelativePath: modelFilename,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.String,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Phi3, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32]));
    }

    /// <summary>
    /// Loads a JSON-array label file. Returns <see langword="null"/> when the
    /// file is missing — letting the model fall back to <c>class_&lt;index&gt;</c>
    /// instead of failing the load. Throws when the file exists but is malformed:
    /// silent fallback there would hide a genuine configuration error.
    /// </summary>
    private static IReadOnlyList<string>? TryLoadLabels(string path)
    {
        if (!File.Exists(path)) return null;

        // Manual walk over JsonDocument keeps this trim-safe (the project is
        // IsTrimmable=true, so the reflection-based JsonSerializer.Deserialize<T>
        // would warn). The schema is intentionally narrow: a JSON array of strings.
        using FileStream stream = File.OpenRead(path);
        using JsonDocument doc = JsonDocument.Parse(stream);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Labels file '{path}' must be a JSON array of strings (e.g. [\"tench\", \"goldfish\", ...]).");
        }

        List<string> labels = new(doc.RootElement.GetArrayLength());
        foreach (JsonElement element in doc.RootElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Labels file '{path}' contains a non-string element at index {labels.Count}.");
            }
            labels.Add(element.GetString() ?? string.Empty);
        }
        return labels;
    }
}
