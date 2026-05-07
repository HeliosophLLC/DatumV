using DatumIngest.Model;
using DatumIngest.ModelLibrary;

namespace DatumIngest.Models;

/// <summary>
/// Catalog row for a single registered model. Combines metadata (name, backend,
/// path, signature) with a lazy factory that loads the underlying <see cref="IModel"/>
/// on first use. The catalog hands these out for the planner to validate calls
/// against (input kinds, return kind, namespace) — actual residency is managed
/// behind the scenes by the entry's loader plus the future
/// <c>ModelResidencyManager</c>.
/// </summary>
/// <remarks>
/// <para>
/// Today's entries land in this shape via the <c>CREATE MODEL</c> SQL
/// statement (catalog-driven installSql or user-authored) and via
/// programmatic registration from
/// <see cref="Python.CatalogDrivenPythonRegistrar"/> for kind="python"
/// catalog entries. <see cref="ModelHost"/> wires the subsystem up; it
/// doesn't construct entries itself.
/// </para>
/// </remarks>
/// <param name="Name">
/// Stable identifier without a namespace prefix. The function call <c>models.mobilenetv2(x)</c>
/// resolves <c>Name == "mobilenetv2"</c>.
/// </param>
/// <param name="Backend">
/// Free-form backend identifier (<c>"onnx"</c>, <c>"llama"</c>, <c>"echo"</c>) — used
/// for diagnostics and the future <c>sys.models</c> table column. The engine never
/// branches on this value; it's a label.
/// </param>
/// <param name="RelativePath">
/// File path relative to the catalog's <c>ModelDirectory</c>. <see langword="null"/>
/// for synthetic backends (<c>EchoModel</c>) that don't need a file.
/// </param>
/// <param name="InputKinds">
/// Required input column kinds; length = required arity. Every call site must
/// supply at least this many positional arguments matching these kinds.
/// </param>
/// <param name="OutputKind">The kind this model produces per row.</param>
/// <param name="OutputIsArray">
/// True when the model's per-row output is a typed array of
/// <paramref name="OutputKind"/> (set by SQL-defined models declaring
/// <c>RETURNS Array&lt;...&gt;</c> / <c>X[]</c>). Drives hover / signature-
/// help / completion rendering downstream; without it those surfaces would
/// silently drop the array marker.
/// </param>
/// <param name="ParameterInfos">
/// Optional per-parameter metadata (name + kind + array-ness + optionality)
/// used by the language server to render the actual declared parameter
/// names and shapes. SQL-defined models populate this; built-ins leave it
/// <see langword="null"/> and the manifest builder falls back to the
/// generic <c>input</c>/<c>inputN</c> labels derived from
/// <paramref name="InputKinds"/>.
/// </param>
/// <param name="OutputStructFields">
/// For struct-returning models: ordered <c>(name, kind, isArray)</c>
/// descriptors for each output field, used by the language server to
/// resolve <c>model_call().field</c> access at hover / completion time.
/// SQL-defined models populate this when their <c>RETURNS Struct&lt;…&gt;</c>
/// annotation carries an explicit field list; built-ins populate it from
/// <c>IModel.OutputFields</c>. <see langword="null"/> for non-struct
/// returns and for opaque <c>RETURNS Struct</c> models.
/// </param>
/// <param name="IsDeterministic">
/// <see langword="true"/> when the same input always yields the same output.
/// Drives CSE folding across call sites and cache validity in future demos.
/// </param>
/// <param name="Loader">
/// Factory that constructs the actual <see cref="IModel"/>. Invoked on first use
/// (lazily, behind the catalog's residency manager). Resolved
/// <see cref="IModel"/>s are cached for the catalog's lifetime.
/// </param>
/// <param name="OptionalArgKinds">
/// Per-call hyperparameter kinds, in the order they appear after the required
/// inputs (e.g. <c>[Float64, Int32]</c> for trailing
/// <c>(prompt, temperature, max_tokens)</c>). Each is optional positionally:
/// a call site may supply a prefix of this list, with later args defaulted by
/// the model. The <see cref="IModel"/> implementation receives them as the
/// <c>overrides</c> parameter to <c>IModel.InferBatchAsync</c>.
/// Defaults to <see langword="null"/> (no per-call overrides).
/// </param>
/// <param name="EstimatedVramBytes">
/// Hint to the residency manager for VRAM accounting, in bytes. When the
/// hint is <see langword="null"/>, the manager defaults to
/// <c>file_size × 1.2</c> for entries with a <see cref="RelativePath"/>;
/// the multiplier covers activations / KV cache / scratch buffers beyond
/// the on-disk weights. Provide an explicit value when you've measured the
/// real residency for a model and the file-size heuristic is materially off
/// (e.g. ONNX models where activations dominate).
/// </param>
/// <param name="DisplayName">
/// Human-readable model name surfaced in <c>system.models</c> and error
/// messages. Independent of <see cref="Name"/> (the SQL identifier) — e.g.
/// <c>"MobileNetV2 ImageNet Classifier"</c>, <c>"Llama 3.1 8B Instruct"</c>.
/// </param>
/// <param name="Parameters">
/// Readable parameter-count for the model: <c>"8B"</c>, <c>"7B"</c>,
/// <c>"3.8B"</c>, <c>"0.5B"</c>, <c>"3.5M"</c>. Not the on-disk size — the
/// architectural parameter count, stable across quantizations. Useful for
/// the side-by-side LLM-comparison use case.
/// </param>
/// <param name="License">
/// SPDX-style or model-specific license identifier:
/// <c>"Apache-2.0"</c>, <c>"MIT"</c>, <c>"AGPL-3.0"</c>,
/// <c>"Llama 3.1 Community"</c>, <c>"Gemma Terms"</c>, etc.
/// Surfaced in <c>system.models</c> so users can audit license compatibility
/// at a glance.
/// </param>
/// <param name="LicenseHolder">
/// Who issued the license — <c>"Meta"</c>, <c>"Microsoft"</c>,
/// <c>"Google"</c>, <c>"Alibaba"</c>, <c>"Megvii"</c>. Not strictly
/// the copyright holder (training data may have other rights), but the
/// entity granting the license under which the weights are distributed.
/// </param>
/// <param name="SourceUrl">
/// HuggingFace repo, model-zoo URL, or other canonical source for the
/// weights file. Lets users re-download a missing file without remembering
/// where it came from. Repo URL preferred over direct file URL — direct
/// file URLs rot when uploaders re-quantize.
/// </param>
/// <param name="Category">
/// Single-valued purpose label: <c>"llm"</c>, <c>"classifier"</c>,
/// <c>"detector"</c>, <c>"embedder"</c>, <c>"captioner"</c>,
/// <c>"transcriber"</c>, <c>"generator"</c>. Routing key for the
/// future <c>tasks.X</c> namespace — <c>tasks.classify</c> consumes
/// models where <c>Category == "classifier"</c>, etc.
/// </param>
/// <param name="Modalities">
/// All mediums this model touches in either direction: e.g.
/// <c>["text"]</c> for an LLM, <c>["image", "text"]</c> for a
/// classifier or captioner, <c>["audio", "text"]</c> for a transcriber.
/// Order is not significant. Lets users find "every model that handles
/// image" via <c>WHERE 'image' IN modalities</c> regardless of whether
/// image is the input or output side.
/// </param>
/// <param name="Files">
/// Every file the model needs to run, expressed as paths *relative to
/// the model directory*. For single-file models this is a one-element
/// list (<c>["yolox_s.onnx"]</c>); for multi-file models like Florence-2
/// or ViT-GPT2 it lists every <c>.onnx</c> + tokenizer + config file
/// the loader will read. Surfaced via <c>system_models.file_names</c>
/// so users can audit dependencies and reconstruct missing installs.
/// Distinct from <see cref="RelativePath"/>, which is the single
/// "anchor" file the catalog uses for status checks.
/// </param>
/// <param name="ImplementsTaskName">
/// Optional task contract this built-in satisfies (e.g.
/// <c>"ImageClassifier"</c>, <c>"TextEmbedder"</c>). Mirrors the
/// <c>IMPLEMENTS</c> clause on SQL-defined models. Surfaces on
/// <c>system.models.task</c> alongside declared models so the frontend
/// dispatch layer (Phase 2) can route uniformly across <c>kind = 'builtin'</c>
/// and <c>kind = 'declared'</c> entries.
/// </param>
/// <param name="Batchable">
/// Whether the engine can dispatch N rows of this model in one cross-row
/// batched call. Mirrors <see cref="IModel.IsBatchable"/>: for SQL-defined
/// models this comes from a straight-line check on the body; for built-in
/// <see cref="IModel"/>s the impl reports its own answer (default
/// <see langword="false"/>, since builtins normally handle batching inside
/// their own <c>InferBatchAsync</c> and don't need the columnar-body path).
/// Surfaces on <c>system.models.batchable</c> as a diagnostic.
/// </param>
/// <param name="FingerprintPath">
/// Optional absolute path used to fingerprint this model's weights for
/// calibration invalidation. When <see langword="null"/>, the calibration
/// layer falls back to resolving <see cref="RelativePath"/> against the
/// catalog's model directory. Set explicitly for SQL-defined models
/// (where <see cref="RelativePath"/> is null but the descriptor's
/// <c>ResolvedUsingPath</c> already points at the primary ONNX file),
/// or any other entry whose canonical weights file isn't expressible
/// as a path relative to the catalog's model directory.
/// </param>
public sealed record ModelCatalogEntry(
    string Name,
    string Backend,
    string? RelativePath,
    IReadOnlyList<DataKind> InputKinds,
    DataKind OutputKind,
    bool IsDeterministic,
    Func<ModelLoadContext, IModel> Loader,
    IReadOnlyList<DataKind>? OptionalArgKinds = null,
    long? EstimatedVramBytes = null,
    string? DisplayName = null,
    string? Parameters = null,
    string? License = null,
    string? LicenseHolder = null,
    string? SourceUrl = null,
    string? Category = null,
    IReadOnlyList<string>? Modalities = null,
    IReadOnlyList<string>? Files = null,
    string? ImplementsTaskName = null,
    bool Batchable = false,
    string? FingerprintPath = null,
    bool OutputIsArray = false,
    IReadOnlyList<ModelParameterInfo>? ParameterInfos = null,
    IReadOnlyList<ModelStructFieldInfo>? OutputStructFields = null);

