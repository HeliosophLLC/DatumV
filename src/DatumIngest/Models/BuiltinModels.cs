using System.Text.Json;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Models.Llama;
using DatumIngest.Models.Onnx;
using DatumIngest.Models.Onnx.PaliGemma;
using DatumIngest.Models.Onnx.Whisper;
using DatumIngest.Models.Python;

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
    /// builtin model (<c>mobilenetv2</c>, <c>yolox_s</c>, <c>llama31_8b</c>,
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
    /// <param name="vramBudgetBytes">
    /// VRAM budget for the residency manager. <see langword="null"/>
    /// auto-detects via <see cref="VramBudgetResolver.Resolve"/> — queries
    /// nvidia-smi for total device memory and subtracts a 4 GiB headroom.
    /// Pass <see cref="ModelResidencyManager.UnlimitedBudget"/> to disable
    /// admission control (the old load-once-hold-forever behaviour, which
    /// over-allocates into shared system memory once the zoo grows past
    /// physical VRAM).
    /// </param>
    public static ModelCatalog AttachStandardModels(
        TableCatalog tableCatalog,
        string? modelDirectory = null,
        long? vramBudgetBytes = null)
    {
        long resolvedBudget = vramBudgetBytes ?? VramBudgetResolver.Resolve();
        ModelCatalog modelCatalog = new(modelDirectory, resolvedBudget, admissionTimeout: null);

        // Vision models
        RegisterMobileNetV2(modelCatalog);
        RegisterAllYoloX(modelCatalog);  // 7 entries: nano/tiny/s/m/l/x/darknet
        RegisterScrfd10g(modelCatalog);
        RegisterPpOcrDetV4(modelCatalog);
        RegisterRealesrganGeneralX4(modelCatalog);
        RegisterU2Net(modelCatalog);
        RegisterU2Netp(modelCatalog);
        RegisterMidasSmall(modelCatalog);
        RegisterDptLarge(modelCatalog);
        RegisterMobileSamPrompted(modelCatalog);
        RegisterMobileSam(modelCatalog);
        RegisterViTGpt2Caption(modelCatalog);
        RegisterTrOcrPrinted(modelCatalog);
        RegisterTrOcrPrintedFp16(modelCatalog);

        // Captioner zoo — Florence-2 in three caption styles plus a
        // quantized comparison entry. Same model, different task tokens.
        // OCR-with-region reuses the same files via a different task token
        // and is the license-clean baseline for screenshot text extraction.
        RegisterFlorence2Caption(modelCatalog);
        RegisterFlorence2DetailedCaption(modelCatalog);
        RegisterFlorence2MoreDetailedCaption(modelCatalog);
        RegisterFlorence2CaptionQuantized(modelCatalog);
        RegisterFlorence2OcrWithRegion(modelCatalog);

        // Image generation. SD-Turbo is the closing leg of the
        // image-in → caption → LLM-narrative → image-out pipeline.
        // SDXL-Turbo is the higher-quality sibling for hero outputs.
        // Juggernaut XL Lightning is the high-realism alternative.
        RegisterSdTurbo(modelCatalog);
        RegisterRealisticVisionHyper(modelCatalog);
        RegisterDreamshaperHyper(modelCatalog);
        RegisterEpicrealismHyper(modelCatalog);
        RegisterOpenjourneyHyper(modelCatalog);
        RegisterMoDiHyper(modelCatalog);
        RegisterAbsoluteRealityHyper(modelCatalog);
        RegisterSdxlTurbo(modelCatalog);
        RegisterJuggernautXlLightning(modelCatalog);

        // Audio generation.
        RegisterMusicGenSmall(modelCatalog);
        RegisterMusicGenMedium(modelCatalog);

        // LLM zoo — seven entries spanning Meta, Microsoft, TinyLlama community,
        // Google, Alibaba, IBM, TII. Every voice in the zoo is at Q4_K_M
        // quantization for clean cross-model comparison (architectural
        // differences, not quantization noise). Total disk: ~12 GB if all
        // present; missing files surface in `system_models` as `status =
        // 'missing'` with re-download hints.
        RegisterLlama31(modelCatalog);
        RegisterPhi3(modelCatalog);
        RegisterPhi35Mini(modelCatalog);
        RegisterTinyLlama(modelCatalog);
        RegisterGemma22b(modelCatalog);
        RegisterQwen25Coder(modelCatalog);
        RegisterQwen25Coder3B(modelCatalog);
        RegisterQwen25Coder7B(modelCatalog);
        RegisterGranite31(modelCatalog);
        RegisterFalcon31b(modelCatalog);
        RegisterMistral7B(modelCatalog);

        // Whisper STT zoo. All four sizes; each shows status=missing in
        // system.models until its optimum-cli output folder lands.
        RegisterWhisperTiny(modelCatalog);
        RegisterWhisperBase(modelCatalog);
        RegisterWhisperSmall(modelCatalog);
        RegisterWhisperMedium(modelCatalog);

        // PaliGemma 2 captioner zoo. Two resolutions: the 224 variant
        // for cheap iteration, the 448 variant as the better default.
        // Both report status=missing until their optimum-cli output
        // folders land.
        RegisterPaliGemma2Mix224(modelCatalog);
        RegisterPaliGemma2Mix448(modelCatalog);

        // VLM zoo — Apache/MIT-licensed alternatives to OmniParser's
        // AGPL detector. Free-form prompt as the second positional arg:
        // `models.moondream2(image, 'Describe this screenshot')`.
        // Phi-3.5-vision uses ORT GenAI for IO-binding-accelerated
        // generation; Moondream2 is the hand-rolled baseline.
        RegisterPhi35Vision(modelCatalog);
        RegisterMoondream2(modelCatalog);

        // Python-bridge models. These show status=bridge in system.models
        // (rather than available/missing) when their worker scripts and
        // model files exist, signalling that runnability also depends on
        // a Python venv with the upstream packages installed -- catalog
        // can't verify pip state without spawning the worker. Conventional
        // venv layout is {ModelDirectory}/.venv-<name>/ which the loaders
        // auto-detect.
        RegisterBarkSmall(modelCatalog);
        RegisterBark(modelCatalog);
        RegisterKokoro82M(modelCatalog);

        tableCatalog.Models = modelCatalog;
        tableCatalog.Add(new ModelsTableProvider(
            tableCatalog.Pool, modelCatalog, tableCatalog.DeclaredModels));
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
        // Llama 3.1 was trained for 128K native. Defaults are sized for
        // long-form creative output without truncation: 32K context lets
        // the prompt grow well past 20K tokens (chained captions + system
        // prompt + multi-shot examples + previous campaign context) while
        // leaving room for an 8K-token generation. ~2 GB KV cache on 8B
        // Q4_K_M, so total resident VRAM is ~7 GB -- comfortable on a 24 GB
        // card alongside SDXL / Whisper / Kokoro. Bump to 65536 / 16384
        // for the rare campaign-spanning generation that needs more.
        uint contextSize = 32768,
        int maxTokens = 8192,
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
            //   [2] templated   (Boolean)  — when true, treat the prompt as
            //                                already templated (skip Format).
            //                                Pair with templates.llama31_open()
            //                                || templates.llama31_msg(...) || ...
            //                                || templates.llama31_assistant_turn().
            // Order matters: callers supply a prefix. `models.llama31_8b(prompt, 0.9)`
            // overrides temperature only; `models.llama31_8b(prompt, 0.9, 64)`
            // overrides both; pass NULLs to skip earlier opts when only the
            // tail matters: `models.llama31_8b(p, NULL, NULL, true)`.
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
            DisplayName: "Phi-3-mini-4k Instruct",
            Parameters: "3.8B",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/bartowski/Phi-3-mini-4k-instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Phi-3.5-mini-instruct (bartowski's GGUF mirror, Q4_K_M).</summary>
    public const string Phi35MiniDefaultFilename = "Phi-3.5-mini-instruct-Q4_K_M.gguf";

    /// <summary>
    /// Registers Microsoft's Phi-3.5-mini-instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"phi35_mini"</c>).
    /// Same architecture as Phi-3-mini but trained natively for 128K
    /// context — long-prompt rewrites that NoKvSlot on
    /// <c>phi3_mini</c> (4K-trained) fit comfortably here. Slightly
    /// better instruction-following than Phi-3 across the board.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Why pick Phi-3.5-mini over Phi-3-mini?</strong> Phi-3-mini
    /// shipped two variants — 4K and 128K. Our default <c>phi3_mini</c>
    /// uses the 4K-trained GGUF, which has a hard ceiling at 4096 tokens
    /// (RoPE doesn't extrapolate cleanly). Phi-3.5-mini supersedes both;
    /// it's the recommended Phi entry for any pipeline that chains
    /// captions + system prompts + multi-paragraph generation.
    /// </para>
    /// <para>
    /// <strong>Setup</strong> — download the bartowski GGUF:
    /// <code>
    /// huggingface-cli download bartowski/Phi-3.5-mini-instruct-GGUF `
    ///   Phi-3.5-mini-instruct-Q4_K_M.gguf `
    ///   --local-dir $env:DATUM_MODELS
    /// </code>
    /// ~2.4 GB. Same chat template as Phi-3 (<c>LlamaChatTemplate.Phi3</c>);
    /// LlamaSharp handles both transparently.
    /// </para>
    /// </remarks>
    public static void RegisterPhi35Mini(
        ModelCatalog catalog,
        string modelName = "phi35_mini",
        string modelFilename = Phi35MiniDefaultFilename,
        // 128K-native; 16K is a comfortable mid-point matching Llama 3.1.
        // Bump higher if your prompts routinely exceed ~12K tokens.
        uint contextSize = 16384,
        int maxTokens = 4096,
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
            DisplayName: "Phi-3.5-mini Instruct (128K)",
            Parameters: "3.8B",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/bartowski/Phi-3.5-mini-instruct-GGUF",
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
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

    /// <summary>Default filename for Qwen2.5-Coder-3B-Instruct (bartowski's GGUF mirror, Q4_K_M).</summary>
    public const string Qwen25Coder3bDefaultFilename = "Qwen2.5-Coder-3B-Instruct-Q4_K_M.gguf";

    /// <summary>Default filename for Qwen2.5-Coder-7B-Instruct (bartowski's GGUF mirror, Q5_K_M).</summary>
    public const string Qwen25Coder7bDefaultFilename = "Qwen2.5-Coder-7B-Instruct-Q5_K_M.gguf";

    /// <summary>
    /// Common backbone for Qwen2.5-Coder size variants. Each public
    /// <c>RegisterQwen25Coder*</c> wraps this with size-appropriate
    /// catalog metadata and defaults — the bigger models default to wider
    /// context windows and larger output budgets so HTML/multi-file code
    /// generation works without per-call overrides.
    /// </summary>
    private static void RegisterQwen25CoderVariant(
        ModelCatalog catalog,
        string modelName,
        string modelFilename,
        string displayName,
        string parameters,
        string sourceUrl,
        uint contextSize,
        int maxTokens,
        float temperature)
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
            DisplayName: displayName,
            Parameters: parameters,
            License: "Apache-2.0",
            LicenseHolder: "Alibaba",
            SourceUrl: sourceUrl,
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>
    /// Registers Alibaba's Qwen2.5-Coder-1.5B-Instruct under the catalog
    /// name <paramref name="modelName"/> (defaults to <c>"qwen25_coder_1_5b"</c>).
    /// The "fast iteration" rung of the Qwen-Coder ladder — small enough
    /// to dispatch in well under a second on a consumer GPU, useful for
    /// rapid A/B comparison against the 3B and 7B siblings.
    /// </summary>
    public static void RegisterQwen25Coder(
        ModelCatalog catalog,
        string modelName = "qwen25_coder_1_5b",
        string modelFilename = Qwen25Coder15bDefaultFilename,
        uint contextSize = 4096,
        int maxTokens = 256,
        float temperature = 0.7f)
        => RegisterQwen25CoderVariant(
            catalog, modelName, modelFilename,
            displayName: "Qwen 2.5 Coder 1.5B Instruct",
            parameters: "1.5B",
            sourceUrl: "https://huggingface.co/bartowski/Qwen2.5-Coder-1.5B-Instruct-GGUF",
            contextSize, maxTokens, temperature);

    /// <summary>
    /// Registers Alibaba's Qwen2.5-Coder-3B-Instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"qwen25_coder_3b"</c>).
    /// The middle rung — meaningfully more coherent on multi-section HTML
    /// and short scripts than the 1.5B, still under 2 GB on disk at Q4_K_M.
    /// Defaults to a 16K context window and 2K-token output budget so
    /// "write a Geocities page" calls work without per-call overrides.
    /// </summary>
    public static void RegisterQwen25Coder3B(
        ModelCatalog catalog,
        string modelName = "qwen25_coder_3b",
        string modelFilename = Qwen25Coder3bDefaultFilename,
        uint contextSize = 16384,
        int maxTokens = 2048,
        float temperature = 0.7f)
        => RegisterQwen25CoderVariant(
            catalog, modelName, modelFilename,
            displayName: "Qwen 2.5 Coder 3B Instruct",
            parameters: "3B",
            sourceUrl: "https://huggingface.co/bartowski/Qwen2.5-Coder-3B-Instruct-GGUF",
            contextSize, maxTokens, temperature);

    /// <summary>
    /// Registers Alibaba's Qwen2.5-Coder-7B-Instruct under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"qwen25_coder_7b"</c>).
    /// The "real coder" rung — handles full single-file pages with embedded
    /// CSS/JS, multi-paragraph rationale, and consistent style across long
    /// outputs. Defaults to 16K context, 4K output budget, and a lower
    /// temperature (0.5) to favour determinism over flair when generating
    /// code.
    /// </summary>
    public static void RegisterQwen25Coder7B(
        ModelCatalog catalog,
        string modelName = "qwen25_coder_7b",
        string modelFilename = Qwen25Coder7bDefaultFilename,
        // Qwen 2.5 7B is trained at 32 768. Bumped from 16384 → 32768 so chat
        // sessions can run longer before compacting kicks in. KV cache scales
        // linearly with context (≈2× memory at 32K vs 16K) but the residency
        // headroom easily absorbs it on a single-model host.
        uint contextSize = 32768,
        int maxTokens = 4096,
        float temperature = 0.5f)
        => RegisterQwen25CoderVariant(
            catalog, modelName, modelFilename,
            displayName: "Qwen 2.5 Coder 7B Instruct",
            parameters: "7B",
            sourceUrl: "https://huggingface.co/bartowski/Qwen2.5-Coder-7B-Instruct-GGUF",
            contextSize, maxTokens, temperature);

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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
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
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
            DisplayName: "Falcon3 1B Instruct",
            Parameters: "1B",
            License: "Falcon LLM License 2.0",
            LicenseHolder: "TII",
            SourceUrl: "https://huggingface.co/tiiuae/Falcon3-1B-Instruct-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    /// <summary>Default filename for Mistral-7B-Instruct-v0.3 (bartowski's GGUF mirror, Q5_K_M).</summary>
    public const string Mistral7Bv03DefaultFilename = "Mistral-7B-Instruct-v0.3-Q5_K_M.gguf";

    /// <summary>
    /// Registers Mistral AI's Mistral-7B-Instruct-v0.3 under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"mistral_7b"</c>). The
    /// French-trained 7B that fills the Mistral-shaped gap in the LLM zoo —
    /// a distinct voice from the Meta / Microsoft / Alibaba / Google entries
    /// already registered. Apache-2.0 — fully unencumbered for commercial use.
    /// </summary>
    /// <remarks>
    /// Uses the <see cref="LlamaChatTemplate.Mistral"/> template
    /// (<c>[INST] ... [/INST]</c>). v0.3 was trained for 32K native context;
    /// the default 16K matches the Phi-3.5 / Qwen2.5 zoo siblings and leaves
    /// generation headroom on a 24 GB card.
    /// </remarks>
    public static void RegisterMistral7B(
        ModelCatalog catalog,
        string modelName = "mistral_7b",
        string modelFilename = Mistral7Bv03DefaultFilename,
        uint contextSize = 16384,
        int maxTokens = 16384,
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
                return new LlamaModel(modelName, modelPath, LlamaChatTemplate.Mistral, contextSize, maxTokens, temperature);
            },
            OptionalArgKinds: [DataKind.Float64, DataKind.Int32, DataKind.Boolean],
            DisplayName: "Mistral 7B Instruct v0.3",
            Parameters: "7B",
            License: "Apache-2.0",
            LicenseHolder: "Mistral AI",
            SourceUrl: "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF",
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

    /// <summary>
    /// Default folder for the TrOCR printed-text OCR model. Holds both
    /// fp32 and fp16 ONNX exports (encoder + merged decoder for each
    /// precision) plus a single shared tokenizer + config set, all
    /// produced by <c>optimum-cli export onnx</c>.
    /// </summary>
    public const string TrOcrPrintedFolder = "trocr-base-printed";

    /// <summary>
    /// Default fp32 anchor: <c>{folder}/encoder_model.onnx</c>. The
    /// <see cref="TrOcrModel"/> loader derives the model directory from
    /// this path and pairs it with <c>decoder_model_merged.onnx</c>.
    /// </summary>
    public const string TrOcrPrintedEncoderRelativePath =
        TrOcrPrintedFolder + "/encoder_model.onnx";

    /// <summary>
    /// fp16 anchor: <c>{folder}/encoder_model_fp16.onnx</c>. Pairs with
    /// <c>decoder_model_merged_fp16.onnx</c>.
    /// </summary>
    public const string TrOcrPrintedFp16EncoderRelativePath =
        TrOcrPrintedFolder + "/encoder_model_fp16.onnx";

    /// <summary>
    /// Registers Microsoft's <c>trocr-base-printed</c> model — ViT-base
    /// 384×384 encoder + 12-layer RoBERTa decoder, ~1.3 GB on disk as
    /// fp32 ONNX. MIT licensed; targets single-line printed text
    /// (receipts, signage, document lines).
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (defaults to <c>"trocr_printed"</c>).</param>
    /// <param name="encoderRelativePath">Path to the encoder ONNX, relative to <c>ModelDirectory</c>.</param>
    /// <param name="maxTokens">Maximum tokens generated per image. Defaults to 20 (matches generation_config).</param>
    public static void RegisterTrOcrPrinted(
        ModelCatalog catalog,
        string modelName = "trocr_printed",
        string encoderRelativePath = TrOcrPrintedEncoderRelativePath,
        int maxTokens = 20)
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
                return new TrOcrModel(modelName, encoderPath, maxTokens: maxTokens);
            },
            DisplayName: "TrOCR Printed (fp32)",
            Parameters: "334M",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/microsoft/trocr-base-printed",
            Category: "ocr",
            Modalities: ["image", "text"],
            Files:
            [
                TrOcrPrintedFolder + "/encoder_model.onnx",
                TrOcrPrintedFolder + "/decoder_model_merged.onnx",
                TrOcrPrintedFolder + "/tokenizer.json",
                TrOcrPrintedFolder + "/vocab.json",
                TrOcrPrintedFolder + "/merges.txt",
                TrOcrPrintedFolder + "/config.json",
                TrOcrPrintedFolder + "/generation_config.json",
                TrOcrPrintedFolder + "/preprocessor_config.json",
                TrOcrPrintedFolder + "/tokenizer_config.json",
                TrOcrPrintedFolder + "/special_tokens_map.json",
            ]));
    }

    /// <summary>
    /// Registers the fp16 TrOCR variant under the catalog name
    /// <paramref name="modelName"/> (defaults to
    /// <c>"trocr_printed_fp16"</c>). ~640 MB on disk — about half the
    /// fp32 size, with negligible accuracy loss for printed-text OCR.
    /// Shares the tokenizer + config files with the fp32 entry; both
    /// can live in the same folder.
    /// </summary>
    public static void RegisterTrOcrPrintedFp16(
        ModelCatalog catalog,
        string modelName = "trocr_printed_fp16",
        string encoderRelativePath = TrOcrPrintedFp16EncoderRelativePath,
        int maxTokens = 20)
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
                return new TrOcrModel(
                    modelName,
                    encoderPath,
                    decoderFileName: "decoder_model_merged_fp16.onnx",
                    maxTokens: maxTokens);
            },
            DisplayName: "TrOCR Printed (fp16)",
            Parameters: "334M",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/microsoft/trocr-base-printed",
            Category: "ocr",
            Modalities: ["image", "text"],
            Files:
            [
                TrOcrPrintedFolder + "/encoder_model_fp16.onnx",
                TrOcrPrintedFolder + "/decoder_model_merged_fp16.onnx",
                TrOcrPrintedFolder + "/tokenizer.json",
                TrOcrPrintedFolder + "/vocab.json",
                TrOcrPrintedFolder + "/merges.txt",
                TrOcrPrintedFolder + "/config.json",
                TrOcrPrintedFolder + "/generation_config.json",
                TrOcrPrintedFolder + "/preprocessor_config.json",
                TrOcrPrintedFolder + "/tokenizer_config.json",
                TrOcrPrintedFolder + "/special_tokens_map.json",
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
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
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

    /// <summary>Default folder for the Realistic Vision V6 + Hyper-SD diffusers ONNX layout.</summary>
    public const string RealisticVisionHyperFolder = "realistic-vision-hyper-onnx";

    /// <summary>
    /// File-existence anchor for Realistic Vision V6 + Hyper-SD: the UNet
    /// weights, ~865M params (same architecture as SD-Turbo / SD 2.1, but the
    /// underlying weights are SG161222's Realistic Vision V6 finetune of SD 1.5
    /// with ByteDance's Hyper-SD 4-step distillation LoRA fused in).
    /// </summary>
    public const string RealisticVisionHyperAnchor = RealisticVisionHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers Realistic Vision V6 (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"realistic_vision_hyper"</c>). Drop-in replacement for <c>sd_turbo</c>
    /// at the same wall-clock cost (~250–330ms per 512×512 image at 4 steps),
    /// trained on a narrower people-and-portraits distribution and distilled
    /// via Hyper-SD's TSCD+RLHF pipeline. Preferred over SD-Turbo when the
    /// prompt describes people or characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the
    /// Realistic Vision V6 base and the Hyper-SD LoRA. See the model cards
    /// at <a href="https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE">SG161222/Realistic_Vision_V6.0_B1_noVAE</a>
    /// and <a href="https://huggingface.co/ByteDance/Hyper-SD">ByteDance/Hyper-SD</a>
    /// for the full terms.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout (single text
    /// encoder, no <c>text_encoder_2/</c>) — same shape as SD-Turbo, paired
    /// with sd-vae-ft-mse for the VAE.
    /// </para>
    /// </remarks>
    public static void RegisterRealisticVisionHyper(
        ModelCatalog catalog,
        string modelName = "realistic_vision_hyper",
        string folder = RealisticVisionHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "Realistic Vision V6 + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "SG161222 / ByteDance",
            SourceUrl: "https://huggingface.co/SG161222/Realistic_Vision_V6.0_B1_noVAE",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the DreamShaper 8 + Hyper-SD diffusers ONNX layout.</summary>
    public const string DreamshaperHyperFolder = "dreamshaper-hyper-onnx";

    /// <summary>
    /// File-existence anchor for DreamShaper 8 + Hyper-SD: the UNet weights,
    /// ~865M params (same architecture as SD-Turbo / SD 1.5, but the
    /// underlying weights are Lykon's DreamShaper 8 finetune of SD 1.5
    /// with ByteDance's Hyper-SD 4-step distillation LoRA fused in).
    /// </summary>
    public const string DreamshaperHyperAnchor = DreamshaperHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers DreamShaper 8 (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"dreamshaper_hyper"</c>). Stylized / painterly leaning fantasy
    /// and concept-art — strong fit for D&amp;D fantasy aesthetics, monsters,
    /// and atmospheric environments. Same wall-clock cost as
    /// <c>realistic_vision_hyper</c> (~250–330ms per 512×512 image at 4 steps);
    /// notably less NSFW-leaning than Realistic Vision V6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the
    /// DreamShaper 8 base and the Hyper-SD LoRA. See the model cards
    /// at <a href="https://huggingface.co/Lykon/dreamshaper-8">Lykon/dreamshaper-8</a>
    /// and <a href="https://huggingface.co/ByteDance/Hyper-SD">ByteDance/Hyper-SD</a>
    /// for the full terms.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout — DreamShaper
    /// ships with its own bundled VAE, no separate sd-vae-ft-mse pairing.
    /// </para>
    /// </remarks>
    public static void RegisterDreamshaperHyper(
        ModelCatalog catalog,
        string modelName = "dreamshaper_hyper",
        string folder = DreamshaperHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "DreamShaper 8 + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "Lykon / ByteDance",
            SourceUrl: "https://huggingface.co/Lykon/dreamshaper-8",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the epiCRealism + Hyper-SD diffusers ONNX layout.</summary>
    public const string EpicrealismHyperFolder = "epicrealism-hyper-onnx";

    /// <summary>
    /// File-existence anchor for epiCRealism + Hyper-SD: the UNet weights,
    /// ~865M params (same architecture as SD-Turbo / SD 1.5, but the
    /// underlying weights are emilianJR's epiCRealism finetune of SD 1.5
    /// with ByteDance's Hyper-SD 4-step distillation LoRA fused in).
    /// </summary>
    public const string EpicrealismHyperAnchor = EpicrealismHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers epiCRealism (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"epicrealism_hyper"</c>). Photoreal-leaning with broader subject
    /// coverage than Realistic Vision V6 — strong fit for D&amp;D environments,
    /// taverns, landscapes, and group scenes where RV's narrow portrait
    /// distribution starts to limit composition. Same wall-clock cost as
    /// the other SD 1.5 + Hyper exports (~250–330ms per 512×512 image at
    /// 4 steps); notably less NSFW-leaning than Realistic Vision V6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the
    /// epiCRealism base and the Hyper-SD LoRA. See the model cards
    /// at <a href="https://huggingface.co/emilianJR/epiCRealism">emilianJR/epiCRealism</a>
    /// and <a href="https://huggingface.co/ByteDance/Hyper-SD">ByteDance/Hyper-SD</a>
    /// for the full terms.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout — epiCRealism
    /// ships with its own bundled VAE, no separate sd-vae-ft-mse pairing.
    /// </para>
    /// </remarks>
    public static void RegisterEpicrealismHyper(
        ModelCatalog catalog,
        string modelName = "epicrealism_hyper",
        string folder = EpicrealismHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "epiCRealism + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "emilianJR / ByteDance",
            SourceUrl: "https://huggingface.co/emilianJR/epiCRealism",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the Openjourney v4 + Hyper-SD diffusers ONNX layout.</summary>
    public const string OpenjourneyHyperFolder = "openjourney-hyper-onnx";

    /// <summary>
    /// File-existence anchor for Openjourney v4 + Hyper-SD: the UNet weights,
    /// ~865M params (same architecture as SD-Turbo / SD 1.5, but the
    /// underlying weights are PromptHero's Openjourney v4 finetune of SD 1.5
    /// — trained on Midjourney v4 outputs — with ByteDance's Hyper-SD 4-step
    /// distillation LoRA fused in).
    /// </summary>
    public const string OpenjourneyHyperAnchor = OpenjourneyHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers Openjourney v4 (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"openjourney_hyper"</c>). Reproduces Midjourney v4's dramatic
    /// lighting, painterly atmosphere, and cinematic composition fingerprint
    /// — strong fit for D&amp;D set-pieces and atmospheric scenes (temple
    /// approaches, dusk vistas, dramatic NPC reveals). Same wall-clock cost
    /// as the other SD 1.5 + Hyper exports (~250–330ms per 512×512 image at 4 steps).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the
    /// Openjourney v4 base and the Hyper-SD LoRA. v4 dropped the
    /// "mdjrny-v4 style" trigger token that v3 required — prompts work
    /// without any prefix.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout — Openjourney
    /// ships with its own bundled VAE, no separate sd-vae-ft-mse pairing.
    /// </para>
    /// </remarks>
    public static void RegisterOpenjourneyHyper(
        ModelCatalog catalog,
        string modelName = "openjourney_hyper",
        string folder = OpenjourneyHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "Openjourney v4 + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "PromptHero / ByteDance",
            SourceUrl: "https://huggingface.co/prompthero/openjourney-v4",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the Mo Di Diffusion + Hyper-SD diffusers ONNX layout.</summary>
    public const string MoDiHyperFolder = "mo-di-hyper-onnx";

    /// <summary>
    /// File-existence anchor for Mo Di Diffusion + Hyper-SD: the UNet weights,
    /// ~865M params (same architecture as SD-Turbo / SD 1.5, but the
    /// underlying weights are nitrosocke's Mo Di Diffusion finetune of SD 1.5
    /// — trained on modern Disney/Pixar 3D animated stills — with ByteDance's
    /// Hyper-SD 4-step distillation LoRA fused in).
    /// </summary>
    public const string MoDiHyperAnchor = MoDiHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers Mo Di Diffusion (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"mo_di_hyper"</c>). Disney / Pixar 3D-render style — whimsical,
    /// rounded, character-forward. A radically different visual envelope
    /// from the photoreal / painterly / Midjourney finetunes; useful for
    /// tone shifts (lighter side-quests, comic-relief NPCs, family-friendly
    /// campaign lines).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Trigger phrase:</strong> Mo Di was trained with the trigger
    /// token <c>"modern disney style"</c>. Prompts without it still produce
    /// reasonable images, but the Disney/Pixar fingerprint only fully kicks
    /// in when the trigger is present. Prepend it in calls:
    /// <c>models.mo_di_hyper('modern disney style, halfling rogue with a sly grin')</c>.
    /// The trigger is purely a prompt convention — nothing about the loader
    /// or scheduler has to know about it.
    /// </para>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the Mo Di
    /// base and the Hyper-SD LoRA. Same nitrosocke lineage as Arcane-Diffusion
    /// and Redshift if you want more stylized variants later.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout — Mo Di
    /// ships with its own bundled VAE.
    /// </para>
    /// </remarks>
    public static void RegisterMoDiHyper(
        ModelCatalog catalog,
        string modelName = "mo_di_hyper",
        string folder = MoDiHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "Mo Di Diffusion + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "nitrosocke / ByteDance",
            SourceUrl: "https://huggingface.co/nitrosocke/mo-di-diffusion",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
                $"{folder}/tokenizer/vocab.json",
                $"{folder}/tokenizer/merges.txt",
                $"{folder}/tokenizer/special_tokens_map.json",
                $"{folder}/tokenizer/tokenizer_config.json",
                $"{folder}/scheduler/scheduler_config.json",
                $"{folder}/model_index.json",
            ]));
    }

    /// <summary>Default folder for the AbsoluteReality + Hyper-SD diffusers ONNX layout.</summary>
    public const string AbsoluteRealityHyperFolder = "absolute-reality-hyper";

    /// <summary>
    /// File-existence anchor for AbsoluteReality + Hyper-SD: the UNet weights,
    /// ~865M params (same architecture as SD-Turbo / SD 1.5, but the
    /// underlying weights are Lykon's AbsoluteReality finetune of SD 1.5
    /// with ByteDance's Hyper-SD 4-step distillation LoRA fused in).
    /// </summary>
    public const string AbsoluteRealityHyperAnchor = AbsoluteRealityHyperFolder + "/unet/model.onnx";

    /// <summary>
    /// Registers AbsoluteReality (SD 1.5 finetune) + Hyper-SD 4-step LoRA
    /// under the catalog name <paramref name="modelName"/> (defaults to
    /// <c>"absolute_reality_hyper"</c>). Lykon's photoreal SD 1.5 flagship —
    /// the SFW general workhorse niche. Versatile coverage across portraits,
    /// scenes, and characters; less stylized than DreamShaper, more general
    /// than epiCRealism's environment lean. Same wall-clock cost as the other
    /// SD 1.5 + Hyper exports (~250–330ms per 512×512 image at 4 steps);
    /// notably less NSFW-leaning than Realistic Vision V6.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Architecture:</strong> SD 1.5 UNet (CLIP-L text encoder, 768
    /// hidden dim, 512×512 native). Identical pipeline shape to SD-Turbo, so
    /// the same <see cref="StableDiffusionTurboModel"/> loader handles it.
    /// </para>
    /// <para>
    /// <strong>License:</strong> CreativeML OpenRAIL-M for both the
    /// AbsoluteReality base and the Hyper-SD LoRA. See the model cards
    /// at <a href="https://huggingface.co/Lykon/AbsoluteReality">Lykon/AbsoluteReality</a>
    /// and <a href="https://huggingface.co/ByteDance/Hyper-SD">ByteDance/Hyper-SD</a>
    /// for the full terms.
    /// </para>
    /// <para>
    /// <strong>Layout:</strong> standard diffusers folder layout — AbsoluteReality
    /// ships with its own bundled VAE, no separate sd-vae-ft-mse pairing.
    /// </para>
    /// </remarks>
    public static void RegisterAbsoluteRealityHyper(
        ModelCatalog catalog,
        string modelName = "absolute_reality_hyper",
        string folder = AbsoluteRealityHyperFolder,
        int? seed = null,
        int steps = 4)
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
                return new StableDiffusionTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "AbsoluteReality + Hyper-SD",
            Parameters: "865M (UNet) + 123M (text encoder)",
            License: "CreativeML OpenRAIL-M",
            LicenseHolder: "Lykon / ByteDance",
            SourceUrl: "https://huggingface.co/Lykon/AbsoluteReality",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
                $"{folder}/vae_decoder/model.onnx",
                $"{folder}/vae_encoder/model.onnx",
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
        int? seed = null,
        int steps = 4)
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
                return new SdxlTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "Stable Diffusion XL Turbo",
            Parameters: "2.6B (UNet) + 1.4B (text encoders)",
            License: "Stability AI Community",
            LicenseHolder: "Stability AI",
            SourceUrl: "https://huggingface.co/onnxruntime/sdxl-turbo",
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

    /// <summary>Default folder for Juggernaut XL Lightning's diffusers ONNX layout.</summary>
    public const string JuggernautXlLightningFolder = "juggernaut-xl-lightning-onnx";

    /// <summary>
    /// Registers Juggernaut XL Lightning under the catalog name
    /// <paramref name="modelName"/> (defaults to
    /// <c>"juggernaut_xl_lightning"</c>). Generates 1024×1024 images.
    /// Uses the same SDXL pipeline as <see cref="RegisterSdxlTurbo"/>
    /// — dual text encoders, UNet, VAE decoder — with RunDiffusion's
    /// Juggernaut XL weights distilled via ByteDance SDXL-Lightning.
    /// </summary>
    /// <remarks>
    /// Lightning distillation is optimised for 4–8 denoising steps; the
    /// single-step Euler path we use here produces results but noticeably
    /// better quality emerges with multi-step support (future follow-up).
    /// </remarks>
    public static void RegisterJuggernautXlLightning(
        ModelCatalog catalog,
        string modelName = "juggernaut_xl_lightning",
        string folder = JuggernautXlLightningFolder,
        int? seed = null,
        int steps = 4)
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
                return new SdxlTurboModel(modelName, modelDirectory, seed, steps);
            },
            DisplayName: "Juggernaut XL Lightning",
            Parameters: "2.6B (UNet) + 1.4B (text encoders)",
            License: "Stability AI Community",
            LicenseHolder: "RunDiffusion / Stability AI",
            SourceUrl: "https://huggingface.co/RunDiffusion/Juggernaut-XL-Lightning",
            Category: "generator",
            Modalities: ["text", "image"],
            Files:
            [
                $"{folder}/text_encoder/model.onnx",
                $"{folder}/text_encoder_2/model.onnx",
                $"{folder}/text_encoder_2/model.onnx_data",
                $"{folder}/unet/model.onnx",
                $"{folder}/unet/model.onnx_data",
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

    /// <summary>Default folder for MusicGen Small's ONNX export.</summary>
    public const string MusicGenSmallFolder = "musicgen-small-onnx";

    /// <summary>Default folder for MusicGen Medium's ONNX export.</summary>
    public const string MusicGenMediumFolder = "musicgen-medium-onnx";

    /// <summary>
    /// Registers MusicGen Small under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"musicgen_small"</c>). Generates mono 32 kHz audio from a
    /// text prompt using Meta's 300M-parameter music generation model.
    /// </summary>
    public static void RegisterMusicGenSmall(
        ModelCatalog catalog,
        string modelName = "musicgen_small",
        string folder = MusicGenSmallFolder,
        int maxNewTokens = 512,
        int? seed = null)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: $"{folder}/decoder_model.onnx",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Audio,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelDirectory = Path.Combine(ctx.ModelDirectory, folder);
                return new MusicGenModel(modelName, modelDirectory, maxNewTokens, seed);
            },
            DisplayName: "MusicGen Small",
            Parameters: "300M",
            License: "CC-BY-NC-4.0",
            LicenseHolder: "Meta",
            SourceUrl: "https://huggingface.co/facebook/musicgen-small",
            Category: "generator",
            Modalities: ["text", "audio"],
            Files:
            [
                $"{folder}/text_encoder.onnx",
                $"{folder}/decoder_model.onnx",
                $"{folder}/decoder_with_past_model.onnx",
                $"{folder}/build_delay_pattern_mask.onnx",
                $"{folder}/encodec_decode.onnx",
                $"{folder}/tokenizer.json",
                $"{folder}/config.json",
                $"{folder}/generation_config.json",
            ]));
    }

    /// <summary>
    /// Registers MusicGen Medium under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"musicgen_medium"</c>). Generates mono 32 kHz audio from a
    /// text prompt using Meta's 1.5B-parameter music generation model.
    /// </summary>
    public static void RegisterMusicGenMedium(
        ModelCatalog catalog,
        string modelName = "musicgen_medium",
        string folder = MusicGenMediumFolder,
        int maxNewTokens = 512,
        int? seed = null)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: $"{folder}/decoder_model.onnx",
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Audio,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string modelDirectory = Path.Combine(ctx.ModelDirectory, folder);
                return new MusicGenModel(modelName, modelDirectory, maxNewTokens, seed);
            },
            DisplayName: "MusicGen Medium",
            Parameters: "1.5B",
            License: "CC-BY-NC-4.0",
            LicenseHolder: "Meta",
            SourceUrl: "https://huggingface.co/facebook/musicgen-medium",
            Category: "generator",
            Modalities: ["text", "audio"],
            Files:
            [
                $"{folder}/text_encoder.onnx",
                $"{folder}/decoder_model.onnx",
                $"{folder}/decoder_model.onnx_data",
                $"{folder}/decoder_with_past_model.onnx",
                $"{folder}/decoder_with_past_model.onnx_data",
                $"{folder}/build_delay_pattern_mask.onnx",
                $"{folder}/encodec_decode.onnx",
                $"{folder}/tokenizer.json",
                $"{folder}/config.json",
                $"{folder}/generation_config.json",
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
    /// Registers Florence-2 in OCR-with-region mode (fp16). Output is a
    /// single string interleaving each detected text run with four
    /// <c>&lt;loc_*&gt;</c> tokens encoding its bounding box (Florence-2's
    /// 0–999 quantized-coordinate convention). License-clean substitute for
    /// the AGPL-encumbered OmniParser detector when the consumer is an LLM
    /// that can parse the location-token stream itself; structured
    /// <c>Array&lt;Struct{text, bbox}&gt;</c> parsing is a follow-up.
    /// </summary>
    public static void RegisterFlorence2OcrWithRegion(ModelCatalog catalog) =>
        RegisterFlorence2Caption(
            catalog,
            modelName: "florence2_ocr_region",
            displayName: "Florence-2 OCR with Region (fp16)",
            taskPrompt: "<OCR_WITH_REGION>",
            folder: Florence2Fp16Folder,
            componentSuffix: "_fp16",
            licenseTag: "fp16",
            // Dense screenshots can produce many text regions, each
            // contributing text tokens + four location tokens. 1024 keeps
            // the generation budget generous without runaway latency.
            maxTokens: 1024);

    /// <summary>
    /// Default filename for the SCRFD-10G ONNX file (InsightFace's
    /// successor to RetinaFace, distributed in the <c>buffalo_l</c> model
    /// pack as <c>det_10g.onnx</c>, ~17 MB).
    /// </summary>
    public const string Scrfd10gDefaultFilename = "det_10g.onnx";

    /// <summary>
    /// Registers SCRFD-10G face detection under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"scrfd_10g"</c>).
    /// Returns one detection-array per image, where each detection is
    /// <c>Struct{label, score, x, y, w, h, landmarks: Array&lt;Struct{x, y}&gt;}</c>
    /// — <c>label</c> is the constant string <c>"face"</c> so the leading
    /// <c>(label, score, x, y, w, h)</c> tuple lines up with general object
    /// detectors like YOLOX. Sibling to YOLOX but face-specialised, with the 5 facial
    /// landmarks (eye centres, nose tip, mouth corners) that downstream
    /// face-pipeline tasks (alignment, recognition) need.
    /// </summary>
    /// <remarks>
    /// SCRFD ("Sample and Computation Redistribution for Face Detection")
    /// is the modern InsightFace successor to RetinaFace. Same general
    /// FPN-with-anchors architecture; the differences are
    /// distance-based bbox regression instead of prior-box exp/delta
    /// encoding, single-channel sigmoid scores instead of softmaxed
    /// background/foreground pairs, and a quietly-tuned anchor schedule
    /// across strides 8/16/32. This registration assumes the buffalo_l
    /// export at 640×640 input — re-exports at 320×320 or 1024×1024 will
    /// fail the K-count check at construction time.
    /// </remarks>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (the <c>X</c> in <c>models.X(image)</c>). Defaults to <c>"scrfd_10g"</c>.</param>
    /// <param name="modelFilename">ONNX filename relative to <see cref="ModelCatalog.ModelDirectory"/>. Defaults to <see cref="Scrfd10gDefaultFilename"/>.</param>
    /// <param name="confidenceThreshold">Construction-time default score threshold below which a detection is dropped pre-NMS. Per-call callers can override via the optional first arg. Defaults to 0.5 — the standard InsightFace cutoff.</param>
    /// <param name="iouThreshold">Construction-time default IoU threshold for NMS. Per-call callers can override via the optional second arg. Defaults to 0.4.</param>
    public static void RegisterScrfd10g(
        ModelCatalog catalog,
        string modelName = "scrfd_10g",
        string modelFilename = Scrfd10gDefaultFilename,
        float confidenceThreshold = 0.5f,
        float iouThreshold = 0.4f)
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
                return new ScrfdModel(modelName, modelPath, confidenceThreshold, iouThreshold);
            },
            // Per-call hyperparameter overrides:
            //   [0] confidence_threshold (Float64) — drop detections below this score pre-NMS
            //   [1] iou_threshold        (Float64) — NMS overlap threshold
            // Both are threaded through to ScrfdModel.InferBatchAsync.
            OptionalArgKinds: [DataKind.Float64, DataKind.Float64],
            DisplayName: "SCRFD-10G Face Detector",
            Parameters: "3.86M",
            License: "MIT",
            LicenseHolder: "InsightFace",
            SourceUrl: "https://github.com/deepinsight/insightface/tree/master/detection/scrfd",
            Category: "detector",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ──────────────────── PP-OCRv4 text detector (Apache-2.0) ────────────────────
    //
    // PaddleOCR's PP-OCRv4 detection model — DBNet++-style segmentation
    // emitting per-line text bounding boxes. ~5 MB ONNX. Pairs with a
    // recognizer (e.g. trocr_printed) to build a two-stage OCR pipeline.

    /// <summary>
    /// Default filename for the PP-OCRv4 detector ONNX (the
    /// <c>ch_PP-OCRv4_det.onnx</c> file shipped by the RapidOCR project).
    /// </summary>
    public const string PpOcrDetV4DefaultFilename = "ch_PP-OCRv4_det.onnx";

    /// <summary>
    /// Registers PP-OCRv4-det under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"ppocr_det_v4"</c>).
    /// Returns one detection-array per image; each element is a
    /// <c>Struct{label="text", score, x, y, w, h}</c>. Designed to be
    /// fed into a recognizer via <c>image_crop</c>:
    /// <code>
    /// SELECT models.trocr_printed_fp16(
    ///          image_crop(photo, det.x, det.y, det.w, det.h)) AS line
    /// FROM receipts
    /// CROSS APPLY UNNEST(models.ppocr_det_v4(photo)) AS det
    /// </code>
    /// </summary>
    /// <remarks>
    /// Upstream is PaddlePaddle's
    /// <a href="https://github.com/PaddlePaddle/PaddleOCR">PaddleOCR</a>;
    /// the practical download is the
    /// <a href="https://github.com/RapidAI/RapidOCR/releases">RapidOCR releases page</a>
    /// which mirrors the official ONNX export. Apache-2.0 — fully
    /// unencumbered for commercial use.
    /// </remarks>
    public static void RegisterPpOcrDetV4(
        ModelCatalog catalog,
        string modelName = "ppocr_det_v4",
        string modelFilename = PpOcrDetV4DefaultFilename,
        float pixelThreshold = 0.3f,
        float boxScoreThreshold = 0.6f,
        float unclipRatio = 1.5f)
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
                return new PpOcrDetectionModel(
                    modelName, modelPath,
                    pixelThreshold: pixelThreshold,
                    boxScoreThreshold: boxScoreThreshold,
                    unclipRatio: unclipRatio);
            },
            // Per-call hyperparameter overrides:
            //   [0] pixel_threshold     (Float64) — per-pixel sigmoid threshold
            //   [1] box_score_threshold (Float64) — per-region mean-prob threshold
            //   [2] unclip_ratio        (Float64) — DBNet polygon-offset ratio
            OptionalArgKinds: [DataKind.Float64, DataKind.Float64, DataKind.Float64],
            DisplayName: "PP-OCRv4 Text Detector",
            Parameters: "4.7M",
            License: "Apache-2.0",
            LicenseHolder: "PaddlePaddle",
            SourceUrl: "https://github.com/PaddlePaddle/PaddleOCR",
            Category: "detector",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ──────────────────── Real-ESRGAN-General x4 (BSD-3) ────────────────────
    //
    // Xintao Wang's Real-ESRGAN-Compact (SRVGGNet) general-content variant.
    // 4× super-resolution, ~10 MB ONNX. Trained on real-world degradations
    // (not anime); the lightweight Compact backbone keeps it fast enough
    // to run per-row in a SQL pipeline without tiling for typical photos.

    /// <summary>
    /// Default filename for Real-ESRGAN-General x4 (the v3 single-input
    /// ONNX export from the OwlMaster/AllFilesRope HuggingFace mirror).
    /// </summary>
    public const string RealesrganGeneralX4DefaultFilename = "realesr-general-x4v3.onnx";

    /// <summary>
    /// Registers Real-ESRGAN-General x4 under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"realesrgan_general_x4"</c>).
    /// Image-in / image-out: PNG-encoded 4× upscale of the input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Upstream is <a href="https://github.com/xinntao/Real-ESRGAN">xinntao/Real-ESRGAN</a>;
    /// xinntao only ships <c>.pth</c> weights and a PyTorch conversion script,
    /// so the practical download is the OwlMaster/AllFilesRope HuggingFace mirror
    /// at <c>https://huggingface.co/OwlMaster/AllFilesRope/blob/main/realesr-general-x4v3.onnx</c>.
    /// </para>
    /// <para>
    /// V1 runs whole-image inference. Memory cost scales with the output
    /// resolution: a 1024×1024 input at 4× costs ~210 MB of intermediate
    /// floats. Tile-based inference is the right follow-up for high-res
    /// inputs; not implemented yet.
    /// </para>
    /// </remarks>
    public static void RegisterRealesrganGeneralX4(
        ModelCatalog catalog,
        string modelName = "realesrgan_general_x4",
        string modelFilename = RealesrganGeneralX4DefaultFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new SuperResolutionModel(modelName, modelPath, scaleFactor: 4);
            },
            // Per-call hyperparameter override:
            //   [0] outscale (Float64) — output scale relative to input.
            //   Defaults to the native 4× scale. Valid range [1.0, 4.0];
            //   values above 4 are rejected (the model can't produce more
            //   pixels than its architecture supports).
            OptionalArgKinds: [DataKind.Float64],
            DisplayName: "Real-ESRGAN General x4 (Compact)",
            Parameters: "1.2M",
            License: "BSD-3-Clause",
            LicenseHolder: "Xintao Wang",
            SourceUrl: "https://github.com/xinntao/Real-ESRGAN",
            Category: "enhancer",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ─────────────────────────── U²-Net (Apache-2.0) ──────────────────────────
    //
    // Xuebin Qin's U²-Net salient-object detector. Two sibling exports from
    // the upstream xuebinqin/U-2-Net repo:
    //
    //   • u2net.onnx   — full 176M-param network (~170 MB)
    //   • u2netp.onnx  — distilled lite variant, 4.7M params (~4.7 MB)
    //
    // Both share the same input shape (320×320 RGB), the same seven-output
    // deep-supervision head, and ImageNet-style normalisation, so a single
    // U2NetModel class loads either file. The "p" (lite) variant trades a
    // small amount of mask precision for a >35× size cut and matching speed
    // gain — the right default for interactive demos and the right starting
    // point if mask quality turns out to be sufficient.
    //
    // Output is a single-channel saliency mask sized to the input. Cutting
    // the foreground out of the source image is deferred to a follow-up
    // `cutout(image, mask)` scalar function — keeping the model class focused
    // on inference matches how YOLOX (boxes) and SCRFD (faces) ship.

    /// <summary>Default filename for U²-Net full (~170 MB).</summary>
    public const string U2NetDefaultFilename = "u2net.onnx";

    /// <summary>Default filename for U²-Net lite (u2netp, ~4.7 MB).</summary>
    public const string U2NetpDefaultFilename = "u2netp.onnx";

    /// <summary>
    /// Registers full U²-Net under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"u2net"</c>). Image-in / image-out: a single-channel
    /// saliency mask resized to match the input.
    /// </summary>
    /// <remarks>
    /// Upstream is <a href="https://github.com/xuebinqin/U-2-Net">xuebinqin/U-2-Net</a>.
    /// Pre-built ONNX exports are widely mirrored on HuggingFace; the canonical
    /// filename is <c>u2net.onnx</c>.
    /// </remarks>
    public static void RegisterU2Net(
        ModelCatalog catalog,
        string modelName = "u2net",
        string modelFilename = U2NetDefaultFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new U2NetModel(modelName, modelPath);
            },
            DisplayName: "U²-Net (salient object segmentation)",
            Parameters: "176M",
            License: "Apache-2.0",
            LicenseHolder: "Xuebin Qin et al.",
            SourceUrl: "https://github.com/xuebinqin/U-2-Net",
            Category: "segmenter",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    /// <summary>
    /// Registers lite U²-Net (u2netp) under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"u2netp"</c>). Same image-in / image-out signature as
    /// <see cref="RegisterU2Net"/>; ~35× smaller weights at the cost of some
    /// mask precision on fine boundaries.
    /// </summary>
    public static void RegisterU2Netp(
        ModelCatalog catalog,
        string modelName = "u2netp",
        string modelFilename = U2NetpDefaultFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new U2NetModel(modelName, modelPath);
            },
            DisplayName: "U²-Net Lite / u2netp (salient object segmentation)",
            Parameters: "4.7M",
            License: "Apache-2.0",
            LicenseHolder: "Xuebin Qin et al.",
            SourceUrl: "https://github.com/xuebinqin/U-2-Net",
            Category: "segmenter",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ───────────────────── MiDaS / DPT depth (MIT) ──────────────────────────
    //
    // Intel ISL's monocular depth-estimation family. Two registrations driven
    // by the same DepthEstimationModel class:
    //   • midas_v21_small_256.onnx — MiDaS v2.1 small (~21M params, 256×256, BGR + ImageNet)
    //   • dpt_large_384.onnx       — DPT-Large v3.1   (~344M params, 384×384, RGB + 0.5/0.5)
    //
    // Output is a single-channel relative depth map sized to match the input
    // image. Inverse depth — bigger value = closer — min-max normalised per
    // image; absolute scale is discarded.

    /// <summary>Default filename for MiDaS v2.1 small (~21M params, 256×256 input).</summary>
    public const string MidasSmall256Filename = "midas_v21_small_256.onnx";

    /// <summary>Default filename for DPT-Large v3.1 (~344M params, 384×384 input).</summary>
    public const string DptLarge384Filename = "dpt_large_384.onnx";

    /// <summary>
    /// Registers Intel MiDaS v2.1 small under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"midas_small"</c>). Image-in / image-out: a single-channel
    /// relative depth map sized to match the input.
    /// </summary>
    /// <remarks>
    /// MiDaS-small v2.1 expects BGR input with ImageNet normalisation. Lightweight
    /// (21M params, ~83 MB) and fast enough to run per-row in a SQL pipeline.
    /// Upstream: <a href="https://github.com/isl-org/MiDaS">isl-org/MiDaS</a>.
    /// </remarks>
    public static void RegisterMidasSmall(
        ModelCatalog catalog,
        string modelName = "midas_small",
        string modelFilename = MidasSmall256Filename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new DepthEstimationModel(
                    name: modelName,
                    modelFilePath: modelPath,
                    inputSize: 256,
                    bgr: true,
                    channelMean: [0.485f, 0.456f, 0.406f],
                    channelStd: [0.229f, 0.224f, 0.225f]);
            },
            DisplayName: "MiDaS v2.1 small (relative depth)",
            Parameters: "21M",
            License: "MIT",
            LicenseHolder: "Intel ISL",
            SourceUrl: "https://github.com/isl-org/MiDaS",
            Category: "depth",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    /// <summary>
    /// Registers Intel DPT-Large under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"dpt_large"</c>). Same image-in / image-out signature as
    /// <see cref="RegisterMidasSmall"/>; substantially more accurate at the cost
    /// of ~16× more parameters (344M, ~1.4 GB).
    /// </summary>
    /// <remarks>
    /// DPT-Large expects RGB input normalised to <c>[-1, 1]</c>
    /// (<c>mean=[0.5,0.5,0.5]</c>, <c>std=[0.5,0.5,0.5]</c>) at 384×384.
    /// Upstream: <a href="https://github.com/isl-org/MiDaS">isl-org/MiDaS</a>.
    /// </remarks>
    public static void RegisterDptLarge(
        ModelCatalog catalog,
        string modelName = "dpt_large",
        string modelFilename = DptLarge384Filename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: modelFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string modelPath = Path.Combine(ctx.ModelDirectory, modelFilename);
                return new DepthEstimationModel(
                    name: modelName,
                    modelFilePath: modelPath,
                    inputSize: 384,
                    bgr: false,
                    channelMean: [0.5f, 0.5f, 0.5f],
                    channelStd: [0.5f, 0.5f, 0.5f]);
            },
            DisplayName: "DPT-Large v3.1 (relative depth)",
            Parameters: "344M",
            License: "MIT",
            LicenseHolder: "Intel ISL",
            SourceUrl: "https://github.com/isl-org/MiDaS",
            Category: "depth",
            Modalities: ["image"],
            Files: [modelFilename]));
    }

    // ─────────────────────── MobileSAM prompted (Apache-2.0) ─────────────────
    //
    // Meta's Segment Anything (TinyViT distillation) wrapped as a
    // prompted-segmentation model: input (image, x, y) → output Image
    // (binary mask). Two ONNX files from the vietanhdev/samexporter
    // pipeline:
    //   • mobile_sam_image_encoder.onnx   — TinyViT encoder, ~28 MB
    //   • sam_mask_decoder_multi.onnx     — multi-mask prompt decoder, ~16 MB
    //                                       (single-mask variant also accepted)
    //
    // The encoder graph bakes in resize-longest-to-1024, ImageNet
    // normalisation, and zero-pad; the decoder graph bakes in the
    // mask-resize-back to the original image dims via orig_im_size. We
    // pack pixels and pick the highest-IoU mask — everything else lives
    // in the ONNX files.

    /// <summary>Default filename for the MobileSAM (TinyViT) image encoder.</summary>
    public const string MobileSamEncoderFilename = "mobile_sam_image_encoder.onnx";

    /// <summary>
    /// Default filename for the multi-mask prompt decoder. Emits a small
    /// number of candidate masks per prompt with a per-mask predicted-IoU
    /// score; the model wrapper picks the highest-scoring one.
    /// </summary>
    public const string MobileSamMaskDecoderMultiFilename = "sam_mask_decoder_multi.onnx";

    /// <summary>
    /// Single-mask decoder variant. Produces one mask + one IoU score per
    /// prompt. Either decoder file works with <see cref="MobileSamModel"/>;
    /// the multi variant gives slightly better quality at no meaningful
    /// extra runtime cost, so it's the default registration.
    /// </summary>
    public const string MobileSamMaskDecoderSingleFilename = "sam_mask_decoder_single.onnx";

    /// <summary>
    /// Registers MobileSAM prompted segmentation under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"mobilesam_prompted"</c>).
    /// SQL surface: <c>models.mobilesam_prompted(image, x, y) → Image</c>
    /// — a binary foreground mask sized to match the input image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Coordinates <c>x</c> / <c>y</c> are in original-image pixel space:
    /// <c>(0, 0)</c> is the top-left corner; <c>x</c> grows to the right,
    /// <c>y</c> down. The decoder's <c>orig_im_size</c> input handles the
    /// internal 1024-space rescaling.
    /// </para>
    /// <para>
    /// Output mask: 0 = background, 255 = foreground, written as RGBA with
    /// equal channels (matches every other image-emitting model so
    /// downstream consumers don't branch on colour type).
    /// </para>
    /// <para>
    /// Upstream encoder distillation: <a href="https://github.com/ChaoningZhang/MobileSAM">ChaoningZhang/MobileSAM</a>.
    /// ONNX export pipeline: <a href="https://github.com/vietanhdev/samexporter">vietanhdev/samexporter</a>.
    /// </para>
    /// </remarks>
    public static void RegisterMobileSamPrompted(
        ModelCatalog catalog,
        string modelName = "mobilesam_prompted",
        string encoderFilename = MobileSamEncoderFilename,
        string decoderFilename = MobileSamMaskDecoderMultiFilename)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderFilename,
            InputKinds: [DataKind.Image, DataKind.Float64, DataKind.Float64],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderFilename);
                string decoderPath = Path.Combine(ctx.ModelDirectory, decoderFilename);
                return new MobileSamModel(modelName, encoderPath, decoderPath, MobileSamMode.Prompted);
            },
            DisplayName: "MobileSAM (prompted segmentation)",
            Parameters: "9.7M",
            License: "Apache-2.0",
            LicenseHolder: "Meta AI / Kyung Hee University",
            SourceUrl: "https://github.com/ChaoningZhang/MobileSAM",
            Category: "segmenter",
            Modalities: ["image"],
            Files: [encoderFilename, decoderFilename]));
    }

    /// <summary>
    /// Registers MobileSAM "everything" segmentation under the catalog
    /// name <paramref name="modelName"/> (defaults to <c>"mobilesam"</c>).
    /// SQL surface: <c>models.mobilesam(image, [gridSize]) →
    /// Array&lt;Image&gt;</c> — one binary mask per object the model
    /// finds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Sweeps a <c>gridSize × gridSize</c> grid of foreground prompts
    /// across the image (<c>32 × 32 = 1024</c> prompts at the default),
    /// runs the decoder per prompt, drops candidates with low predicted
    /// IoU or low stability, and NMS-deduplicates the survivors.
    /// </para>
    /// <para>
    /// <strong>Cost.</strong> Encoder runs once per image; decoder runs
    /// <c>gridSize²</c> times. ~3-5 seconds per image on CPU at the
    /// default. Pass a smaller grid (<c>models.mobilesam(image, 16)</c>)
    /// for ~4× faster batches at the cost of missing small objects.
    /// </para>
    /// </remarks>
    public static void RegisterMobileSam(
        ModelCatalog catalog,
        string modelName = "mobilesam",
        string encoderFilename = MobileSamEncoderFilename,
        string decoderFilename = MobileSamMaskDecoderMultiFilename,
        int defaultGridSize = 32)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderFilename,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderFilename);
                string decoderPath = Path.Combine(ctx.ModelDirectory, decoderFilename);
                return new MobileSamModel(
                    modelName, encoderPath, decoderPath,
                    MobileSamMode.Everything, defaultGridSize);
            },
            // Per-call hyperparameter overrides:
            //   [0] gridSize (Int32) — grid side length for the prompt
            //   sweep; the model does gridSize² decoder dispatches per row.
            OptionalArgKinds: [DataKind.Int32],
            DisplayName: "MobileSAM (everything segmentation)",
            Parameters: "9.7M",
            License: "Apache-2.0",
            LicenseHolder: "Meta AI / Kyung Hee University",
            SourceUrl: "https://github.com/ChaoningZhang/MobileSAM",
            Category: "segmenter",
            Modalities: ["image"],
            Files: [encoderFilename, decoderFilename]));
    }

    // ────────────────────────── YOLOX (Apache-2.0) ──────────────────────────
    //
    // Megvii's YOLOX detector family — license-clean object detector.
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
    /// Common backbone for the seven YOLOX size registrations. EachS
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
            // Optional thresholding shape (forward-compat; not yet wired
            // into per-call overrides).
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

    // ────────────────────── Python-backed models (TTS / music gen / cloning) ──────────────────────
    //
    // These wrap a Python subprocess via PythonBackedModel — the experimentation
    // path for HuggingFace transformers pipelines whose .pth/.bin weights would
    // be a heavy lift to convert to ONNX. Each requires the user to create a
    // per-model venv with the upstream library installed; setup scripts under
    // scripts/ automate the venv + pip install + (where applicable) model file
    // download. Worker scripts ship with the engine in the python/ output
    // folder — users don't drop their own.
    //
    // The catalog uses .venv-{name}/pyvenv.cfg as the discoverability anchor
    // for these entries: if the venv exists, system_models reports
    // status=bridge ("set up; runnable as long as pip packages are intact");
    // otherwise status=missing ("run scripts/setup-{name}-venv.ps1").

    /// <summary>Default filename for the Bark Small Python worker script,
    /// shipped in the engine's <c>python/</c> output folder.</summary>
    public const string BarkSmallWorkerFilename = "bark_worker.py";

    /// <summary>
    /// Catalog anchor file for the Bark venv — written by <c>python -m venv</c>
    /// and present whenever the venv exists. We use this as <c>RelativePath</c>
    /// rather than a model file because Bark's weights live in the HF cache,
    /// not in <c>$DATUM_MODELS</c>; the venv is the closest proxy for "set up".
    /// </summary>
    public const string BarkSmallVenvAnchor = ".venv-bark/pyvenv.cfg";

    /// <summary>Default filename for the Kokoro-82M ONNX model file.</summary>
    public const string Kokoro82MOnnxDefaultFilename = "kokoro-v1.0.onnx";

    /// <summary>
    /// Default filename for the Kokoro voices bundle. The
    /// <c>kokoro-onnx</c> package accepts either a single bundled
    /// <c>voices-v1.0.bin</c> (~26 MB containing all voices) or a path to
    /// a directory of per-voice <c>.bin</c> files; pass either path
    /// through <c>voicesPath</c> on registration.
    /// </summary>
    public const string Kokoro82MVoicesDefaultFilename = "voices-v1.0.bin";

    /// <summary>
    /// Default filename for the Kokoro Python worker script. Shipped in
    /// the engine's <c>python/</c> output folder; the loader resolves it
    /// relative to <see cref="AppContext.BaseDirectory"/> so users don't
    /// have to drop their own copy.
    /// </summary>
    public const string Kokoro82MWorkerFilename = "kokoro_worker.py";

    /// <summary>
    /// Registers Suno's Bark Small TTS model under the catalog name
    /// <paramref name="modelName"/> (defaults to <c>"bark_small"</c>) via a
    /// Python subprocess. Bark generates speech with embedded sound effects
    /// and music notes — write <c>[laughs]</c> or <c>[sighs]</c> in the prompt
    /// and Bark renders them inline. The worker emits 24kHz WAV bytes; the
    /// engine carries them as <see cref="DataKind.Image"/> until a proper
    /// <c>DataKind.Audio</c> lands.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Setup.</strong> Run <c>scripts/setup-bark-venv.ps1</c> from
    /// the repo root — it creates the venv at <c>$DATUM_MODELS/.venv-bark</c>,
    /// installs <c>transformers</c> + <c>torch</c> (CUDA wheel by default) +
    /// <c>scipy</c>, and is idempotent (rerun-safe). The Bark model weights
    /// download from HuggingFace on the first inference call.
    /// </para>
    /// <para>
    /// <strong>Worker script.</strong> Ships with the engine at
    /// <c>{AppContext.BaseDirectory}/python/bark_worker.py</c>. The
    /// <paramref name="scriptPath"/> parameter overrides this for users who
    /// want a customised pipeline (different sampling, voice presets, etc.).
    /// </para>
    /// <para>
    /// <strong>Determinism.</strong> Bark samples internally and is therefore
    /// nondeterministic — repeated identical prompts produce different audio
    /// each time, which is part of the appeal for narrative work.
    /// </para>
    /// </remarks>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (the <c>X</c> in <c>models.X(text)</c>).</param>
    /// <param name="scriptPath">
    /// Absolute path to the Bark Python worker. <see langword="null"/>
    /// resolves to the engine-bundled script at
    /// <c>{AppContext.BaseDirectory}/python/{BarkSmallWorkerFilename}</c>.
    /// </param>
    /// <param name="pythonExecutable">
    /// Absolute path to the Python interpreter to spawn (typically a venv-
    /// scoped <c>python.exe</c>). <see langword="null"/> auto-detects the
    /// conventional <c>{ModelDirectory}/.venv-bark/</c> venv, falling back
    /// to the <c>DATUM_PYTHON</c> env var or <c>python</c> on PATH.
    /// </param>
    /// <param name="readyTimeoutSeconds">
    /// How long to wait for the worker to print the ready handshake. Bark
    /// loads in ~10-30s warm-cache, longer on first run when the model
    /// downloads from HuggingFace.
    /// </param>
    public static void RegisterBarkSmall(
        ModelCatalog catalog,
        string modelName = "bark_small",
        string? scriptPath = null,
        string? pythonExecutable = null,
        int readyTimeoutSeconds = 180)
        => RegisterBarkVariant(
            catalog, modelName, scriptPath, pythonExecutable, readyTimeoutSeconds,
            huggingFaceModelId: "suno/bark-small",
            displayName: "Bark Small (TTS, Python-backed)",
            parameters: "~100M",
            sourceUrl: "https://huggingface.co/suno/bark-small");

    /// <summary>
    /// Registers Suno's full Bark TTS model (~3.5 GB weights, higher
    /// quality than Bark Small). Same Python venv (<c>.venv-bark</c>),
    /// same worker script, same per-call overrides — only the
    /// HuggingFace model ID differs. Inference is ~3-4× slower than
    /// Bark Small but the speech quality is noticeably more natural.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Setup.</strong> Same as Bark Small — run
    /// <c>scripts/setup-bark-venv.ps1</c> once. The full Bark model
    /// downloads from HuggingFace into <c>~/.cache/huggingface/</c>
    /// on the first inference call (~3.5 GB, one-time).
    /// </para>
    /// </remarks>
    public static void RegisterBark(
        ModelCatalog catalog,
        string modelName = "bark",
        string? scriptPath = null,
        string? pythonExecutable = null,
        int readyTimeoutSeconds = 240)
        => RegisterBarkVariant(
            catalog, modelName, scriptPath, pythonExecutable, readyTimeoutSeconds,
            huggingFaceModelId: "suno/bark",
            displayName: "Bark (TTS, Python-backed)",
            parameters: "~700M",
            sourceUrl: "https://huggingface.co/suno/bark");

    /// <summary>
    /// Common backbone for the Bark size variants. Both registrations
    /// share the same venv (<c>.venv-bark</c>) and the same worker
    /// script (<c>bark_worker.py</c>); only the HuggingFace model ID
    /// changes. Both report status against the same <c>pyvenv.cfg</c>
    /// anchor — if the venv is set up, both variants are runnable.
    /// </summary>
    private static void RegisterBarkVariant(
        ModelCatalog catalog,
        string modelName,
        string? scriptPath,
        string? pythonExecutable,
        int readyTimeoutSeconds,
        string huggingFaceModelId,
        string displayName,
        string parameters,
        string sourceUrl)
    {
        string resolvedScriptPath = scriptPath
            ?? Path.Combine(AppContext.BaseDirectory, "python", BarkSmallWorkerFilename);

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "python",
            // Anchor on the venv marker rather than a model file: Bark's
            // weights live in the HF cache (not $DATUM_MODELS), so the venv
            // is the best proxy the catalog can stat for "set up".
            RelativePath: BarkSmallVenvAnchor,
            InputKinds: [DataKind.String],
            // PCM_16 mono WAV bytes at the model's configured sample rate
            // (24kHz for both bark and bark-small).
            OutputKind: DataKind.Audio,
            IsDeterministic: false,
            Loader: ctx =>
            {
                string? py = pythonExecutable
                    ?? ResolveVenvPython(ctx.ModelDirectory, ".venv-bark");
                return new PythonBackedModel(
                    name: modelName,
                    inputKinds: [DataKind.String],
                    outputKind: DataKind.Audio,
                    isDeterministic: false,
                    scriptPath: resolvedScriptPath,
                    pythonExecutable: py,
                    scriptArgs: ["--model-id", huggingFaceModelId],
                    readyTimeout: TimeSpan.FromSeconds(readyTimeoutSeconds),
                    // PreferredBatchSize=1 streams each clip as soon as it
                    // finishes, matching SDXL-Turbo's interactive cadence.
                    preferredBatchSize: 1);
            },
            // Per-call optional positional args:
            //   [0] voice_preset (String)  - e.g. 'v2/en_speaker_9'
            // Worker pins v2/en_speaker_6 by default; without a preset
            // Bark picks randomly per call, sometimes producing unhinged
            // speakers. See the Bark speaker library for the full list.
            OptionalArgKinds: [DataKind.String],
            DisplayName: displayName,
            Parameters: parameters,
            License: "MIT",
            LicenseHolder: "Suno",
            SourceUrl: sourceUrl,
            Category: "tts",
            Modalities: ["text", "audio"],
            // The venv anchor is what we track for status. The HF model
            // cache is out of band — catalog can't see it, but its absence
            // surfaces as a clear PythonProcessException on first invocation.
            Files: [BarkSmallVenvAnchor]));
    }

    // ─────────────────────────── Whisper STT ──────────────────────────────
    //
    // OpenAI Whisper as native ONNX. Each size variant is a separate
    // registration pointing at its own optimum-cli output folder. Same
    // architectural shape as ViT-GPT2 (encoder + autoregressive decoder),
    // plus a Slaney mel-spectrogram pipeline for the audio side. MIT
    // license, fully unencumbered.

    /// <summary>Default folder for Whisper Tiny (~78 MB encoder + 250 MB decoder).</summary>
    public const string WhisperTinyFolder = "whisper-tiny-onnx";

    /// <summary>Default folder for Whisper Base (~78 MB encoder + 300 MB decoder).</summary>
    public const string WhisperBaseFolder = "whisper-base-onnx";

    /// <summary>Default folder for Whisper Small (~280 MB encoder + 1.1 GB decoder).</summary>
    public const string WhisperSmallFolder = "whisper-small-onnx";

    /// <summary>Default folder for Whisper Medium (~870 MB encoder + 2.5 GB decoder).</summary>
    public const string WhisperMediumFolder = "whisper-medium-onnx";

    /// <summary>
    /// Files needed for any Whisper install (relative to its variant
    /// folder). Tracks both ONNX components plus tokenizer + configs so
    /// <c>system.models</c> reports missing pieces accurately.
    /// </summary>
    private static IReadOnlyList<string> WhisperFiles(string folder) =>
    [
        $"{folder}/encoder_model.onnx",
        $"{folder}/decoder_model.onnx",
        $"{folder}/vocab.json",
        $"{folder}/merges.txt",
        $"{folder}/tokenizer.json",
        $"{folder}/preprocessor_config.json",
        $"{folder}/generation_config.json",
        $"{folder}/special_tokens_map.json",
    ];

    /// <summary>
    /// Common backbone for the four Whisper size registrations. Each
    /// public <c>RegisterWhisper*</c> wraps this with size-specific
    /// folder + display metadata.
    /// </summary>
    private static void RegisterWhisperVariant(
        ModelCatalog catalog,
        string modelName,
        string folder,
        string displayName,
        string parameters,
        int maxTokens)
    {
        string encoderRelativePath = $"{folder}/encoder_model.onnx";

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderRelativePath,
            InputKinds: [DataKind.Audio],
            OutputKind: DataKind.String,
            // Greedy decoding from a fixed prefix → reproducible.
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderRelativePath);
                return new WhisperOnnxModel(modelName, encoderPath, maxTokens);
            },
            DisplayName: displayName,
            Parameters: parameters,
            License: "MIT",
            LicenseHolder: "OpenAI",
            SourceUrl: "https://huggingface.co/openai/whisper-base",
            Category: "stt",
            Modalities: ["audio", "text"],
            Files: WhisperFiles(folder)));
    }

    /// <summary>Registers Whisper Tiny — fastest STT, lowest accuracy. ~39M params.</summary>
    public static void RegisterWhisperTiny(ModelCatalog catalog, string modelName = "whisper_tiny")
        => RegisterWhisperVariant(catalog, modelName, WhisperTinyFolder,
            displayName: "Whisper Tiny", parameters: "39M", maxTokens: 224);

    /// <summary>Registers Whisper Base — balanced STT. ~74M params.</summary>
    public static void RegisterWhisperBase(ModelCatalog catalog, string modelName = "whisper_base")
        => RegisterWhisperVariant(catalog, modelName, WhisperBaseFolder,
            displayName: "Whisper Base", parameters: "74M", maxTokens: 224);

    /// <summary>Registers Whisper Small — better accuracy, modest cost. ~244M params.</summary>
    public static void RegisterWhisperSmall(ModelCatalog catalog, string modelName = "whisper_small")
        => RegisterWhisperVariant(catalog, modelName, WhisperSmallFolder,
            displayName: "Whisper Small", parameters: "244M", maxTokens: 224);

    /// <summary>Registers Whisper Medium — strong STT, slower. ~769M params.</summary>
    public static void RegisterWhisperMedium(ModelCatalog catalog, string modelName = "whisper_medium")
        => RegisterWhisperVariant(catalog, modelName, WhisperMediumFolder,
            displayName: "Whisper Medium", parameters: "769M", maxTokens: 224);

    // ───────────────────────── PaliGemma 2 captioner ─────────────────────────
    //
    // Google's vision-language model: SigLIP encoder + Gemma 2B decoder
    // with a learned linear projector. The "mix" variants are pre-finetuned
    // on captioning + VQA + OCR so they handle generic prompts well. We
    // register at 448x448 by default (better fine-detail than 224); the
    // 224 variant is a separate registration for cheaper iteration.

    /// <summary>Default folder for PaliGemma 2 mix-224 (faster, less detail).</summary>
    public const string PaliGemma2Mix224Folder = "paligemma2-3b-mix-224-onnx";

    /// <summary>Default folder for PaliGemma 2 mix-448 (slower, fine-detail aware).</summary>
    public const string PaliGemma2Mix448Folder = "paligemma2-3b-mix-448-onnx";

    /// <summary>
    /// Files needed for any PaliGemma 2 install (relative to its variant
    /// folder). Multi-file model — vision encoder + token embedder +
    /// autoregressive decoder + tokenizer.
    /// </summary>
    private static IReadOnlyList<string> PaliGemma2Files(string folder) =>
    [
        $"{folder}/vision_encoder.onnx",
        $"{folder}/embed_tokens.onnx",
        $"{folder}/decoder_model.onnx",
        // optimum may also produce decoder_model_merged.onnx /
        // decoder_with_past_model.onnx; we only need decoder_model.onnx
        // for the no-cache path. Skip them in the file list to keep
        // status checks clean.
        $"{folder}/vocab.json",
        $"{folder}/merges.txt",
        $"{folder}/tokenizer.json",
        $"{folder}/config.json",
    ];

    /// <summary>
    /// Common backbone for the PaliGemma 2 size variants. Each public
    /// <c>RegisterPaliGemma2*</c> wraps this with size-specific
    /// folder + display metadata.
    /// </summary>
    private static void RegisterPaliGemma2Variant(
        ModelCatalog catalog,
        string modelName,
        string folder,
        string displayName,
        string defaultPrompt,
        int maxTokens)
    {
        string visionEncoderRelativePath = $"{folder}/vision_encoder.onnx";

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: visionEncoderRelativePath,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.String,
            // Greedy decoding from a fixed prefix → reproducible.
            IsDeterministic: true,
            Loader: ctx =>
            {
                string visionEncoderPath = Path.Combine(ctx.ModelDirectory, visionEncoderRelativePath);
                return new PaliGemmaModel(modelName, visionEncoderPath, defaultPrompt, maxTokens);
            },
            DisplayName: displayName,
            Parameters: "3B",
            License: "Gemma Terms",
            LicenseHolder: "Google",
            SourceUrl: "https://huggingface.co/google/paligemma2-3b-mix-448",
            Category: "captioner",
            Modalities: ["image", "text"],
            Files: PaliGemma2Files(folder)));
    }

    /// <summary>
    /// Registers PaliGemma 2 mix-224 — faster captioner (256 image tokens
    /// vs 1024 for the 448 variant). Use for cheaper iteration; the 448
    /// variant is the better default for fine-detail scene art.
    /// </summary>
    public static void RegisterPaliGemma2Mix224(
        ModelCatalog catalog,
        string modelName = "paligemma2_224",
        string defaultPrompt = "caption en",
        int maxTokens = 100)
        => RegisterPaliGemma2Variant(catalog, modelName, PaliGemma2Mix224Folder,
            displayName: "PaliGemma 2 mix-224 (Captioner)",
            defaultPrompt: defaultPrompt,
            maxTokens: maxTokens);

    /// <summary>
    /// Registers PaliGemma 2 mix-448 — high-detail captioner. 1024 image
    /// tokens give noticeably better fine-detail recognition than the 224
    /// variant; ~3-4× slower per call. Default for D&amp;D scene art.
    /// </summary>
    public static void RegisterPaliGemma2Mix448(
        ModelCatalog catalog,
        string modelName = "paligemma2_448",
        string defaultPrompt = "caption en",
        int maxTokens = 100)
        => RegisterPaliGemma2Variant(catalog, modelName, PaliGemma2Mix448Folder,
            displayName: "PaliGemma 2 mix-448 (Captioner)",
            defaultPrompt: defaultPrompt,
            maxTokens: maxTokens);

    /// <summary>Default folder for the Xenova / onnx-community Moondream2 export.</summary>
    public const string Moondream2Folder = "moondream2-onnx";

    /// <summary>
    /// File-existence anchor for Moondream2: the fp16 vision encoder.
    /// The model loader resolves embed_tokens, the merged decoder, and
    /// tokenizer files relative to this file.
    /// </summary>
    public const string Moondream2VisionAnchor = Moondream2Folder + "/onnx/vision_encoder_fp16.onnx";

    /// <summary>
    /// Files needed for any Moondream2 install (relative to the model
    /// directory). Tokenizer files live at the export root; the three
    /// ONNX files live in the <c>onnx/</c> subfolder.
    /// </summary>
    private static IReadOnlyList<string> Moondream2Files() =>
    [
        Moondream2Folder + "/onnx/vision_encoder_fp16.onnx",
        Moondream2Folder + "/onnx/embed_tokens_fp16.onnx",
        Moondream2Folder + "/onnx/decoder_model_merged_fp16.onnx",
        Moondream2Folder + "/onnx/decoder_model_merged_fp16.onnx_data",
        Moondream2Folder + "/vocab.json",
        Moondream2Folder + "/merges.txt",
        Moondream2Folder + "/tokenizer.json",
        Moondream2Folder + "/config.json",
        Moondream2Folder + "/preprocessor_config.json",
    ];

    /// <summary>Default subfolder for the Microsoft GenAI-format Phi-3.5-vision GPU build.</summary>
    public const string Phi35VisionGpuSubfolder = "phi35-vision-onnx/gpu/gpu-int4-rtn-block-32";

    /// <summary>
    /// File-existence anchor for Phi-3.5-vision: the <c>genai_config.json</c>
    /// inside the chosen variant subfolder. ORT GenAI loads the directory
    /// as a single unit and resolves the three ONNX bundles + tokenizer
    /// from the bundled config.
    /// </summary>
    public const string Phi35VisionAnchor = Phi35VisionGpuSubfolder + "/genai_config.json";

    /// <summary>
    /// Files needed for any Phi-3.5-vision install (relative to the
    /// model directory). The three <c>.onnx</c> sidecars (<c>.data</c>)
    /// hold the bulk of the weights; the JSON files are config /
    /// tokenizer.
    /// </summary>
    private static IReadOnlyList<string> Phi35VisionFiles() =>
    [
        Phi35VisionGpuSubfolder + "/genai_config.json",
        Phi35VisionGpuSubfolder + "/processor_config.json",
        Phi35VisionGpuSubfolder + "/tokenizer.json",
        Phi35VisionGpuSubfolder + "/tokenizer_config.json",
        Phi35VisionGpuSubfolder + "/special_tokens_map.json",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-vision.onnx",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-vision.onnx.data",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-embedding.onnx",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-embedding.onnx.data",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-text.onnx",
        Phi35VisionGpuSubfolder + "/phi-3.5-v-instruct-text.onnx.data",
    ];

    /// <summary>
    /// Registers Microsoft Phi-3.5-vision via the ORT GenAI managed
    /// runtime. SQL surface:
    /// <c>models.phi35_vision(image, prompt) → string</c>. Anchors on
    /// the int4-quantized GPU build's <c>genai_config.json</c>; ORT
    /// GenAI handles vision encoder + embedding + decoder orchestration,
    /// IO binding, and KV-cache management internally — dramatically
    /// faster than hand-rolled ORT for decoder-only generation.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name. Defaults to <c>"phi35_vision"</c>.</param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per prompt. 256 is generous for
    /// descriptions and short Q&amp;A; raise for long-form summarisation.
    /// </param>
    public static void RegisterPhi35Vision(
        ModelCatalog catalog,
        string modelName = "phi35_vision",
        int maxTokens = 256)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            // Distinct from "onnx" so system_models can render the runtime
            // boundary (ORT GenAI vs raw ORT) for users debugging.
            Backend: "onnx_genai",
            RelativePath: Phi35VisionAnchor,
            InputKinds: [DataKind.Image, DataKind.String],
            OutputKind: DataKind.String,
            // genai_config.json pins do_sample=false + top_k=1 — pure greedy.
            IsDeterministic: true,
            Loader: ctx =>
            {
                string anchorPath = Path.Combine(ctx.ModelDirectory, Phi35VisionAnchor);
                string bundleDirectory = Path.GetDirectoryName(anchorPath)!;
                return new Phi35VisionModel(modelName, bundleDirectory, maxTokens);
            },
            DisplayName: "Phi-3.5-vision (Vision-Language, ORT GenAI)",
            Parameters: "4.2B",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/microsoft/Phi-3.5-vision-instruct-onnx",
            Category: "vlm",
            Modalities: ["image", "text"],
            Files: Phi35VisionFiles()));
    }

    /// <summary>
    /// Registers Moondream2 — small (1.9B-param) vision-language model
    /// with a SigLIP-style 378×378 encoder and a Phi-1.5/2 decoder.
    /// SQL surface: <c>models.moondream2(image, prompt) → string</c>.
    /// First registration in the catalog with the
    /// <c>(Image, String) → String</c> shape; pairs with Florence-2's
    /// fixed-task captioners and OCR-with-region for the screenshot-
    /// interpretation comparison harness.
    /// </summary>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name. Defaults to <c>"moondream2"</c>.</param>
    /// <param name="maxTokens">
    /// Cap on generated tokens per prompt. 256 is generous for descriptions
    /// and short Q&amp;A; raise for long-form summarisation.
    /// </param>
    public static void RegisterMoondream2(
        ModelCatalog catalog,
        string modelName = "moondream2",
        int maxTokens = 256)
    {
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: Moondream2VisionAnchor,
            InputKinds: [DataKind.Image, DataKind.String],
            OutputKind: DataKind.String,
            // Greedy argmax sampling — same image+prompt produces the same
            // text every call.
            IsDeterministic: true,
            Loader: ctx =>
            {
                string visionPath = Path.Combine(ctx.ModelDirectory, Moondream2VisionAnchor);
                return new Moondream2Model(modelName, visionPath, maxTokens: maxTokens);
            },
            DisplayName: "Moondream2 (Vision-Language)",
            Parameters: "1.9B",
            License: "Apache-2.0",
            LicenseHolder: "vikhyatk",
            SourceUrl: "https://huggingface.co/Xenova/moondream2",
            Category: "vlm",
            Modalities: ["image", "text"],
            Files: Moondream2Files()));
    }

    /// <summary>
    /// Registers Kokoro-82M TTS under the catalog name <paramref name="modelName"/>
    /// (defaults to <c>"kokoro_82m"</c>). 82M-parameter ONNX TTS with 11+ built-in
    /// voices, fast enough to keep up with token-streaming LLM output.
    /// Apache-2.0, hosted at <c>hexgrad/Kokoro-82M-ONNX</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Python prerequisites.</strong> The worker needs the
    /// <c>kokoro-onnx</c> package (which bundles the misaki phonemizer and
    /// the ONNX Runtime call) plus <c>soundfile</c> for WAV encoding.
    /// Recommended setup inside the model directory:
    /// <code>
    /// python -m venv .venv-kokoro
    /// .venv-kokoro\Scripts\pip install kokoro-onnx soundfile
    /// </code>
    /// </para>
    /// <para>
    /// <strong>Required files</strong> in the model directory:
    /// <list type="bullet">
    ///   <item><description><c>kokoro-v1.0.onnx</c> — the 82M-parameter model (~326 MB unquantized).</description></item>
    ///   <item><description><c>voices-v1.0.bin</c> — bundled voices file (~26 MB), OR a directory of per-voice <c>.bin</c> files. Pass whichever via <paramref name="voicesPath"/>.</description></item>
    /// </list>
    /// Both available from <c>hexgrad/Kokoro-82M-ONNX</c> on HuggingFace.
    /// </para>
    /// <para>
    /// <strong>Per-call overrides</strong> (declared in <c>OptionalArgKinds</c>):
    /// <list type="bullet">
    ///   <item><description>[0] <c>voice</c> (string) — e.g. <c>'af_bella'</c>, <c>'am_michael'</c>, <c>'bm_george'</c>. Empty string falls back to <paramref name="defaultVoice"/>.</description></item>
    ///   <item><description>[1] <c>speed</c> (Float64) — 0.5..2.0. Defaults to <paramref name="defaultSpeed"/> when omitted.</description></item>
    /// </list>
    /// SQL examples:
    /// <code>
    /// SELECT models.kokoro_82m(text)                          -- defaults
    /// SELECT models.kokoro_82m(text, 'af_bella')              -- voice override
    /// SELECT models.kokoro_82m(text, 'bm_george', 1.2)        -- voice + speed
    /// </code>
    /// </para>
    /// <para>
    /// <strong>Determinism.</strong> Kokoro is deterministic for a given
    /// (text, voice, speed) tuple — useful for cached re-renders and
    /// planner CSE.
    /// </para>
    /// </remarks>
    /// <param name="catalog">Catalog to register against.</param>
    /// <param name="modelName">SQL-visible name (the <c>X</c> in <c>models.X(text)</c>).</param>
    /// <param name="onnxFilename">ONNX model filename relative to the catalog's <see cref="ModelCatalog.ModelDirectory"/>. Defaults to <see cref="Kokoro82MOnnxDefaultFilename"/>.</param>
    /// <param name="voicesPath">
    /// Voices file or directory, relative to the model directory. Defaults
    /// to <see cref="Kokoro82MVoicesDefaultFilename"/>. Pass a directory
    /// name (e.g. <c>"kokoro-voices"</c>) if you have per-voice <c>.bin</c>
    /// files instead of the bundled archive.
    /// </param>
    /// <param name="scriptPath">
    /// Absolute path to the Kokoro Python worker. <see langword="null"/>
    /// resolves to the engine-bundled script at
    /// <c>{AppContext.BaseDirectory}/python/{Kokoro82MWorkerFilename}</c>.
    /// </param>
    /// <param name="pythonExecutable">
    /// Absolute path to the Python interpreter to spawn (typically a
    /// venv-scoped <c>python.exe</c>). <see langword="null"/> falls back
    /// to the <c>DATUM_PYTHON</c> environment variable, then <c>python</c>
    /// on PATH.
    /// </param>
    /// <param name="defaultVoice">Voice used when the per-call override is empty. Defaults to <c>"af_heart"</c>.</param>
    /// <param name="defaultSpeed">Speed used when the per-call override is omitted. Defaults to 1.0 (unchanged tempo).</param>
    /// <param name="lang">Language tag passed to Kokoro (e.g. <c>"en-us"</c>, <c>"en-gb"</c>).</param>
    /// <param name="readyTimeoutSeconds">Worker startup timeout. Kokoro loads in well under 30s warm-cache.</param>
    public static void RegisterKokoro82M(
        ModelCatalog catalog,
        string modelName = "kokoro_82m",
        string onnxFilename = Kokoro82MOnnxDefaultFilename,
        string voicesPath = Kokoro82MVoicesDefaultFilename,
        string? scriptPath = null,
        string? pythonExecutable = null,
        string defaultVoice = "af_heart",
        double defaultSpeed = 1.0,
        string lang = "en-us",
        int readyTimeoutSeconds = 60)
    {
        string resolvedScriptPath = scriptPath
            ?? Path.Combine(AppContext.BaseDirectory, "python", Kokoro82MWorkerFilename);

        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "python",
            // The .onnx is the discoverability anchor for catalog status.
            // The voices bundle/dir is captured in Files below.
            RelativePath: onnxFilename,
            InputKinds: [DataKind.String],
            OutputKind: DataKind.Audio,
            // Same (text, voice, speed) -> same audio. Lets the planner CSE
            // duplicate call sites within a query.
            IsDeterministic: true,
            Loader: ctx =>
            {
                string onnxPath = Path.Combine(ctx.ModelDirectory, onnxFilename);
                string resolvedVoicesPath = Path.Combine(ctx.ModelDirectory, voicesPath);
                // Auto-detect a per-model venv at the conventional
                // {ModelDirectory}/.venv-kokoro/ location when no explicit
                // executable was passed; falls back to DATUM_PYTHON / PATH
                // if the venv isn't present.
                string? py = pythonExecutable
                    ?? ResolveVenvPython(ctx.ModelDirectory, ".venv-kokoro");
                return new PythonBackedModel(
                    name: modelName,
                    inputKinds: [DataKind.String],
                    outputKind: DataKind.Audio,
                    isDeterministic: true,
                    scriptPath: resolvedScriptPath,
                    pythonExecutable: py,
                    scriptArgs:
                    [
                        "--model-path", onnxPath,
                        "--voices-path", resolvedVoicesPath,
                        "--default-voice", defaultVoice,
                        "--default-speed", defaultSpeed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "--lang", lang,
                    ],
                    readyTimeout: TimeSpan.FromSeconds(readyTimeoutSeconds),
                    // Per-clip streaming, same cadence as SDXL-Turbo.
                    preferredBatchSize: 1);
            },
            // Per-call optional positional args:
            //   [0] voice (String)  - voice override, empty/null -> default
            //   [1] speed (Float64) - playback speed override (0.5..2.0)
            OptionalArgKinds: [DataKind.String, DataKind.Float64],
            DisplayName: "Kokoro 82M TTS (Python-backed)",
            Parameters: "82M",
            License: "Apache-2.0",
            LicenseHolder: "hexgrad",
            SourceUrl: "https://huggingface.co/hexgrad/Kokoro-82M-ONNX",
            Category: "tts",
            Modalities: ["text", "audio"],
            // Track the .onnx + the voices entry. The voices path may be
            // a directory; the catalog's existence check still surfaces
            // its absence as status=missing in `system.models`.
            Files: [onnxFilename, voicesPath]));
    }

    /// <summary>
    /// Resolves a per-model venv's Python executable, if a venv exists at
    /// <c>{modelDirectory}/{venvFolder}/</c>. Returns <see langword="null"/>
    /// when no venv is present so <see cref="PythonBackedModel"/> falls back
    /// to <c>DATUM_PYTHON</c> env var or <c>python</c> on PATH. The venv
    /// layout is the standard <c>python -m venv</c> convention: <c>Scripts/</c>
    /// on Windows, <c>bin/</c> elsewhere.
    /// </summary>
    private static string? ResolveVenvPython(string modelDirectory, string venvFolder)
    {
        string venvRoot = Path.Combine(modelDirectory, venvFolder);
        if (!Directory.Exists(venvRoot)) return null;

        string candidate = OperatingSystem.IsWindows()
            ? Path.Combine(venvRoot, "Scripts", "python.exe")
            : Path.Combine(venvRoot, "bin", "python");

        return File.Exists(candidate) ? candidate : null;
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
