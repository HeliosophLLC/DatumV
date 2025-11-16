using DatumIngest.Model;

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
/// Demo 0.5 / Phase A registers entries via a hardcoded
/// <c>BuiltinModels.Register(catalog)</c>. The eventual <c>CREATE MODEL</c> SQL
/// statement will be a second caller of the same registration API, populating
/// the same shape from user input.
/// </para>
/// </remarks>
/// <param name="Name">
/// Stable identifier without a namespace prefix. The function call <c>models.classify(x)</c>
/// resolves <c>Name == "classify"</c>.
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
/// <c>overrides</c> parameter to <see cref="IModel.InferBatchAsync"/>.
/// Defaults to <see langword="null"/> (no per-call overrides).
/// </param>
public sealed record ModelCatalogEntry(
    string Name,
    string Backend,
    string? RelativePath,
    IReadOnlyList<DataKind> InputKinds,
    DataKind OutputKind,
    bool IsDeterministic,
    Func<ModelLoadContext, IModel> Loader,
    IReadOnlyList<DataKind>? OptionalArgKinds = null);

/// <summary>
/// Context handed to a <see cref="ModelCatalogEntry.Loader"/> when first instantiating
/// the underlying <see cref="IModel"/>. Exposes the catalog's resolved
/// <see cref="ModelDirectory"/> and the entry itself so the loader can compose
/// the absolute file path and read whatever metadata it needs from the entry.
/// </summary>
/// <param name="Entry">The catalog row that triggered the load.</param>
/// <param name="ModelDirectory">Absolute path to the catalog's models directory.</param>
public sealed record ModelLoadContext(ModelCatalogEntry Entry, string ModelDirectory);