/// <summary>
/// Per-parameter metadata for a registered model — used by the language
/// server to render the actual declared name + shape in hover / signature
/// help / completion. SQL-defined models populate this from their
/// <c>UdfParameter</c> list; built-ins leave it <see langword="null"/>
/// and the manifest builder falls back to the generic
/// <c>input</c>/<c>input1</c>/… labels derived from <c>InputKinds</c>.
/// </summary>
/// <param name="Name">Declared parameter name (<c>img</c>, <c>prompt</c>, …).</param>
/// <param name="Kind">Element data kind. Combine with <paramref name="IsArray"/> for the full shape.</param>
/// <param name="IsArray">True when the parameter was declared as a typed array.</param>
/// <param name="IsOptional">True when the parameter has a default (call site may omit).</param>
/// <param name="StructFields">
/// When the element kind is <see cref="DataKind.Struct"/> AND the declared
/// type name resolved to a known struct shape (a named vocabulary entry
/// like <c>ChatMessage</c> or an inline <c>Struct&lt;name: Kind, …&gt;</c>),
/// the ordered field list. Lets the language server suggest field names
/// inside the struct literal at this parameter slot. <see langword="null"/>
/// for non-struct parameters and for opaque bare <c>Struct</c>.
/// </param>
public sealed record ModelParameterInfo(
    string Name,
    DataKind Kind,
    bool IsArray,
    bool IsOptional,
    IReadOnlyList<ModelStructFieldInfo>? StructFields = null);

