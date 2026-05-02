using System.Text.Json;

using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Diagnostics;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models.Llama;
using DatumIngest.Models.Onnx;
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

        // Thread the TableCatalog through as the active-version lookup so
        // the resolver consults the live model rows instead of any
        // <id>/active filesystem pointer. Wires the lookup eagerly here
        // because BuiltinModels constructs its own ModelCatalog (and
        // therefore its own VersionedModelPathResolver) outside the DI
        // graph — the resolver registered in AddModelLibrary is not the
        // one ModelCatalog will use.
        ModelCatalog modelCatalog = new(
            modelDirectory,
            resolvedBudget,
            admissionTimeout: null,
            calibrationStore: calibrationStore,
            hostFingerprint: fingerprint,
            activeVersionLookup: tableCatalog);

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
        // MobileSAM migrated to a SQL-defined model (models/sql/mobile-sam/).
        // Prompted-mode TBD as a follow-up.
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

        // Whisper STT (tiny/base/small) migrated to SQL-defined models
        // (models/sql/whisper-*/2026-05-29.sql); the catalog entries' installSql
        // declarations re-register them when their bundles land on disk.

        // VLM zoo — Apache/MIT-licensed alternatives to OmniParser's AGPL
        // detector. Moondream2 ships as SQL (catalog installSql →
        // models/sql/moondream2.sql) using the `decode_decoder_only`
        // scalar. Phi-3.5-vision was previously registered here via
        // ORT GenAI; it was dropped in favour of the Python-backed
        // model path for VLMs that need IO-binding-accelerated
        // generation (avoids the ORT GenAI dependency + Microsoft's
        // OGA export lag for new models).

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

        // Build the catalog vocabulary surface once and share it with
        // the providers that need the reverse identifier→catalog index
        // (system.models for discovered rows + catalog_id + active_version,
        // and system.tasks for the dispatch-state join). Standalone hosts
        // without a manifest fall back to a null vocabulary — both
        // providers tolerate it: system.models drops the "discovered"
        // bucket, system.tasks isn't registered at all.
        ICatalogVocabulary? vocabulary = catalogManifest is null
            ? null
            : new CatalogVocabulary(catalogManifest);
        tableCatalog.CatalogVocabulary = vocabulary;

        tableCatalog.Add(new ModelsTableProvider(
            tableCatalog.Pool, modelCatalog, tableCatalog.DeclaredModels, vocabulary));
        if (vocabulary is not null)
        {
            tableCatalog.Add(new SystemTasksTableProvider(
                tableCatalog.Pool, vocabulary, modelCatalog, tableCatalog.DeclaredModels));
        }
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string modelPath = ctx.Paths.ResolveIdPrefixedPath(modelFilename);
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
                string onnxPath = ctx.Paths.ResolveIdPrefixedPath(onnxFilename);
                string resolvedVoicesPath = ctx.Paths.ResolveIdPrefixedPath(voicesPath);
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
