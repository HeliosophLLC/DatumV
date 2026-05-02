using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.ModelLibrary;
using DatumIngest.Models;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

internal sealed partial class RoutineRegistrar
{
    // ───────────────────── Models ─────────────────────

    /// <summary>
    /// Applies a <c>CREATE MODEL</c> statement: resolves the
    /// <c>USING</c> path, asks the inference dispatcher to load the
    /// bundle (eagerly — failures surface at CREATE-time rather than at
    /// the first call site), builds a <see cref="ModelDescriptor"/>, and
    /// registers it under the catalog's <see cref="TableCatalog.DeclaredModels"/>.
    /// Disposes any sessions previously bound under the same name when
    /// <c>OR REPLACE</c> is in effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>USING path resolution.</strong>
    /// <list type="bullet">
    ///   <item><description>Paths prefixed <c>file://</c> are treated as
    ///   absolute (the prefix is stripped). Useful for tests and
    ///   developer workflows where the ONNX file lives outside the
    ///   host's models directory.</description></item>
    ///   <item><description>All other paths are resolved against the
    ///   host's model directory (taken from
    ///   <c>TableCatalog.Models.ModelDirectory</c>). Throws when no
    ///   <see cref="DatumIngest.Models.ModelCatalog"/> is configured —
    ///   either supply <c>file://</c> or wire the model catalog before
    ///   issuing <c>CREATE MODEL</c>.</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>Persistence.</strong> SQL-defined models persist their
    /// verbatim CREATE MODEL source text in the catalog file (v5+).
    /// On startup, <see cref="TableCatalog.RehydrateModelsAsync"/> walks
    /// the persisted entries and re-invokes <see cref="ApplyCreateModelAsync"/>
    /// for each — the source text reparses, the USING path resolves
    /// against the current models directory, and the inference
    /// dispatcher reloads the bundle. Bound inference sessions are
    /// re-created at rehydrate time; they never travel across process
    /// boundaries.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The fixed schema all SQL-defined models live under. Built-in
    /// (ONNX / LlamaSharp / etc.) models also surface from this schema in
    /// <c>system.models</c>, so models are always addressable as
    /// <c>models.X</c> regardless of origin. CREATE MODEL refuses any other
    /// schema qualifier.
    /// </summary>
    private const string ModelsSchema = "models";