/// <summary>
/// Per-field metadata for a struct-returning model's output. Drives the
/// language server's <c>model_call().field</c> resolution. Mirrors the
/// shape of <see cref="ModelParameterInfo"/> for inputs.
/// </summary>
/// <param name="Name">Declared field name (<c>depth</c>, <c>intrinsics</c>, …).</param>
/// <param name="Kind">Element data kind for the field's value.</param>
/// <param name="IsArray">True when the field's value is a typed array of <paramref name="Kind"/>.</param>
/// <param name="KindLabel">
/// Canonical kind label including any dim / width suffix the user
/// declared in their <c>RETURNS Struct&lt;…&gt;</c> annotation —
/// <c>"Array&lt;Float32&gt;(518, 518)"</c>, <c>"VARCHAR(64)"</c>, etc.
/// The structured <see cref="Kind"/> / <see cref="IsArray"/> fields lose
/// that suffix during their TryParse round-trip; the label preserves it
/// so hover popups can show the full declared shape. Falls back to the
/// kind name when no richer label is available.
/// </param>
public sealed record ModelStructFieldInfo(string Name, DataKind Kind, bool IsArray, string KindLabel);

/// <summary>
/// Context handed to a <see cref="ModelCatalogEntry.Loader"/> when first instantiating
/// the underlying <see cref="IModel"/>. Exposes the catalog's resolved
/// <see cref="ModelDirectory"/> and the entry itself so the loader can compose
/// the absolute file path and read whatever metadata it needs from the entry.
/// </summary>
/// <param name="Entry">The catalog row that triggered the load.</param>
/// <param name="ModelDirectory">Absolute path to the catalog's models directory.</param>
/// <param name="PathResolver">
/// Resolver for per-model on-disk paths. Loaders prefer this over
/// hand-composing <see cref="ModelDirectory"/> + relative segments so the
/// catalog substrate's per-version folder flip is a one-line resolver swap
/// rather than a per-loader rewrite. <see langword="null"/> only for legacy
/// test fixtures that constructed a <see cref="ModelLoadContext"/> directly
/// — loaders should null-coalesce to the flat-layout fallback in that case.
/// </param>
public sealed record ModelLoadContext(
    ModelCatalogEntry Entry,
    string ModelDirectory,
    IModelPathResolver? PathResolver = null)
{
    /// <summary>
    /// Always-non-null view of <see cref="PathResolver"/>; falls back to a
    /// flat-layout resolver rooted at <see cref="ModelDirectory"/> for the
    /// handful of legacy test fixtures that constructed a load context
    /// without supplying a resolver. Loaders should prefer this over
    /// composing paths from <see cref="ModelDirectory"/> directly.
    /// </summary>
    public IModelPathResolver Paths => PathResolver ?? new FlatModelPathResolver(ModelDirectory);
}
