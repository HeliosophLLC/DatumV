using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Catalog.Providers;
using Heliosoph.DatumV.Diagnostics;
using Heliosoph.DatumV.ModelLibrary;
using Heliosoph.DatumV.Models.Python;

using Microsoft.Extensions.Logging.Abstractions;

namespace Heliosoph.DatumV.Models;

/// <summary>
/// Attaches the model subsystem — <see cref="ModelCatalog"/>, calibration
/// store, catalog vocabulary, <c>system.*</c> table providers, the Python
/// toolchain manager — to a <see cref="TableCatalog"/>. The class registers
/// no individual models itself; every model in the zoo is SQL-defined,
/// loaded via the catalog manifest at <c>models/catalog.json</c> and
/// installed through each entry's <c>installSql</c>. This is the single
/// entry point that wires the layer up.
/// </summary>
public static class ModelHost
{
    /// <summary>
    /// One-call setup for the model subsystem on a <see cref="TableCatalog"/>.
    /// Builds a fresh <see cref="ModelCatalog"/> rooted at
    /// <paramref name="modelDirectory"/> (or the default — <c>DATUMV_MODELS</c>
    /// env var, then per-user fallback), loads the on-disk
    /// <c>models/catalog.json</c> manifest, registers any catalog-driven
    /// Python entries via <see cref="Python.CatalogDrivenPythonRegistrar"/>,
    /// wires the model catalog onto <see cref="TableCatalog.Models"/>, and
    /// adds every <c>system.*</c> model-shaped table provider
    /// (<c>system.models</c>, <c>system.tasks</c>,
    /// <c>system.model_calibration</c>, residency snapshots,
    /// <c>system.python_paths</c>, <c>system.python_environments</c>,
    /// <c>system.types</c>) for runtime introspection.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Use this from any callsite that wants the canonical model-enabled
    /// configuration: shell startup, batch tools, programmatic API consumers,
    /// integration tests. Returns the constructed <see cref="ModelCatalog"/>
    /// so callers that need finer control (custom VRAM budget, additional
    /// programmatically-registered models) can keep configuring after the
    /// subsystem is attached.
    /// </para>
    /// <para>
    /// The user's data tables can be registered before or after this call —
    /// the virtual <c>system.*</c> tables don't conflict because they use
    /// reserved schema-qualified names. Callers that gate on "user provided
    /// at least one data table" should track datum-file count explicitly
    /// rather than relying on <see cref="TableCatalog.Count"/>, since
    /// <c>ModelHost</c> always contributes several entries.
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
    public static ModelCatalog AttachTo(
        TableCatalog tableCatalog,
        string? modelDirectory = null,
        long? vramBudgetBytes = null)
    {
        long resolvedBudget = vramBudgetBytes ?? VramBudgetResolver.Resolve();

        // Wire calibration persistence when we can detect a stable host
        // fingerprint. On NVIDIA hosts this lets `system.models.weight_cost_bytes`
        // and the full `system.model_calibration` curve survive across
        // process restarts via `%LOCALAPPDATA%/Heliosoph.DatumV/calibration.json`.
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
        // because ModelHost constructs its own ModelCatalog (and
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
        // Every LLM in the zoo is now SQL-defined — see
        // models/sql/<catalog-id>/2026-06-01.sql for each entry's
        // CREATE MODEL declarations. The C# IModel path stays available
        // for non-catalog programmatic use (LlamaModel can still be
        // instantiated directly by host code) but no IModels are
        // registered into the catalog at startup.
        //
        // Catalog-driven LLMs (chat + simple TextGenerator delegate per id):
        //   - llama-3.1-8b-instruct-gguf       → llama31_8b_chat + llama31_8b
        //   - mistral-7b-instruct-v0.3-gguf    → mistral_7b_chat + mistral_7b
        //   - qwen2.5-coder-{1.5b,3b,7b}-instruct-gguf
        //   - tinyllama-1.1b-chat-v1.0-gguf    → tinyllama_1_1b_chat + tinyllama_1_1b
        //   - granite-3.1-1b-a400m-instruct-gguf → granite31_1b_chat + granite31_1b
        //   - falcon3-1b-instruct-gguf         → falcon3_1b_chat + falcon3_1b
        //
        // Retired (not currently exposed):
        //   - Phi-3.5-mini, Phi-3-mini-4k: bartowski Q4_K_M quants tilt
        //     trivial prompts into training-data continuation / code-bleed.
        //     Reintroduce if a better Phi quant lands.
        //   - Gemma 2 2B: deferred (catalog license file + entry not yet authored).

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
        PythonEnvironmentManager pythonEnvironments = new();

        // Catalog-driven Python model registration. Reads kind="python"
        // entries from models/catalog.json (today: bark-small + bark)
        // and registers them as ModelCatalogEntry instances with lazy
        // PythonBackedModel loaders. Kokoro stays hardcoded for now
        // because its scaffold args reference runtime-resolved model-
        // directory paths (--model-path / --voices-path); migrating it
        // to the catalog needs either scaffold-arg path templating or a
        // worker refactor, neither of which belongs in this PR.
        (CatalogManifest? catalogManifest, ILicenseRegistry? licenseRegistry) =
            TryLoadCatalogAndLicenses();
        if (catalogManifest is not null && licenseRegistry is not null)
        {
            string scriptsDirectory = Path.Combine(AppContext.BaseDirectory, "python");
            CatalogDrivenPythonRegistrar.RegisterAll(
                modelCatalog, pythonEnvironments, catalogManifest, licenseRegistry, scriptsDirectory);
        }

        // Kokoro 82M TTS retired for the first release alongside the
        // other Python-backed models. The worker script + catalog wiring
        // remain available for re-introduction in a follow-up release
        // once first-line support for Python venvs is in scope.

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
        tableCatalog.Add(new PythonPathsTableProvider(
            tableCatalog.Pool, pythonEnvironments));
        tableCatalog.Add(new PythonEnvironmentsTableProvider(
            tableCatalog.Pool, pythonEnvironments));

        tableCatalog.Add(new TypesTableProvider(
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
    // (Heliosoph.DatumV/mobilenetv2-onnx) and is loaded catalog-relative.

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

    /// <summary>
    /// Loads the engine-bundled models/catalog.json manifest via a
    /// transient <see cref="ManifestStore"/>. Returns
    /// <see langword="null"/> when the manifest can't be located (test
    /// runs, custom layouts) — callers treat that as "no catalog-driven
    /// Python registrations" and fall back to hardcoded paths. Wraps
    /// any deserialize / validation throw so a malformed catalog
    /// doesn't break engine startup; the error surfaces in stderr
    /// instead.
    /// </summary>
    private static (CatalogManifest?, ILicenseRegistry?) TryLoadCatalogAndLicenses()
    {
        try
        {
            LicenseRegistry licenses = new(NullLogger<LicenseRegistry>.Instance);
            ManifestStore store = new(licenses, NullLogger<ManifestStore>.Instance);
            return (store.Manifest, licenses);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[builtin-models] failed to load catalog manifest: {ex.Message}");
            return (null, null);
        }
    }
}
