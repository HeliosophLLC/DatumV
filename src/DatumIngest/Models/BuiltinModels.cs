using System.Text.Json;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
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
    /// One-call setup for the standard model surface on a
    /// <see cref="TableCatalog"/>. Builds a fresh <see cref="ModelCatalog"/>
    /// rooted at <paramref name="modelDirectory"/> (or the default —
    /// <c>DATUM_MODELS</c> env var, then per-user fallback), registers every
    /// builtin model (<c>mobilenetv2</c>, <c>yolov8n</c>, <c>llama31_8b</c>,
    /// <c>phi3_mini</c>), wires the model catalog onto
    /// <see cref="TableCatalog.Models"/>, and adds the <c>system_models</c>
    /// virtual table for runtime introspection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this from any callsite that wants the canonical model-enabled
    /// configuration: shell startup, batch tools, programmatic API consumers,
    /// integration tests. Returns the constructed <see cref="ModelCatalog"/>
    /// so callers that need finer control (custom VRAM budget, additional
    /// non-builtin entries) can keep configuring after the standard set
    /// lands.
    /// </para>
    /// <para>
    /// Order of registrations: builtins first, then <c>system_models</c>.
    /// The user's data tables can be registered before or after this call —
    /// the virtual <c>system_models</c> table doesn't conflict because it
    /// uses a reserved name. Callers that gate on "user provided at least
    /// one data table" should track datum-file count explicitly rather than
    /// relying on <see cref="TableCatalog.Count"/>, since <c>system_models</c>
    /// always contributes one entry.
    /// </para>
    /// </remarks>
    /// <param name="tableCatalog">The table catalog to configure. Mutated in place.</param>
    /// <param name="modelDirectory">
    /// Override for the model files directory. <see langword="null"/> uses
    /// <see cref="ModelCatalog.DefaultModelDirectory"/> (env-var aware).
    /// </param>
    /// <returns>The freshly constructed <see cref="ModelCatalog"/>.</returns>
    public static ModelCatalog AttachStandardModels(
        TableCatalog tableCatalog,
        string? modelDirectory = null)
    {
        ModelCatalog modelCatalog = new(modelDirectory);

        // Vision models
        RegisterMobileNetV2(modelCatalog);
        RegisterYolo(modelCatalog);
        RegisterAllYoloX(modelCatalog);  // 7 entries: nano/tiny/s/m/l/x/darknet
        RegisterViTGpt2Caption(modelCatalog);

        // Captioner zoo — Florence-2 in three caption styles plus a
        // quantized comparison entry. Same model, different task tokens.
        RegisterFlorence2Caption(modelCatalog);
        RegisterFlorence2DetailedCaption(modelCatalog);
        RegisterFlorence2MoreDetailedCaption(modelCatalog);
        RegisterFlorence2CaptionQuantized(modelCatalog);

        // Image generation. SD-Turbo is the closing leg of the
        // image-in → caption → LLM-narrative → image-out pipeline.
        // SDXL-Turbo is the higher-quality sibling for hero outputs.
        RegisterSdTurbo(modelCatalog);
        RegisterSdxlTurbo(modelCatalog);

        // LLM zoo — seven entries spanning Meta, Microsoft, TinyLlama community,
        // Google, Alibaba, IBM, TII. Every voice in the zoo is at Q4_K_M
        // quantization for clean cross-model comparison (architectural
        // differences, not quantization noise). Total disk: ~12 GB if all
        // present; missing files surface in `system_models` as `status =
        // 'missing'` with re-download hints.
        RegisterLlama31(modelCatalog);
        RegisterPhi3(modelCatalog);
        RegisterTinyLlama(modelCatalog);
        RegisterGemma22b(modelCatalog);
        RegisterQwen25Coder(modelCatalog);
        RegisterGranite31(modelCatalog);
        RegisterFalcon31b(modelCatalog);

        tableCatalog.Models = modelCatalog;
        tableCatalog.Add(new ModelsTableProvider(tableCatalog.Pool, modelCatalog));
        return modelCatalog;
    }

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
    /// (defaults to <c>"mobilenetv2"</c>). The ONNX file is resolved as
    /// <c>{ModelDirectory}/{modelFilename}</c>; ImageNet labels load from
    /// <c>{ModelDirectory}/{labelsFilename}</c> if present, otherwise predictions
    /// fall back to <c>class_&lt;index&gt;</c>.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(image)</c>). Defaults to
    /// <c>"mobilenetv2"</c> — the architecture name. The capability-level
    /// <c>tasks.classify</c> namespace routes here (and to other classifiers)
    /// once the task layer lands.
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
        string modelName = "mobilenetv2",
        string modelFilename = MobileNetV2DefaultFilename,
        string? labelsFilename = ImageNetLabelsDefaultFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            // Struct{label: String, score: Float32}. The catalog entry's
            // OutputKind currently captures only the top-level kind; field
            // names land alongside the schema-layer struct-field plumbing.
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                IReadOnlyList<string>? labels = labelsFilename is null
                    ? null
                    : TryLoadLabels(Path.Combine(ctx.ModelDirectory, labelsFilename));
                return new MobileNetV2Model(modelName, modelPath, labels);
            },
            DisplayName: "MobileNetV2 ImageNet Classifier",
            Parameters: "3.5M",
            License: "Apache-2.0",
            LicenseHolder: "ONNX Model Zoo",
            SourceUrl: "https://github.com/onnx/models/tree/main/validated/vision/classification/mobilenet",
            Category: "classifier",
            Modalities: ["image", "text"],
            Files: labelsFilename is null
                ? [modelFilename]
                : [modelFilename, labelsFilename]));
    }

    /// <summary>
    /// Default filename for the Llama 3.1 8B Instruct GGUF (Q4_K_M
    /// imatrix-quantized variant from bartowski's HuggingFace repo).
    /// </summary>
    public const string Llama31_8BDefaultFilename = "Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Llama 3.1 8B Instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"llama31_8b"</c>). The GGUF file
    /// is resolved as <c>{ModelDirectory}/{modelFilename}</c> and loaded via
    /// LlamaSharp; all layers offload to GPU when a supported backend is
    /// installed (CUDA 12 by default in this build).
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(prompt)</c>). Defaults to
    /// <c>"llama31_8b"</c> — architecture + size, leaving room for sibling
    /// registrations like <c>llama31_70b</c>. The capability-level
    /// <c>tasks.llm</c> namespace will route here (and to cheaper LLMs)
    /// once the task layer lands.
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
        string modelName = "llama31_8b",
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
            // Order matters: callers supply a prefix. `models.llama31_8b(prompt, 0.9)`
            // overrides temperature only; `models.llama31_8b(prompt, 0.9, 64)`
            // overrides both. Adding a third (e.g. seed) tomorrow is a non-breaking append.
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "Llama 3.1 8B Instruct",
            Parameters: "8B",
            // Llama 3.1 ships under Meta's custom community license — broadly
            // permissive but with anti-misuse clauses and a >700M-MAU
            // commercial threshold. Distinct from a standard OSS license.
            License: "Llama 3.1 Community",
            LicenseHolder: "Meta",
            SourceUrl: "https://huggingface.co/bartowski/Meta-Llama-3.1-8B-Instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>
    /// Default filename for the Phi-3-mini-4k Instruct GGUF (Q4_K_M
    /// imatrix-quantized variant from bartowski's HuggingFace repo).
    /// </summary>
    public const string Phi3MiniDefaultFilename = "Phi-3-mini-4k-instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Microsoft Phi-3-mini-4k-instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"phi3_mini"</c>). Same backend
    /// (LlamaSharp) as Llama 3.1, just with the Phi-3 chat template — making
    /// it a much smaller (~2.4 GB on disk, ~3 GB cold VRAM) drop-in for
    /// budget-tight setups or for verifying the engine handles multiple
    /// concurrent LLMs.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">
    /// SQL-visible name (the <c>X</c> in <c>models.X(prompt)</c>). Defaults to
    /// <c>"phi3_mini"</c> — architecture + size, leaving room for
    /// <c>phi3_medium</c> later. The cheap end of the eventual <c>tasks.llm</c>
    /// cascade.
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
        string modelName = "phi3_mini",
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "Phi-3-mini-4k Instruct",
            Parameters: "3.8B",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/bartowski/Phi-3-mini-4k-instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for TinyLlama-1.1B-Chat-v1.0 (TheBloke's GGUF mirror).</summary>
    public const string TinyLlama11BChatDefaultFilename = "tinyllama-1.1b-chat-v1.0.Q4_K_M.gguf";

    /// <summary>
    /// Registers TinyLlama-1.1B-Chat-v1.0 under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"tinyllama_1b"</c>). The
    /// "previous-era" voice in the LLM zoo — its 2023-vintage chat tuning
    /// produces noticeably different prose to modern instruction-tuned models,
    /// useful for tonal comparison.
    /// </summary>
    public static void RegisterTinyLlama(
        ModelCatalog catalog,
        string modelName = "tinyllama_1b",
        string modelFilename = TinyLlama11BChatDefaultFilename,
        uint contextSize = 2048,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Zephyr, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "TinyLlama 1.1B Chat v1.0",
            Parameters: "1.1B",
            License: "Apache-2.0",
            LicenseHolder: "TinyLlama community",
            SourceUrl: "https://huggingface.co/TheBloke/TinyLlama-1.1B-Chat-v1.0-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Gemma-2-2b-it (bartowski's GGUF mirror).</summary>
    public const string Gemma22bItDefaultFilename = "gemma-2-2b-it-Q4_K_M.gguf";

    /// <summary>
    /// Registers Google's Gemma-2-2b-it under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"gemma2_2b"</c>). Larger
    /// than the other small zoo entries (~1.6 GB Q4_K_M) but a distinct
    /// Google-trained voice for comparison — careful, thorough, often
    /// verbose. Ships under Google's Gemma Terms of Use (custom permissive,
    /// allows commercial use; downstream redistribution must pass the
    /// Gemma terms along).
    /// </summary>
    public static void RegisterGemma22b(
        ModelCatalog catalog,
        string modelName = "gemma2_2b",
        string modelFilename = Gemma22bItDefaultFilename,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Gemma, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "Gemma 2 2B Instruct",
            Parameters: "2B",
            License: "Gemma Terms",
            LicenseHolder: "Google",
            SourceUrl: "https://huggingface.co/bartowski/gemma-2-2b-it-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Qwen2.5-Coder-1.5B-Instruct (bartowski's GGUF mirror).</summary>
    public const string Qwen25Coder15bDefaultFilename = "Qwen2.5-Coder-1.5B-Instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Alibaba's Qwen2.5-Coder-1.5B-Instruct under the catalog
    /// name <paramref name="modelName"/> (defaults to <c>"qwen25_coder_1_5b"</c>).
    /// Code-specialised variant — adds genre diversity to the zoo: prompts
    /// like "write me a quicksort" produce noticeably different output
    /// shape from the general-chat models.
    /// </summary>
    public static void RegisterQwen25Coder(
        ModelCatalog catalog,
        string modelName = "qwen25_coder_1_5b",
        string modelFilename = Qwen25Coder15bDefaultFilename,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.ChatML, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "Qwen 2.5 Coder 1.5B Instruct",
            Parameters: "1.5B",
            License: "Apache-2.0",
            LicenseHolder: "Alibaba",
            SourceUrl: "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Granite-3.1-1B-A400M-Instruct (bartowski's GGUF mirror).</summary>
    public const string Granite31_1bDefaultFilename = "granite-3.1-1b-a400m-instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers IBM's Granite-3.1-1B-A400M-Instruct under the catalog
    /// name <paramref name="modelName"/> (defaults to <c>"granite31_1b"</c>).
    /// Mixture-of-experts (1B total params, 400M active per token).
    /// IBM's instruction tuning produces a noticeably structured /
    /// "enterprise-y" voice — frequently bullet-pointed, careful with
    /// caveats. Apache-2.0 — fully unencumbered for commercial use.
    /// </summary>
    public static void RegisterGranite31(
        ModelCatalog catalog,
        string modelName = "granite31_1b",
        string modelFilename = Granite31_1bDefaultFilename,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Granite, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "IBM Granite 3.1 1B A400M Instruct",
            Parameters: "1B (400M active)",
            License: "Apache-2.0",
            LicenseHolder: "IBM",
            SourceUrl: "https://huggingface.co/bartowski/granite-3.1-1b-a400m-instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Falcon3-1B-Instruct (TII's official GGUF).</summary>
    public const string Falcon3_1bDefaultFilename = "Falcon3-1B-Instruct-q4_k_m.gguf";

    /// <summary>
    /// Registers TII's Falcon3-1B-Instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"falcon3_1b"</c>). Yet
    /// another family corner — Technology Innovation Institute (UAE)
    /// trained, distinctive vocabulary and style. Uses ChatML format
    /// (same template as Qwen). Custom <c>TII Falcon-LLM License 2.0</c>
    /// — broadly permissive with an acceptable-use policy.
    /// </summary>
    public static void RegisterFalcon31b(
        ModelCatalog catalog,
        string modelName = "falcon3_1b",
        string modelFilename = Falcon3_1bDefaultFilename,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.ChatML, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32],
            DisplayName: "Falcon3 1B Instruct",
            Parameters: "1B",
            License: "Falcon LLM License 2.0",
            LicenseHolder: "TII",
            SourceUrl: "https://huggingface.co/tiiuae/Falcon3-1B-Instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>
    /// Default folder name for the ViT-GPT2 image captioner. The folder
    /// contains <c>encoder_model.onnx</c>, <c>decoder_model.onnx</c>,
    /// <c>vocab.json</c>, <c>merges.txt</c>, and tokenizer/preprocessor
    /// configs — all produced by <c>optimum-cli export onnx</c>.
    /// </summary>
    public const string ViTGpt2CaptionDefaultFolder = "vit-gpt2-image-captioning";

    /// <summary>
    /// File-existence anchor used by the catalog to verify the captioner is
    /// installed: <c>{folder}/encoder_model.onnx</c>. Multi-file model
    /// convention — the catalog's <c>RelativePath</c> still points at a
    /// single file, the model loader derives the rest from that file's
    /// directory.
    /// </summary>
    public const string ViTGpt2CaptionEncoderRelativePath =
        ViTGpt2CaptionDefaultFolder + "/encoder_model.onnx";

    /// <summary>
    /// Registers the nlpconnect/vit-gpt2-image-captioning model under the
    /// catalog name <paramref name="modelName"/> (defaults to
    /// <c>"vit_gpt2_caption"</c>). ViT-base encoder + GPT-2 decoder, ~1 GB
    /// on disk as ONNX. Apache-2.0 — fully unencumbered for commercial use.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (the <c>X</c> in <c>models.X(image)</c>).</param>
    /// <param name="encoderRelativePath">
    /// Path to <c>encoder_model.onnx</c>, relative to the catalog's
    /// model directory. The loader resolves the rest of the file pack
    /// (decoder + tokenizer) from the encoder's parent directory.
    /// </param>
    /// <param name="maxTokens">Maximum tokens generated per caption. Defaults to 16 (vit-gpt2 produces short captions).</param>
    public static void RegisterViTGpt2Caption(
        ModelCatalog catalog,
        string modelName = "vit_gpt2_caption",
        string encoderRelativePath = ViTGpt2CaptionEncoderRelativePath,
        int maxTokens = 16)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderRelativePath,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderRelativePath);
                return new ViTGpt2CaptionModel(modelName, encoderPath, maxTokens);
            },
            DisplayName: "ViT-GPT2 Image Captioner",
            Parameters: "239M",
            License: "Apache-2.0",
            LicenseHolder: "nlpconnect",
            SourceUrl: "https://huggingface.co/nlpconnect/vit-gpt2-image-captioning",
            Category: "captioner",
            Modalities: ["image", "text"],
            // Multi-file model — every file required to run, relative to the
            // model directory. The catalog's RelativePath = encoderRelativePath
            // is the anchor for status checks; this list is for documentation /
            // recovery. Order: ONNX weights first, then tokenizer/configs.
            Files:
            [
                ViTGpt2CaptionDefaultFolder + "/encoder_model.onnx",
                ViTGpt2CaptionDefaultFolder + "/decoder_model.onnx",
                ViTGpt2CaptionDefaultFolder + "/tokenizer.json",
                ViTGpt2CaptionDefaultFolder + "/vocab.json",
                ViTGpt2CaptionDefaultFolder + "/merges.txt",
                ViTGpt2CaptionDefaultFolder + "/config.json",
                ViTGpt2CaptionDefaultFolder + "/generation_config.json",
                ViTGpt2CaptionDefaultFolder + "/tokenizer_config.json",
                ViTGpt2CaptionDefaultFolder + "/special_tokens_map.json",
            ]));
    }

    /// <summary>Default folder for SD-Turbo's diffusers ONNX layout.</summary>
    public const string SdTurboFolder = "sd-turbo-onnx";

    /// <summary>
    /// File-existence anchor for SD-Turbo: the UNet weights, the largest
    /// component (~1.6 GB) and the heart of the diffusion pipeline. Catalog
    /// status checks against this file; the model loader resolves the rest
    /// of the components from sibling subfolders.
    /// </summary>
    public const string SdTurboAnchor = SdTurboFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers Stability AI's SD-Turbo text-to-image model under the
    /// catalog name <paramref name="modelName"/> (defaults to
    /// <c>"sd_turbo"</c>). Generates 512×512 images in a single denoising
    /// step (~1–2 seconds per image on consumer GPUs).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>License:</strong> Stability AI Community License — free
    /// for personal use and commercial use under $1M ARR. See
    /// <a href="https://huggingface.co/stabilityai/sd-turbo">the model
    /// card</a> for current terms. Above the threshold, an Enterprise
    /// license from Stability AI is required.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> the model is a folder of HuggingFace
    /// diffusers-format subfolders (<c>text_encoder/</c>, <c>unet/</c>,
    /// <c>vae_decoder/</c>, <c>tokenizer/</c>, <c>scheduler/</c>). The
    /// model loader resolves sub-paths from the folder root.
    /// </para>
    /// </remarks>
    public static void RegisterSdTurbo(
        ModelCatalog catalog,
        string modelName = "sd_turbo",
        string folder = SdTurboFolder,
        int? seed = null)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: $"{folder}/unet/model.onnx",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Image,
            // Diffusion sampling — different output every call without a
            // fixed seed. The deterministic flag drives planner CSE: same
            // call site shares result regardless, but two textually
            // identical call sites with different seeds shouldn't fold.
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelDirectory = Path.Combine(ctx.ModelDirectory, folder);
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed);
            },
            DisplayName: "Stable Diffusion Turbo",
            Parameters: "865M",
            License: "Stability AI Community",
            LicenseHolder: "Stability AI",
            SourceUrl: "https://huggingface.co/stabilityai/sd-turbo",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",  // external-data file (UNet weights > 2GB ONNX limit)
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",  // for img2img (not used by txt2img path)
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for SDXL-Turbo's diffusers ONNX layout.</summary>
    public const string SdxlTurboFolder = "sdxl-turbo-onnx";

    /// <summary>
    /// File-existence anchor for SDXL-Turbo: the UNet weights, ~2.6B params
    /// — the largest component and the heart of the diffusion pipeline.
    /// </summary>
    public const string SdxlTurboAnchor = SdxlTurboFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers SDXL-Turbo under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"sdxl_turbo"</c>). Generates 1024×1024 images with
    /// notably better composition and prompt adherence than SD-Turbo,
    /// at modestly higher latency (~3-5s per image vs SD-Turbo's ~1-2s).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>License:</strong> Stability AI Community License — same
    /// terms as SD-Turbo. Free for personal use and commercial use under
    /// $1M ARR; Enterprise license required above that threshold.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> diffusers folder layout with the SDXL-
    /// specific addition of a second text encoder
    /// (<c>text_encoder_2/</c> alongside <c>text_encoder/</c>).
    /// </para>
    /// </remarks>
    public static void RegisterSdxlTurbo(
        ModelCatalog catalog,
        string modelName = "sdxl_turbo",
        string folder = SdxlTurboFolder,
        int? seed = null)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: $"{folder}/unet/model.onnx",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Image,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelDirectory = Path.Combine(ctx.ModelDirectory, folder);
                return new SdxlTurboModel(modelName, modelDirectory, seed);
            },
            DisplayName: "Stable Diffusion XL Turbo",
            Parameters: "2.6B (UNet) + 1.4B (text encoders)",
            License: "Stability AI Community",
            LicenseHolder: "Stability AI",
            SourceUrl: "https://huggingface.co/stabilityai/sdxl-turbo",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/text_encoder_2/model.onnx",
                $"{folder}/text_encoder_2/model.onnx_data",   // OpenCLIP-G is large; uses external data
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",             // UNet is huge (~2.6B params); external data
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/tokenizer_2/vocab.json",
                $"{folder}/tokenizer_2/merges.txt",
                $"{folder}/tokenizer_2/special_tokens_map.json",
                $"{folder}/tokenizer_2/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the fp16 Florence-2 build.</summary>
    public const string Florence2Fp16Folder = "florence-2-base-ft-fp16";

    /// <summary>Default folder for the int8-quantized Florence-2 build.</summary>
    public const string Florence2QuantizedFolder = "florence-2-base-ft-quantized";

    /// <summary>
    /// Files needed for any Florence-2 install (relative to its variant
    /// folder). The four ONNX file names are interpolated per
    /// quantization suffix; tokenizer + configs are shared across both.
    /// </summary>
    private static IReadOnlyList<string> Florence2Files(string folder, string componentSuffix) =>
    [
        $"{folder}/vision_encoder{componentSuffix}.onnx",
        $"{folder}/embed_tokens{componentSuffix}.onnx",
        $"{folder}/encoder_model{componentSuffix}.onnx",
        $"{folder}/decoder_model{componentSuffix}.onnx",
        $"{folder}/tokenizer.json",
        $"{folder}/vocab.json",
        $"{folder}/merges.txt",
        $"{folder}/config.json",
        $"{folder}/generation_config.json",
        $"{folder}/preprocessor_config.json",
        $"{folder}/special_tokens_map.json",
    ];

    /// <summary>
    /// Common backbone for the four Florence-2 caption registrations. Each
    /// caller passes the catalog name, the task prompt token, and the
    /// folder/variant to register against.
    /// </summary>
    private static void RegisterFlorence2Caption(
        ModelCatalog catalog,
        string modelName,
        string displayName,
        string taskPrompt,
        string folder,
        string componentSuffix,
        string licenseTag,
        int maxTokens)
    {
        string encoderRelativePath = $"{folder}/vision_encoder{componentSuffix}.onnx";

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderRelativePath,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.String,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderRelativePath);
                return new Florence2Model(modelName, encoderPath, taskPrompt, maxTokens);
            },
            DisplayName: displayName,
            Parameters: "232M",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/onnx-community/Florence-2-base-ft",
            Category: "captioner",
            Modalities: ["image", "text"],
            Files: Florence2Files(folder, componentSuffix)));
    }

    /// <summary>
    /// Registers Florence-2 in short-caption mode (fp16). Output is a
    /// COCO-style single-sentence caption similar to ViT-GPT2 but with
    /// noticeably better quality.
    /// </summary>
    public static void RegisterFlorence2Caption(ModelCatalog catalog) =>
        RegisterFlorence2Caption(
            catalog,
            modelName: "florence2_caption",
            displayName: "Florence-2 Caption (fp16)",
            taskPrompt: "<CAPTION>",
            folder: Florence2Fp16Folder,
            componentSuffix: "_fp16",
            licenseTag: "fp16",
            maxTokens: 50);

    /// <summary>
    /// Registers Florence-2 in detailed-caption mode (fp16). Outputs a
    /// fuller sentence with descriptive context.
    /// </summary>
    public static void RegisterFlorence2DetailedCaption(ModelCatalog catalog) =>
        RegisterFlorence2Caption(
            catalog,
            modelName: "florence2_detailed_caption",
            displayName: "Florence-2 Detailed Caption (fp16)",
            taskPrompt: "<DETAILED_CAPTION>",
            folder: Florence2Fp16Folder,
            componentSuffix: "_fp16",
            licenseTag: "fp16",
            maxTokens: 150);

    /// <summary>
    /// Registers Florence-2 in paragraph-caption mode (fp16). Outputs a
    /// multi-sentence description suitable as an SDXL prompt seed.
    /// </summary>
    public static void RegisterFlorence2MoreDetailedCaption(ModelCatalog catalog) =>
        RegisterFlorence2Caption(
            catalog,
            modelName: "florence2_more_detailed_caption",
            displayName: "Florence-2 More Detailed Caption (fp16)",
            taskPrompt: "<MORE_DETAILED_CAPTION>",
            folder: Florence2Fp16Folder,
            componentSuffix: "_fp16",
            licenseTag: "fp16",
            maxTokens: 300);

    /// <summary>
    /// Registers Florence-2 in short-caption mode using the int8-quantized
    /// build. Same model, ¼ the disk; useful for quality / size A/B against
    /// <c>florence2_caption</c>.
    /// </summary>
    public static void RegisterFlorence2CaptionQuantized(ModelCatalog catalog) =>
        RegisterFlorence2Caption(
            catalog,
            modelName: "florence2_caption_q8",
            displayName: "Florence-2 Caption (int8)",
            taskPrompt: "<CAPTION>",
            folder: Florence2QuantizedFolder,
            componentSuffix: "_quantized",
            licenseTag: "q8",
            maxTokens: 50);

    /// <summary>
    /// Default filename for the YOLOv8-nano ONNX file (the smallest variant
    /// from Ultralytics, ~12 MB). Larger variants — yolov8s.onnx, yolov8m.onnx —
    /// drop in by passing a different <c>modelFilename</c>.
    /// </summary>
    public const string YoloDefaultFilename = "yolov8n.onnx";

    /// <summary>
    /// Registers YOLOv8-nano object detection under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"yolov8n"</c>). Returns one
    /// detection-array per image. Sibling registrations like <c>yolov8s</c> /
    /// <c>yolov8m</c> drop in by passing the appropriate filename + name; the
    /// capability-level <c>tasks.detect</c> namespace will route across them.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (the <c>X</c> in <c>models.X(image)</c>). Defaults to <c>"yolov8n"</c> — Ultralytics' own size suffix.</param>
    /// <param name="modelFilename">ONNX filename relative to <see cref="ModelCatalog.ModelDirectory"/>. Defaults to <see cref="YoloDefaultFilename"/>.</param>
    /// <param name="confidenceThreshold">Score threshold below which a prediction is dropped pre-NMS. Defaults to 0.25.</param>
    /// <param name="iouThreshold">IoU threshold for NMS. Defaults to 0.45.</param>
    public static void RegisterYolo(
        ModelCatalog catalog,
        string modelName = "yolov8n",
        string modelFilename = YoloDefaultFilename,
        float confidenceThreshold = 0.25f,
        float iouThreshold = 0.45f)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            // Detection array of structs — per-element kind is Struct; the IsArray
            // bit will join the catalog surface alongside the schema-layer collapse.
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new YoloModel(modelName, modelPath, labels: null, confidenceThreshold, iouThreshold);
            },
            // Optional positional overrides for per-call thresholding:
            //   [0] confidenceThreshold (Float64) — drop predictions below this score
            //   [1] iouThreshold        (Float64) — NMS overlap threshold
            // Not yet wired into YoloModel (it uses its construction-time thresholds);
            // parser will accept them for forward-compat. TODO: thread overrides through.
            OptionalArgKinds: [DataKind.Float64, DataKind.Float64],
            DisplayName: "YOLOv8-nano Detector",
            Parameters: "3.2M",
            // AGPL-3.0 is strong copyleft + propagates to network use. Personal /
            // research / open-source-with-AGPL-compatible-license is fine; commercial
            // SaaS that exposes detection as a service must either release source
            // under AGPL or buy Ultralytics' separate commercial license.
            License: "AGPL-3.0",
            LicenseHolder: "Ultralytics",
            SourceUrl: "https://github.com/ultralytics/ultralytics",
            Category: "detector",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ────────────────────────── YOLOX (Apache-2.0) ──────────────────────────
    //
    // Megvii's YOLOX detector family — license-clean alternative to YOLOv8.
    // Seven sibling registrations spanning the full speed/accuracy ladder.
    // Same architecture, same COCO-80 vocab, different parameter counts.
    // Pre-built ONNX files available directly from the Megvii GitHub release
    // page; no Python conversion needed.

    /// <summary>Default filename for YOLOX-Nano (smallest, ~3 MB, 416×416 input).</summary>
    public const string YoloXNanoFilename = "yolox_nano.onnx";

    /// <summary>Default filename for YOLOX-Tiny (~20 MB, 416×416 input).</summary>
    public const string YoloXTinyFilename = "yolox_tiny.onnx";

    /// <summary>Default filename for YOLOX-S (~36 MB, 640×640 input).</summary>
    public const string YoloXSFilename = "yolox_s.onnx";

    /// <summary>Default filename for YOLOX-M (~98 MB, 640×640 input).</summary>
    public const string YoloXMFilename = "yolox_m.onnx";

    /// <summary>Default filename for YOLOX-L (~200 MB, 640×640 input).</summary>
    public const string YoloXLFilename = "yolox_l.onnx";

    /// <summary>Default filename for YOLOX-X (~378 MB, 640×640 input).</summary>
    public const string YoloXXFilename = "yolox_x.onnx";

    /// <summary>
    /// Default filename for YOLOX-Darknet53 — uses Darknet53 backbone
    /// instead of YOLOX's CSPNet. Sits roughly between YOLOX-L and YOLOX-X
    /// in size; its niche is "what if we kept YOLOv3's backbone but
    /// applied YOLOX's anchor-free head." Useful for academic
    /// comparisons.
    /// </summary>
    public const string YoloXDarknetFilename = "yolox_darknet.onnx";

    /// <summary>
    /// Common backbone for the seven YOLOX size registrations. Each
    /// caller supplies the catalog name, the ONNX filename, the
    /// architectural parameter count, and a one-line description.
    /// </summary>
    private static void RegisterYoloXVariant(
        ModelCatalog catalog,
        string modelName,
        string modelFilename,
        string parameters,
        string sizeLabel)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Struct,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new YoloXModel(modelName, modelPath, labels: null);
            },
            // Same optional thresholding shape as YOLOv8 (forward-compat;
            // not yet wired into per-call overrides).
            OptionalArgKinds: [DataKind.Float64, DataKind.Float64],
            DisplayName: $"YOLOX-{sizeLabel} Detector",
            Parameters: parameters,
            License: "Apache-2.0",
            LicenseHolder: "Megvii",
            SourceUrl: "https://github.com/Megvii-BaseDetection/YOLOX",
            Category: "detector",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    /// <summary>Registers YOLOX-Nano (smallest, fastest, lowest accuracy).</summary>
    public static void RegisterYoloXNano(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_n", YoloXNanoFilename, "0.91M", "Nano");

    /// <summary>Registers YOLOX-Tiny (small, fast).</summary>
    public static void RegisterYoloXTiny(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_t", YoloXTinyFilename, "5.06M", "Tiny");

    /// <summary>Registers YOLOX-S (small, balanced).</summary>
    public static void RegisterYoloXSmall(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_s", YoloXSFilename, "9.0M", "S");

    /// <summary>Registers YOLOX-M (medium).</summary>
    public static void RegisterYoloXMedium(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_m", YoloXMFilename, "25.3M", "M");

    /// <summary>Registers YOLOX-L (large, quality bias).</summary>
    public static void RegisterYoloXLarge(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_l", YoloXLFilename, "54.2M", "L");

    /// <summary>Registers YOLOX-X (extra-large, maximum accuracy).</summary>
    public static void RegisterYoloXExtraLarge(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_x", YoloXXFilename, "99.1M", "X");

    /// <summary>Registers YOLOX-Darknet53 (Darknet backbone variant).</summary>
    public static void RegisterYoloXDarknet(ModelCatalog catalog) =>
        RegisterYoloXVariant(catalog, "yolox_darknet", YoloXDarknetFilename, "63.7M", "Darknet53");

    /// <summary>
    /// Convenience: registers all seven YOLOX size variants at once.
    /// Call this in addition to <see cref="AttachStandardModels"/> if
    /// you want the full speed/accuracy cascade available — the default
    /// <c>AttachStandardModels</c> registers every YOLOX variant already.
    /// </summary>
    public static void RegisterAllYoloX(ModelCatalog catalog)
    {
        RegisterYoloXNano(catalog);
        RegisterYoloXTiny(catalog);
        RegisterYoloXSmall(catalog);
        RegisterYoloXMedium(catalog);
        RegisterYoloXLarge(catalog);
        RegisterYoloXExtraLarge(catalog);
        RegisterYoloXDarknet(catalog);
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