    public async Task ApplyCreateModelAsync(
        CreateModelStatement create, string? sourceText = null, bool suppressSave = false)
    {
        ValidateDefaultsContiguous(create.Parameters, $"CREATE MODEL {create.Name}");

        // Schema lockdown. CREATE MODEL always lands in `models`; explicit
        // qualifiers must match (case-insensitively) or be absent. This
        // mirrors how built-in models register — every model in the
        // catalog lives at `models.X`, and CREATE MODEL inherits that
        // namespacing rather than letting users scatter declarations
        // across `public`, custom schemas, etc.
        if (create.SchemaName is not null &&
            !string.Equals(create.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"CREATE MODEL {create.SchemaName}.{create.Name}: models must live in the " +
                $"'{ModelsSchema}' schema. Use 'CREATE MODEL {create.Name}' (lands in " +
                $"'{ModelsSchema}' implicitly) or 'CREATE MODEL {ModelsSchema}.{create.Name}' " +
                "(equivalent explicit form).");
        }

        if (_catalog.InferenceDispatcher is not Inference.IInferenceDispatcher dispatcher)
        {
            throw new InvalidOperationException(
                $"CREATE MODEL {create.Name}: no inference dispatcher is configured for this host. " +
                "Wire an IInferenceDispatcher via TableCatalog.InferenceDispatcher before issuing CREATE MODEL.");
        }

        // IMPLEMENTS validation happens before any inference-dispatcher work
        // — fast fail if the signature doesn't match the declared contract,
        // before paying load cost.
        if (create.ImplementsTaskName is { } taskName)
        {
            ValidateImplementsContract(create, taskName);
        }

        // Pass A + Pass B body-walk typecheck: when the declared return
        // type is a named struct (or Array<NamedStruct>), verify the
        // body's tail RETURN expression against the contract. Pass A
        // covers struct-literal returns directly; Pass B covers
        // variable-ref / UDF call / model call / CAST / array-literal
        // returns by walking the declared return-type annotations through
        // the registries.
        ValidateBodyReturnShape(create);

        // Arity / kind gate over the body's reachable function calls,
        // tracking DECLAREs so the typical model-body shape (chain
        // through Struct locals) gets covered. Fires here so a bad
        // inner call surfaces at CREATE MODEL rather than at the
        // first row through the dispatch path.
        ProceduralBodyArityGate.Enforce(
            create.StatementBody,
            create.Parameters,
            _functions,
            $"model {ModelsSchema}.{create.Name}");

        // Slice-D registration-time pre-flight: a CHECK that the parameter's
        // default value already violates should fail CREATE MODEL, not wait
        // for the first call site that happens to omit the override. Runs
        // before the (expensive) ONNX dispatcher load.
        await ValidateDefaultsAgainstChecksAsync(
            create.Parameters,
            $"CREATE MODEL {create.Name}",
            CancellationToken.None).ConfigureAwait(false);

        QualifiedName qn = new(ModelsSchema, create.Name);

        if (create.IfNotExists && _catalog.DeclaredModels.TryGet(qn, out _))
        {
            return;
        }

        // Build the (alias → resolved-path) map from one of three shapes:
        //   - Multi-file USING: every spec resolves to a separate session
        //   - Legacy single-file USING: one session keyed "default"
        //   - No USING clause: empty map → zero sessions bound. The
        //     model's body produces its result by delegating to another
        //     model or a UDF; any body that references a session alias
        //     surfaces a clear runtime error from LazyModelSessions
        //     because the alias is never declared.
        Dictionary<string, string> sessionPaths = new(StringComparer.Ordinal);
        List<ResolvedUsingFile>? resolvedFiles = null;
        string? primaryResolvedPath = null;
        if (create.UsingFiles is { Count: > 0 } usingFiles)
        {
            resolvedFiles = new List<ResolvedUsingFile>(usingFiles.Count);
            foreach (UsingFileSpec spec in usingFiles)
            {
                string resolved = ResolveUsingPath(spec.Path, create.Name);
                if (!File.Exists(resolved))
                {
                    throw new FileNotFoundException(
                        $"CREATE MODEL {create.Name}: model file not found at '{resolved}' " +
                        $"(USING '{spec.Path}' AS {spec.Alias}). " +
                        "Verify the path is correct relative to the host's model directory, " +
                        "or prefix with 'file://' for an absolute path.",
                        resolved);
                }
                sessionPaths[spec.Alias] = resolved;
                resolvedFiles.Add(new ResolvedUsingFile(spec.Path, spec.Alias, resolved));
            }
            primaryResolvedPath = resolvedFiles[0].ResolvedPath;
        }
        else if (create.UsingPath is { } usingPath)
        {
            primaryResolvedPath = ResolveUsingPath(usingPath, create.Name);
            if (!File.Exists(primaryResolvedPath))
            {
                throw new FileNotFoundException(
                    $"CREATE MODEL {create.Name}: model file not found at '{primaryResolvedPath}' " +
                    $"(USING '{usingPath}'). " +
                    "Verify the path is correct relative to the host's model directory, " +
                    "or prefix with 'file://' for an absolute path.",
                    primaryResolvedPath);
            }
            sessionPaths["default"] = primaryResolvedPath;
        }
        // else: delegating model — sessionPaths stays empty

        // Defer the ONNX session load until the body's first infer('alias', ...)
        // call. Eager LoadBundleAsync at registration time made catalog rehydration
        // (which replays every persisted CREATE MODEL on startup) load every
        // installed model's sessions before the host could serve requests — an
        // O(installed models) boot cost. With LazyModelSessions, registration
        // pays only path-resolution + AST cost; sessions land on first invoke
        // and stick around for subsequent calls.
        LazyModelSessions sessions = new(
            dispatcher,
            sessionPaths,
            bundleId: create.UsingPath is { } bundlePath
                ? $"{qn} (USING '{bundlePath}')"
                : $"{qn} (no USING)");

        // Stamp catalog provenance from the install context — set by
        // CatalogBackedModelInstaller during catalog-driven installs and by
        // TableCatalog.RehydrateModelsAsync during catalog-row rehydrate.
        // User-authored CREATE MODEL leaves the context unset, so these
        // stay null and the persisted row falls back to the source-text
        // shape on the next Save.
        string? installCatalogId = ModelInstallContext.CurrentCatalogId;
        string? installCatalogVersion = installCatalogId is null
            ? null
            : ModelInstallContext.CurrentVersionPin;
        string? installPinnedAs = installCatalogId is not null
            && ModelInstallContext.CurrentInstallIsPinned
            ? qn.Name
            : null;

        ModelDescriptor descriptor = new(
            SchemaName: qn.Schema,
            Name: qn.Name,
            Parameters: create.Parameters,
            ReturnTypeName: create.ReturnTypeName,
            UsingPath: create.UsingPath,
            ResolvedUsingPath: primaryResolvedPath,
            StatementBody: create.StatementBody,
            BoundSessions: sessions,
            ReturnIsNotNull: create.ReturnIsNotNull,
            SourceText: sourceText ?? $"CREATE MODEL {qn}",
            ImplementsTaskName: create.ImplementsTaskName,
            UsingFiles: resolvedFiles,
            CatalogId: installCatalogId,
            CatalogVersion: installCatalogVersion,
            PinnedAs: installPinnedAs);

        ModelDescriptor? displaced = _catalog.DeclaredModels.Register(
            descriptor, replace: create.OrReplace);

        // Wire the scalar dispatcher so `SELECT models.softmax_test(...)`
        // resolves to this model's body. Same pattern as the UDF adapter
        // path: the function registry holds the adapter under the model's
        // qualified name; OR REPLACE flips the entry atomically.
        RegisterModelAdapter(descriptor, replace: create.OrReplace);

        // Persist now so a process crash between CREATE MODEL completion
        // and a subsequent commit doesn't lose the registration. Skipped
        // during rehydration where the file already holds these entries
        // — re-saving would just rewrite identical bytes N times.
        if (!suppressSave)
        {
            _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);
        }

