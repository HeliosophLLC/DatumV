using System.Text.Json;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.Models.Llama;
using DatumIngest.Models.Onnx;
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
    /// builtin model (<c>llama31_8b</c>,
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

        // Wire calibration persistence when we can detect a stable host
        // fingerprint. On NVIDIA hosts this lets `system.models.weight_cost_bytes`
        // and the full `system.model_calibration` curve survive across
        // process restarts via `%LOCALAPPDATA%/DatumIngest/calibration.json`.
        // On hosts without NVML (CPU-only, AMD/Intel GPU, init failed)
        // `HostFingerprint.Detect` returns null and we fall through to
        // in-memory-only calibration — engine still runs, just doesn't
        // persist across restarts.
        //
        // ORT version comes from `OnnxRuntimeVersion.Value` — a static
        // `typeof(InferenceSession).Assembly` lookup in the inference
        // layer that's both trim-safe (no IL2026 warning) and
        // NativeAOT-compatible (no `Assembly.Load(name)` call). The
        // diagnostics layer stays leaf and just receives the string.
        Diagnostics.HostFingerprint? fingerprint = Diagnostics.HostFingerprint.Detect(
            Inference.OnnxRuntime.OnnxRuntimeVersion.Value);
        Calibration.CalibrationStore? calibrationStore =
            fingerprint is not null ? new Calibration.CalibrationStore() : null;

        // Loud trace when persistence is disabled. Silent failure here
        // was the cause of "calibration runs but nothing survives a
        // restart" — calibration data accumulated in the in-memory
        // registry while every save call was a no-op. Surface the
        // reason at startup so future regressions are immediately
        // visible in `--activity-log`.
        if (fingerprint is null)
        {
            DatumActivity.Calibration.Trace(
                "persistence disabled: HostFingerprint.Detect returned null "
                + "(no NVIDIA GPU / NVML / ORT assembly resolvable). "
                + "Calibration will work in-memory but won't survive restarts.");
        }

        ModelCatalog modelCatalog = new(
            modelDirectory,
            resolvedBudget,
            admissionTimeout: null,
            calibrationStore: calibrationStore,
            hostFingerprint: fingerprint);

        // Vision models
        // PP-OCR-det, MobileNetV2, and YOLOX-{nano,tiny,s,m,l,x,darknet}
        // were previously registered here as built-in C# IModels; they
        // shipped as SQL-defined models under models/sql/. Their catalog
        // entries declare installSql so the downloader registers them
        // after fetching weights. SCRFD-10G was removed entirely
        // (InsightFace weights aren't openly licensed).
        // Real-ESRGAN + U²-Net (full + lite) + MiDaS-small + DPT-Large
        // migrated to SQL-defined models (models/sql/{realesrgan-x4v3,u2net,
        // u2netp,midas-small,dpt-large}.sql).
        RegisterMobileSamPrompted(modelCatalog);
        RegisterMobileSam(modelCatalog);
        // ViT-GPT2 migrated to a SQL-defined model (models/sql/vit-gpt2-image-captioning.sql).
        // TrOCR (fp32 + fp16) migrated to SQL-defined models
        // (models/sql/trocr-base-printed.sql + trocr-base-printed-fp16.sql).

        // Captioner zoo — Florence-2 in three caption styles plus a
        // quantized comparison entry. Same model, different task tokens.
        // OCR-with-region reuses the same files via a different task token
        // and is the license-clean baseline for screenshot text extraction.
        // Florence-2 (5 caption/OCR variants × 2 binaries) migrated to
        // SQL-defined models (models/sql/florence-2-base-ft-fp16.sql and
        // models/sql/florence-2-base-ft-quantized.sql). Five SQL-visible
        // catalog names match the original C# registrations:
        //   florence2_caption, florence2_detailed_caption,
        //   florence2_more_detailed_caption, florence2_ocr_with_region (fp16),
        //   florence2_caption_q8 (INT8 quantized).

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

        // VLM zoo — Apache/MIT-licensed alternatives to OmniParser's
        // AGPL detector. Phi-3.5-vision uses ORT GenAI for IO-binding-
        // accelerated generation. Moondream2 migrated to SQL (catalog
        // installSql → models/sql/moondream2.sql) using the
        // `decode_decoder_only` scalar; no C# registration here.
        RegisterPhi35Vision(modelCatalog);

        // Python-bridge models. These show status=bridge in system.models
        // (rather than available/missing) when their worker scripts and
        // model files exist, signalling that runnability also depends on
        // a Python venv with the upstream packages installed -- catalog
        // can't verify pip state without spawning the worker. Conventional
        // venv layout is {ModelDirectory}/.venv-<name>/ which the loaders
        // auto-detect.
        // Engine-managed Python toolchain. Single instance shared
        // across all Python-backed registrations (Bark, Kokoro, future
        // models) and the two `system.python_*` providers — so the
        // venvs created by model loaders are the same venvs the
        // system tables show.
        DatumIngest.Models.Python.PythonEnvironmentManager pythonEnvironments = new();

        // Catalog-driven Python model registration. Reads kind="python"
        // entries from models/catalog.json (today: bark-small + bark)
        // and registers them as ModelCatalogEntry instances with lazy
        // PythonBackedModel loaders. Kokoro stays hardcoded for now
        // because its scaffold args reference runtime-resolved model-
        // directory paths (--model-path / --voices-path); migrating it
        // to the catalog needs either scaffold-arg path templating or a
        // worker refactor, neither of which belongs in this PR.
        DatumIngest.ModelLibrary.CatalogManifest? catalogManifest = TryLoadCatalogManifest();
        if (catalogManifest is not null)
        {
            string scriptsDirectory = System.IO.Path.Combine(AppContext.BaseDirectory, "python");
            DatumIngest.Models.Python.CatalogDrivenPythonRegistrar.RegisterAll(
                modelCatalog, pythonEnvironments, catalogManifest, scriptsDirectory);
        }

        RegisterKokoro82M(modelCatalog, pythonEnvironments);

        tableCatalog.Models = modelCatalog;
        tableCatalog.Add(new ModelsTableProvider(
            tableCatalog.Pool, modelCatalog, tableCatalog.DeclaredModels));
        tableCatalog.Add(new ModelCalibrationTableProvider(
            tableCatalog.Pool, modelCatalog));
        tableCatalog.Add(new ResidencySnapshotTableProvider(
            tableCatalog.Pool, modelCatalog));
        tableCatalog.Add(new VramSnapshotTableProvider(
            tableCatalog.Pool, modelCatalog));

        // Python toolchain visibility — surfaces what's been installed
        // under the engine's managed directory and how much disk it
        // costs.
        tableCatalog.Add(new DatumIngest.Catalog.Providers.PythonPathsTableProvider(
            tableCatalog.Pool, pythonEnvironments));
        tableCatalog.Add(new DatumIngest.Catalog.Providers.PythonEnvironmentsTableProvider(
            tableCatalog.Pool, pythonEnvironments));

        tableCatalog.Add(new DatumIngest.Catalog.Providers.TypesTableProvider(
            tableCatalog.Pool));
        return modelCatalog;
    }

    // MobileNetV2 was previously registered here as a built-in C# IModel
    // (MobileNetV2Model.cs). It shipped as a SQL-defined model in
    // models/sql/mobilenetv2.sql backed by `image_to_tensor_chw`,
    // `softmax`, `argmax`, and `read_string_list('imagenet-classes.json')`
    // — the C# class was deleted along with RegisterMobileNetV2,
    // MobileNetV2DefaultFilename, and ImageNetLabelsDefaultFilename. The
    // labels file now travels with the model bundle on HuggingFace
    // (Heliosoph/mobilenetv2-onnx) and is loaded catalog-relative.

    /// <summary>
    /// Default filename for the Llama 3.1 8B Instruct GGUF (Q4_K_M
    /// imatrix-quantized variant from bartowski's HuggingFace repo).
    /// </summary>
    public const string Llama31_8BDefaultFilename = "llama-3.1-8b-instruct-gguf/Meta-Llama-3.1-8B-Instruct-Q4_K_M.gguf";

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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
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
            ImplementsTaskName: "TextGenerator",
            Parameters: "7B",
            License: "Apache-2.0",
            LicenseHolder: "Mistral AI",
            SourceUrl: "https://huggingface.co/bartowski/Mistral-7B-Instruct-v0.3-GGUF",
            Category: "llm",
            Modalities: ["text"],
            Files: [modelFilename]));
    }

    // ViT-GPT2 was previously registered here as a built-in C# IModel
    // (ViTGpt2CaptionModel.cs). It now ships as a SQL-defined model in
    // models/sql/vit-gpt2-image-captioning.sql backed by image_to_tensor_chw,
    // a two-session USING (encoder + decoder), decode_seq2seq for greedy
    // generation, and tokenizer.decode_bpe + tokenizer.byte_level_decode
    // for caption assembly. The catalog entry's installSql declaration
    // re-registers the SQL form when the model directory is rehydrated.

    // TrOCR (fp32 + fp16) was previously registered here as a built-in
    // C# IModel (TrOcrModel.cs). Both variants now ship as SQL-defined
    // models in models/sql/trocr-base-printed.sql and
    // models/sql/trocr-base-printed-fp16.sql, backed by image_to_tensor_chw,
    // a two-session USING (encoder + merged-decoder-with-cache), and
    // decode_seq2seq with use_kv_cache=true for the auto-regressive loop.
    // The catalog entries' installSql declarations re-register the SQL
    // forms when the model directories are rehydrated.

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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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
            ImplementsTaskName: "TextToImage",
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

    /// <summary>
    /// Default folder for the MobileSAM bundle. Matches the catalog id
    /// (<c>mobile-sam</c>) so the downloader's per-entry folder convention
    /// resolves to the same on-disk location.
    /// </summary>
    public const string MobileSamFolder = "mobile-sam";

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
        // All files land under {ModelDirectory}/{MobileSamFolder}/ once the
        // catalog downloader extracts the bundle. Prefix every path with
        // the folder so the loader + Files manifest agree with on-disk
        // layout.
        string encoderRelativePath = $"{MobileSamFolder}/{encoderFilename}";
        string decoderRelativePath = $"{MobileSamFolder}/{decoderFilename}";
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderRelativePath,
            InputKinds: [DataKind.Image, DataKind.Float64, DataKind.Float64],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderRelativePath);
                string decoderPath = Path.Combine(ctx.ModelDirectory, decoderRelativePath);
                return new MobileSamModel(modelName, encoderPath, decoderPath, MobileSamMode.Prompted);
            },
            DisplayName: "MobileSAM (prompted segmentation)",
            Parameters: "9.7M",
            License: "Apache-2.0",
            LicenseHolder: "Meta AI / Kyung Hee University",
            SourceUrl: "https://github.com/ChaoningZhang/MobileSAM",
            Category: "segmenter",
            Modalities: ["image"],
            Files: [encoderRelativePath, decoderRelativePath]));
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
        string encoderRelativePath = $"{MobileSamFolder}/{encoderFilename}";
        string decoderRelativePath = $"{MobileSamFolder}/{decoderFilename}";
        catalog.Register(new ModelCatalogEntry(
            Name: modelName,
            Backend: "onnx",
            RelativePath: encoderRelativePath,
            InputKinds: [DataKind.Image],
            OutputKind: DataKind.Image,
            IsDeterministic: true,
            Loader: ctx =>
            {
                string encoderPath = Path.Combine(ctx.ModelDirectory, encoderRelativePath);
                string decoderPath = Path.Combine(ctx.ModelDirectory, decoderRelativePath);
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
            Files: [encoderRelativePath, decoderRelativePath]));
    }

    // YOLOX-{nano,tiny,s,m,l,x,darknet} previously registered here as built-in
    // C# IModels (YoloXModel.cs + CocoLabels.cs). They shipped as SQL-defined
    // models in models/sql/yolox-{nano,tiny,s,m,l,x,darknet}.sql backed by the
    // three new scalar functions (yolox_preprocess, yolox_postprocess,
    // read_string_list). The C# class was deleted along with the seven
    // RegisterYoloX* methods. Each variant's catalog.json entry declares
    // installSql so the downloader runs CREATE MODEL after fetching the
    // ONNX + coco-classes.json bundle.

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

    // ─────────────────────────── Whisper STT ──────────────────────────────
    //
    // OpenAI Whisper as native ONNX. Each size variant is a separate
    // registration pointing at its own optimum-cli output folder. Same
    // architectural shape as ViT-GPT2 (encoder + autoregressive decoder),
    // plus a Slaney mel-spectrogram pipeline for the audio side. MIT
    // license, fully unencumbered.

    /// <summary>Default folder for Whisper Tiny (~78 MB encoder + 250 MB decoder).
    /// Matches the catalog id so the downloader's per-entry folder convention
    /// resolves to the same on-disk location.</summary>
    public const string WhisperTinyFolder = "whisper-tiny";

    /// <summary>Default folder for Whisper Base (~78 MB encoder + 300 MB decoder).</summary>
    public const string WhisperBaseFolder = "whisper-base";

    /// <summary>Default folder for Whisper Small (~280 MB encoder + 1.1 GB decoder).</summary>
    public const string WhisperSmallFolder = "whisper-small";

    /// <summary>Default folder for Whisper Medium (~870 MB encoder + 2.5 GB decoder).</summary>
    public const string WhisperMediumFolder = "whisper-medium";

    /// <summary>
    /// Files needed for any Whisper install (relative to the model
    /// directory). The Xenova / onnx-community export convention puts
    /// the two ONNX components in an <c>onnx/</c> subdir and the
    /// tokenizer + configs at the catalog folder's root — matches what
    /// the downloader gets from <c>"include": ["onnx/*", "*.json", "*.txt"]</c>.
    /// Tracks every file so <c>system.models</c> reports missing pieces
    /// accurately.
    /// </summary>
    private static IReadOnlyList<string> WhisperFiles(string folder) =>
    [
        $"{folder}/onnx/encoder_model.onnx",
        $"{folder}/onnx/decoder_model.onnx",
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
        // Encoder/decoder live in {folder}/onnx/ per the Xenova export
        // convention; tokenizer + configs at {folder}/ root.
        string encoderRelativePath = $"{folder}/onnx/encoder_model.onnx";

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
            ImplementsTaskName: "VisualQA",
            Parameters: "4.2B",
            License: "MIT",
            LicenseHolder: "Microsoft",
            SourceUrl: "https://huggingface.co/microsoft/Phi-3.5-vision-instruct-onnx",
            Category: "vlm",
            Modalities: ["image", "text"],
            Files: Phi35VisionFiles()));
    }

    // Moondream2 migrated to SQL — see models/sql/moondream2.sql, registered
    // via the catalog's installSql entry. Uses the `decode_decoder_only`
    // scalar for the KV-cached greedy decoder loop.

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
    /// <param name="pythonEnvironments">
    /// Engine-managed Python toolchain. First call triggers uv +
    /// Python 3.11 + kokoro-onnx install; subsequent calls fast-path.
    /// </param>
    /// <param name="defaultVoice">Voice used when the per-call override is empty. Defaults to <c>"af_heart"</c>.</param>
    /// <param name="defaultSpeed">Speed used when the per-call override is omitted. Defaults to 1.0 (unchanged tempo).</param>
    /// <param name="lang">Language tag passed to Kokoro (e.g. <c>"en-us"</c>, <c>"en-gb"</c>).</param>
    /// <param name="readyTimeoutSeconds">Worker startup timeout. Kokoro loads in well under 30s warm-cache.</param>
    public static void RegisterKokoro82M(
        ModelCatalog catalog,
        DatumIngest.Models.Python.PythonEnvironmentManager pythonEnvironments,
        string modelName = "kokoro_82m",
        string onnxFilename = Kokoro82MOnnxDefaultFilename,
        string voicesPath = Kokoro82MVoicesDefaultFilename,
        string? scriptPath = null,
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
                return new PythonBackedModel(
                    name: modelName,
                    inputKinds: [DataKind.String],
                    outputKind: DataKind.Audio,
                    isDeterministic: true,
                    environments: pythonEnvironments,
                    venvName: modelName,
                    pythonVersion: "3.11",
                    // kokoro-onnx pulls in numpy + onnxruntime
                    // transitively; listed explicitly so
                    // `system.python_environments` shows the full
                    // declared set.
                    requirements: ["kokoro-onnx", "numpy", "onnxruntime"],
                    scriptPath: resolvedScriptPath,
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
            ImplementsTaskName: "TextToAudio",
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
    /// Loads the engine-bundled models/catalog.json manifest via a
    /// transient <see cref="DatumIngest.ModelLibrary.ManifestStore"/>.
    /// Returns <see langword="null"/> when the manifest can't be
    /// located (test runs, custom layouts) — callers treat that as
    /// "no catalog-driven Python registrations" and fall back to
    /// hardcoded paths. Wraps any deserialize / validation throw so
    /// a malformed catalog doesn't break engine startup; the error
    /// surfaces in stderr instead.
    /// </summary>
    private static DatumIngest.ModelLibrary.CatalogManifest? TryLoadCatalogManifest()
    {
        try
        {
            DatumIngest.ModelLibrary.ManifestStore store = new(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<DatumIngest.ModelLibrary.ManifestStore>.Instance);
            return store.Manifest;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[builtin-models] failed to load catalog manifest: {ex.Message}");
            return null;
        }
    }
}