        if (displaced is null)
        {
            _catalog.Events.Raise(new ModelCreatedEvent(qn, descriptor, sourceText));
        }
        else
        {
            _catalog.Events.Raise(new ModelAlteredEvent(qn, displaced, descriptor, sourceText));
        }

        // OR REPLACE: dispose the previous descriptor's sessions after the
        // new one is in place. In-flight queries holding a reference to the
        // displaced descriptor will keep running on the now-disposed
        // sessions until they finish — same behaviour as OR REPLACE for
        // UDF macros where in-flight inlined references keep the old AST
        // alive until the query completes.
        if (displaced is not null)
        {
            DisposeSessions(displaced);
        }
    }

    /// <summary>
    /// Applies an <c>EVICT MODEL</c> statement: drops the model's
    /// currently resident <see cref="IModel"/> instance from the residency
    /// manager so its VRAM is freed. The catalog registration is left in
    /// place — the next query that references the model will trigger a
    /// fresh load through <see cref="ModelCatalog.AcquireAsync"/>.
    /// </summary>
    /// <remarks>
    /// Manual EVICT is the user-side companion to the residency manager's
    /// automatic LRU eviction. Useful when the user knows they're done
    /// with a big model (Llama, SDXL) and wants to free VRAM
    /// proactively without waiting for pressure to force eviction.
    /// </remarks>
    public void ApplyEvictModel(EvictModelStatement evict, string? sourceText = null)
    {
        // Schema lockdown mirrors DROP MODEL — models live exclusively
        // in the `models` schema, so any other explicit qualifier is a
        // user mistake we surface with a pointed error rather than a
        // silent no-op.
        if (evict.SchemaName is not null &&
            !string.Equals(evict.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"EVICT MODEL {evict.SchemaName}.{evict.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'EVICT MODEL {evict.Name}' or " +
                $"'EVICT MODEL {ModelsSchema}.{evict.Name}'.");
        }

        ModelResidencyManager? residency = _catalog.Models?.ResidencyManager;
        ModelResidencyManager.EvictResult result =
            residency?.TryEvictUnpinned(evict.Name) ?? ModelResidencyManager.EvictResult.NotResident;

        switch (result)
        {
            case ModelResidencyManager.EvictResult.Evicted:
                break;

            case ModelResidencyManager.EvictResult.NotResident:
                if (!evict.IfExists)
                {
                    throw new InvalidOperationException(
                        $"Model '{evict.Name}' is not currently resident. Use EVICT MODEL IF EXISTS " +
                        "to make this a no-op.");
                }
                break;

            case ModelResidencyManager.EvictResult.Pinned:
                // Always an error — IF EXISTS doesn't suppress this. The
                // user asked to evict a model that's actively dispatching;
                // they need to know it wasn't done so they can retry.
                throw new InvalidOperationException(
                    $"Model '{evict.Name}' is currently in use by one or more active queries " +
                    "and cannot be evicted. Wait for those queries to complete, then retry.");
        }

        _ = sourceText;
    }

    /// <summary>
    /// Applies a <c>RESET CALIBRATION</c> statement: removes the per-model
    /// VRAM calibration curve from <see cref="ModelCatalog.CalibrationRegistry"/>
    /// and triggers a fresh on-disk write on the next save tick. The
    /// model itself stays resident (if loaded) and registered; the next
    /// dispatch falls through to the calibration coordinator for
    /// re-measurement.
    /// </summary>
    public void ApplyResetCalibration(ResetCalibrationStatement reset, string? sourceText = null)
    {
        if (reset.SchemaName is not null &&
            !string.Equals(reset.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"RESET CALIBRATION {reset.SchemaName}.{reset.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'RESET CALIBRATION {reset.Name}' or " +
                $"'RESET CALIBRATION {ModelsSchema}.{reset.Name}'.");
        }

        DatumIngest.Models.Calibration.CalibrationRegistry? registry =
            _catalog.Models?.CalibrationRegistry;
        bool removed = registry?.Remove(reset.Name) ?? false;

        if (!removed && !reset.IfExists)
        {
            throw new InvalidOperationException(
                $"Model '{reset.Name}' has no calibration entry. Use RESET CALIBRATION IF EXISTS " +
                "to make this a no-op.");
        }

        _ = sourceText;
    }

    /// <summary>
    /// Applies a <c>DROP MODEL</c> statement: removes the descriptor from
    /// the registry and disposes its bound inference sessions.
    /// </summary>
    public void ApplyDropModel(DropModelStatement drop, string? sourceText = null)
    {
        // Same schema lockdown as CREATE MODEL: explicit qualifiers must
        // be the `models` schema or absent. Lookups always go straight to
        // `models` — there's no point walking search_path when models
        // can only exist in one place.
        if (drop.SchemaName is not null &&
            !string.Equals(drop.SchemaName, ModelsSchema, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"DROP MODEL {drop.SchemaName}.{drop.Name}: models live exclusively in the " +
                $"'{ModelsSchema}' schema. Use 'DROP MODEL {drop.Name}' or " +
                $"'DROP MODEL {ModelsSchema}.{drop.Name}'.");
        }

        QualifiedName qn = new(ModelsSchema, drop.Name);

        ModelDescriptor? removed = _catalog.DeclaredModels.Unregister(qn);
        if (removed is null)
        {
            if (!drop.IfExists)
            {
                throw new InvalidOperationException(
                    $"Model '{qn}' is not registered. Use DROP MODEL IF EXISTS to make this a no-op.");
            }
            return;
        }

        // Drop the scalar adapter so subsequent SELECT calls fail with a
        // clean "not registered" error rather than dispatching into a
        // descriptor whose sessions are about to be disposed.
        _functions.UnregisterScalar(qn.ToString());

        // Drop the ModelCatalog entry too — symmetric with the dual
        // registration in RegisterModelAdapter. Without this, MIO's hoister
        // would still see the entry and route hoisted call sites into a
        // ProceduralModelAdapter whose descriptor's sessions are about to
        // be disposed below.
        _catalog.Models?.Unregister(removed.Name);
        // Evict the residency cache so any newly-acquired lease that races
        // the disposal below doesn't latch onto the stale ProceduralModelAdapter.
        // EvictAlways defers the IModel disposal until any in-flight lease
        // drains — without that, an in-flight Session.Run would crash with
        // 0xC0000005 when the registrar disposes the descriptor's sessions
        // immediately below.
        _catalog.Models?.ResidencyManager.EvictAlways(removed.Name);

        _catalogStore?.Save(_udfs, _procedures, _catalog.DeclaredModels);

        _catalog.Events.Raise(new ModelDroppedEvent(qn, removed, sourceText));

        DisposeSessions(removed);
    }

    /// <summary>
    /// Resolves a <c>USING</c> path supplied to a <c>CREATE MODEL</c>
    /// statement against the host's model directory, honouring the
    /// <c>file://</c> escape for absolute paths. Thin wrapper around
    /// <see cref="ModelCatalog.ResolveFilePath"/> that adds the CREATE MODEL
    /// caller label.
    /// </summary>
    private string ResolveUsingPath(string usingPath, string modelName)
        => ModelCatalog.ResolveFilePath(
            usingPath, _catalog.Models, $"CREATE MODEL {modelName}");


    /// <summary>
    /// Best-effort disposal of every bound session in a descriptor.
    /// Disposal failures are logged via <see cref="Console.Error"/>
    /// rather than rethrown — the descriptor is already out of the
    /// registry and a thrown exception here would mask the real reason
    /// the descriptor was being released.
    /// </summary>
    private static void DisposeSessions(ModelDescriptor descriptor)
        => descriptor.BoundSessions.DisposeLoaded();

    /// <summary>
    /// Registers (or replaces) the SQL-defined model on two surfaces:
    /// <list type="bullet">
    ///   <item><description>
    ///     The scalar function registry — the <see cref="ProceduralModelFunction"/>
    ///     adapter satisfies any call site the planner didn't hoist into a
    ///     <c>ModelInvocationOperator</c> (e.g. inside a UDF body, inside
    ///     an unhoisted clause). The hoister prefers MIO for top-level
    ///     <c>models.X(...)</c> calls; the scalar dispatch is the fallback.
    ///   </description></item>
    ///   <item><description>
    ///     The <see cref="ModelCatalog"/> via a <see cref="ProceduralModelAdapter"/>
    ///     wrapped in a <see cref="ModelCatalogEntry"/>. The hoister consults
    ///     this catalog at plan time; once the SQL-defined model has an entry
    ///     here, top-level call sites lift into MIO and inherit operator-
    ///     boundary parity with built-in models (tracer, residency lease,
    ///     RowLimit short-circuit, streaming-sink awareness, sub-batching).
    ///   </description></item>
    /// </list>
    /// Both surfaces stay in sync: <c>OR REPLACE</c> replaces both atomically,
    /// <c>DROP MODEL</c> tears down both.
    /// </summary>
    private void RegisterModelAdapter(ModelDescriptor descriptor, bool replace)
    {
        ProceduralModelFunction scalarAdapter = new(descriptor, _functions);
        FunctionDescriptor catalogDescriptor = BuildModelFunctionDescriptor(descriptor);
        _functions.RegisterScalarInstance(
            descriptor.QualifiedName.ToString(),
            scalarAdapter,
            descriptor: catalogDescriptor,
            replace: replace);

        ModelCatalog? models = _catalog.Models;
        if (models is null) return;

        ProceduralModelAdapter iModelAdapter = new(descriptor, _catalog);
        // Estimate VRAM as on-disk file size × 1.2 — same heuristic the
        // C# builtin path uses (ModelResidencyManager.DefaultFileSizeMultiplier).
        // Weights dominate the resident footprint; the 20% slack covers ORT's
        // session metadata + per-input/output tensor buffers. Without this,
        // EstimatedVramBytes was 0, making every SQL-defined model invisible
        // to the residency manager's admission control — multiple models would
        // happily co-load past dedicated VRAM and spill into shared memory,
        // which the NVIDIA driver mishandles into native crashes inside
        // InferenceSession.Run.
        //
        // Delegating models (no USING) have no weights of their own — the
        // delegated model's residency accounting owns the VRAM cost, so
        // this entry's estimate stays at 0.
        long estimatedVram = descriptor.ResolvedUsingPath is { } weightsPath
            ? EstimateFileSizeBytes(weightsPath)
            : 0L;
        ModelCatalogEntry entry = new(
            Name: descriptor.Name,
            Backend: "sql",
            RelativePath: null,
            InputKinds: iModelAdapter.InputKinds,
            OutputKind: iModelAdapter.OutputKind,
            IsDeterministic: iModelAdapter.IsDeterministic,
            // Pre-warm every bound session synchronously before returning
            // the adapter. ProceduralModelAdapter's constructor records
            // metadata only — the actual ONNX session loads lazily on
            // first infer() call inside the body. Without this warm-up
            // the residency manager's RecordWeightCost would measure
            // VRAM before/after an effectively-empty loader call and
            // report weight_cost = 0 → NULL in system.models. Shifting
            // the load cost forward to the loader call (a) makes the
            // weight-cost measurement see real VRAM growth, and (b)
            // doesn't add any net work — the first inference would have
            // paid the same cost. Multi-session bundles load every
            // alias up-front; for typical SQL-defined models every
            // alias is referenced per-inference anyway.
            Loader: _ =>
            {
                foreach (string alias in descriptor.BoundSessions.Keys)
                {
                    descriptor.BoundSessions
                        .ResolveAsync(alias, CancellationToken.None)
                        .AsTask()
                        .GetAwaiter()
                        .GetResult();
                }
                return iModelAdapter;
            },
            OptionalArgKinds: iModelAdapter.OptionalKinds.Count > 0 ? iModelAdapter.OptionalKinds : null,
            EstimatedVramBytes: estimatedVram,
            DisplayName: descriptor.QualifiedName.ToString(),
            Batchable: iModelAdapter.IsBatchable,
            // Threads the resolved ONNX path through to the calibration
            // layer so RecordWeightCost can fingerprint SQL-defined
            // models the same way it fingerprints builtins. Without
            // this, every SQL-defined model's system.models row would
            // surface weight_cost_bytes = NULL because the residency
            // manager's RecordWeightCost short-circuits on a null
            // RelativePath. Use the same fingerprint regardless of
            // multi-file bundles — the primary ResolvedUsingPath is
            // the anchor weights file the planner already treats as
            // the model's canonical identity.
            FingerprintPath: descriptor.ResolvedUsingPath,
            // Preserve the RETURNS-clause array bit and the user-declared
            // parameter shapes so the LanguageServer manifest can render
            // `img: Image` / `→ Array<Float32>` instead of the lossy
            // `input: <kind>` / `→ <element kind>` defaults that
            // ModelCatalogEntry's name/kind-only fields would produce.
            OutputIsArray: iModelAdapter.OutputIsArray,
            ParameterInfos: BuildModelParameterInfos(descriptor),
            // Struct-output models can declare their field shapes via
            // `RETURNS Struct<depth Array<Float32>, intrinsics Array<Float32>>`.
            // Extract the field list here so the LanguageServer can resolve
            // `model_call().depth` to its actual `Array<Float32>` kind
            // instead of the opaque `Struct` placeholder. Bare
            // `RETURNS Struct` returns null and the LS treats it as opaque.
            OutputStructFields: BuildModelOutputStructFields(descriptor));

        if (replace)
        {
            models.Unregister(descriptor.Name);
            // Drop the cached IModel for this name so AcquireAsync re-runs
            // the loader against the new ModelCatalogEntry. Without this,
            // the residency manager keeps handing back the displaced
            // ProceduralModelAdapter whose descriptor's sessions are about
            // to be disposed by DisposeSessions(displaced) — every
            // subsequent invocation would then fault inside Session.Run
            // with "Cannot access a disposed object". EvictAlways defers
            // the displaced IModel's disposal until in-flight leases drain.
            models.ResidencyManager.EvictAlways(descriptor.Name);
        }
        models.Register(entry);
    }

    /// <summary>
    /// Synthesises a <see cref="FunctionDescriptor"/> for a SQL-defined
    /// model so the type resolver can read its return shape (including
    /// <c>Array&lt;T&gt;</c> returns) via the standard per-signature
    /// path. Mirrors the UDF analog
    /// (<see cref="BuildProceduralDescriptor(UdfDescriptor)"/>): parameter
    /// kinds use <see cref="DataKindMatcher.Any"/> because the adapter
    /// does its own arity check; the synthesised signature carries the
    /// return rule, not gating logic.
    /// </summary>
    private static FunctionDescriptor BuildModelFunctionDescriptor(ModelDescriptor model)
    {
        DataKind returnKind = DataKind.String;
        bool returnIsArray = false;
        if (model.ReturnTypeName is not null)
        {
            TypeAnnotationResolver.TryParse(model.ReturnTypeName, out returnKind, out returnIsArray);
        }

        ReturnTypeRule returnRule = returnIsArray
            ? ReturnTypeRule.ArrayOf(ReturnTypeRule.Constant(returnKind))
            : ReturnTypeRule.Constant(returnKind);

        ParameterSpec[] parameters = new ParameterSpec[model.Parameters.Count];
        for (int i = 0; i < model.Parameters.Count; i++)
        {
            UdfParameter p = model.Parameters[i];
            // Resolve the declared type into a kind + array-ness so hover /
            // signature help / completion show the actual annotation
            // (<c>img: Image</c>) instead of the wildcard <c>img: Any</c>
            // we used to emit. Unrecognised type names fall back to
            // <c>Any</c> rather than throwing — CREATE MODEL already
            // validates the annotation at registration time, so the
            // fallback is purely defensive.
            DataKindMatcher matcher = DataKindMatcher.Any;
            ArrayMatch arrayMatch = ArrayMatch.Either;
            if (TypeAnnotationResolver.TryParse(p.TypeName, out DataKind kind, out bool isArray))
            {
                matcher = DataKindMatcher.Exact(kind);
                arrayMatch = isArray ? ArrayMatch.Array : ArrayMatch.Scalar;
            }
            parameters[i] = new ParameterSpec(
                p.Name,
                matcher,
                IsOptional: p.Default is not null,
                IsArray: arrayMatch,
                Metadata: BuildParameterMetadata(p));
        }

        return new FunctionDescriptor(
            PrimaryName: model.Name,
            Aliases: Array.Empty<string>(),
            Category: FunctionCategory.Utility,
            Description: $"SQL-defined model {model.QualifiedName}.",
            Signatures:
            [
                new FunctionSignatureVariant(parameters, VariadicTrailing: null, ReturnType: returnRule),
            ]);
    }

    /// <summary>
    /// Builds the per-parameter metadata snapshot attached to a SQL-defined
    /// model's <see cref="ModelCatalogEntry"/>. Unlike the function-descriptor
    /// path (which expresses kinds as matcher / arity), this is a plain
    /// name + kind + shape list — what the language server needs to render
    /// hover / signature / completion popups. Returns <see langword="null"/>
    /// when the descriptor has no parameters so the manifest builder can
    /// fall back to its generic <c>input</c>/<c>inputN</c> labels.
    /// </summary>
    private static IReadOnlyList<ModelParameterInfo>? BuildModelParameterInfos(ModelDescriptor model)
    {
        if (model.Parameters.Count == 0) return null;
        ModelParameterInfo[] infos = new ModelParameterInfo[model.Parameters.Count];
        for (int i = 0; i < model.Parameters.Count; i++)
        {
            UdfParameter p = model.Parameters[i];
            // CREATE MODEL validated the annotation at registration, but we
            // defensively fall back to a non-array Unknown kind on parse
            // failure rather than throwing — manifest rendering should
            // never break model registration.
            DataKind kind = DataKind.Unknown;
            bool isArray = false;
            if (TypeAnnotationResolver.TryParse(p.TypeName, out DataKind parsedKind, out bool parsedIsArray))
            {
                kind = parsedKind;
                isArray = parsedIsArray;
            }
            infos[i] = new ModelParameterInfo(p.Name, kind, isArray, IsOptional: p.Default is not null);
        }
        return infos;
    }

    /// <summary>
    /// Parses a SQL-defined model's <c>RETURNS Struct&lt;…&gt;</c> annotation
    /// into ordered <see cref="ModelStructFieldInfo"/> entries the
    /// LanguageServer can use to resolve <c>model_call().field</c> hovers.
    /// Returns <see langword="null"/> when the annotation is the opaque
    /// bare <c>Struct</c>, when it's a non-Struct return, or when parsing
    /// fails — every fallback keeps the caller from emitting bogus field
    /// metadata.
    /// </summary>
    private static IReadOnlyList<ModelStructFieldInfo>? BuildModelOutputStructFields(ModelDescriptor model)
    {
        if (model.ReturnTypeName is null) return null;
        if (!StructTypeAnnotation.TryParse(model.ReturnTypeName, out IReadOnlyList<StructFieldShape> fields))
        {
            return null;
        }

        ModelStructFieldInfo[] infos = new ModelStructFieldInfo[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            StructFieldShape f = fields[i];
            DataKind kind = DataKind.Unknown;
            bool isArray = false;
            if (TypeAnnotationResolver.TryParse(f.Kind, out DataKind parsedKind, out bool parsedIsArray))
            {
                kind = parsedKind;
                isArray = parsedIsArray;
            }
            // Preserve the raw kind label from the struct annotation so
            // dim suffixes (`Array<Float32>(518, 518)`) survive into the
            // manifest. The structured DataKind/IsArray path drops them
            // because TypeAnnotationResolver returns the shape via a
            // separate out-param the LS doesn't read today.
            infos[i] = new ModelStructFieldInfo(f.Name, kind, isArray, KindLabel: f.Kind);
        }
        return infos;
    }
}
