using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.DatasetLibrary;
using Heliosoph.DatumV.Execution.Contexts;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

/// <summary>
/// Builds a <see cref="LanguageServerManifest"/> from a live <see cref="TableCatalog"/>
/// and <see cref="FunctionRegistry"/>. Used by the interactive shell to feed the
/// language server's completion / diagnostics / hover providers without going
/// through the offline JSON manifest workflow.
/// </summary>
/// <remarks>
/// Function signatures emit only names plus their category flags (aggregate /
/// window / table-valued); parameter lists, return types, and descriptions are
/// not currently exposed by the runtime <see cref="FunctionRegistry"/>, so those
/// fields stay empty / null. The completion provider only needs names to suggest
/// candidates — richer metadata can come later if hover docs are wired up.
/// </remarks>
public static class CatalogManifestBuilder
{
    /// <summary>
    /// Constructs a manifest snapshotting every registered table's name and
    /// schema, plus every registered function name in the given registry.
    /// When <paramref name="datasetBinder"/> is supplied, the manifest also
    /// carries a <c>Datasets</c> list covering installed + discovered
    /// variants from the dataset catalog so the LS can render rich hover
    /// + autocomplete the uninstalled-yet shape.
    /// </summary>
    public static LanguageServerManifest Build(
        TableCatalog catalog,
        FunctionRegistry functions,
        DatasetSchemaBinder? datasetBinder = null)
    {
        List<TableSchemaEntry> tables = new();
        foreach (ITableProvider provider in catalog)
        {
            Schema schema = provider.GetSchema();
            List<TableColumnEntry> columns = new(schema.Columns.Count);
            foreach (ColumnInfo column in schema.Columns)
            {
                columns.Add(new TableColumnEntry
                {
                    Name = column.Name,
                    Kind = column.Kind.ToString(),
                    Nullable = column.Nullable,
                });
            }
            tables.Add(new TableSchemaEntry
            {
                Name = provider.QualifiedName.ToString(),
                Columns = columns,
            });
        }

        // Surface registered views as table-shaped entries so FROM-clause
        // completion, hover, and semantic analysis see them with the same
        // affordances as base tables. Column resolution goes through
        // QuerySchemaResolver — same path the engine uses for column-name
        // checks elsewhere — and best-effort-degrades to an empty list when
        // the body references something not yet on the catalog.
        //
        // Using ResolveProjectionAsync (not ResolveAsync) so the surfaced
        // columns are the view's declared projection — what queries against
        // the view actually see — rather than the wider FROM/JOIN column
        // set the body happens to read from.
        QuerySchemaResolver viewResolver = new(catalog, functions);
        foreach (ViewDescriptor view in catalog.Views.Entries)
        {
            IReadOnlyList<TableColumnEntry> viewColumns;
            try
            {
                ResolvedQuerySchema resolved = viewResolver
                    .ResolveProjectionAsync(view.Body, view.QualifiedName.ToString(), CancellationToken.None)
                    .GetAwaiter().GetResult();
                List<TableColumnEntry> columns = new(resolved.Columns.Count);
                foreach (ResolvedColumn col in resolved.Columns)
                {
                    columns.Add(new TableColumnEntry
                    {
                        Name = col.ColumnName,
                        Kind = col.IsArray ? $"Array<{col.Kind}>" : col.Kind.ToString(),
                        Nullable = col.Nullable,
                    });
                }
                viewColumns = columns;
            }
            catch (Exception)
            {
                // A view whose body can't currently be resolved (missing
                // dependency, circular reference, parse drift) still
                // surfaces as a name — completion is the immediate win,
                // and columns will populate on the next manifest build
                // once the dependency lands.
                viewColumns = Array.Empty<TableColumnEntry>();
            }

            tables.Add(new TableSchemaEntry
            {
                Name = view.QualifiedName.ToString(),
                Columns = viewColumns,
                Kind = "VIEW",
            });
        }

        List<FunctionSignature> functionSigs = new();

        // Scalar functions carry static-abstract metadata (IFunction.Signatures)
        // captured at registration time as FunctionDescriptor.Signatures. Pick
        // the first variant — most functions have one shape; multi-variant
        // functions still surface the most common form, enough for the
        // completion popup's first hint. Aliases get their own entry so
        // the editor offers them under their typed name.
        //
        // Body-scoped functions (today: `infer()`) are excluded from the
        // language-server manifest. They're only callable inside a CREATE
        // MODEL body — surfacing them in a regular query's completion
        // dropdown would mislead. Users discover them via
        // `SELECT * FROM system.functions WHERE body_scope = 'modelbody'`,
        // and the plan-time gate refuses out-of-context call sites with a
        // CREATE-MODEL-pointing error if anyone types them by hand.
        // A future Tier 2 could re-include them when the editor zone is
        // detected to be inside a model body; until then, omit.
        foreach (FunctionDescriptor descriptor in functions.ScalarDescriptors)
        {
            if (descriptor.BodyScope != BodyScopeRequirement.None)
            {
                continue;
            }

            FunctionSignature primary = BuildSignatureFromDescriptor(descriptor.PrimaryName, descriptor);
            functionSigs.Add(primary);

            foreach (string alias in descriptor.Aliases)
            {
                functionSigs.Add(BuildSignatureFromDescriptor(alias, descriptor));
            }
        }

        // Aggregate functions carry static-abstract metadata
        // (IAggregateFunctionMetadata.Signatures) captured at registration
        // time as AggregateFunctionDescriptor.Signatures. Mirror the scalar
        // path: emit one entry per primary name plus one per alias so
        // completion offers each surfaced spelling.
        foreach (AggregateFunctionDescriptor descriptor in functions.AggregateDescriptors)
        {
            functionSigs.Add(BuildAggregateSignatureFromDescriptor(descriptor.PrimaryName, descriptor));
            foreach (string alias in descriptor.Aliases)
            {
                functionSigs.Add(BuildAggregateSignatureFromDescriptor(alias, descriptor));
            }
        }

        // Window functions don't yet carry static signature metadata —
        // register-by-instance only — so their argument lists stay empty
        // until the window registry gains descriptors.
        foreach (QualifiedName qn in functions.WindowFunctionQualifiedNames)
        {
            functionSigs.Add(new FunctionSignature { SchemaName = qn.Schema, Name = qn.Name, Parameters = [], IsWindowFunction = true });
        }

        // Table-valued functions carry static-abstract metadata via
        // ITableValuedFunctionMetadata.Signatures captured at registration
        // time as TableValuedFunctionDescriptor.Signatures. Render full
        // parameter list + (when fixed) the output schema so hover /
        // signature help land with real detail rather than a bare name.
        HashSet<QualifiedName> tvfWithDescriptor = new();
        foreach (TableValuedFunctionDescriptor descriptor in functions.TableValuedDescriptors)
        {
            tvfWithDescriptor.Add(new QualifiedName(descriptor.SchemaName, descriptor.PrimaryName));
            functionSigs.Add(BuildTvfSignatureFromDescriptor(descriptor));
        }
        // Fallback for any TVF registered through the instance-only overload
        // (no static metadata available) — surface as a bare entry so
        // completion still finds the name.
        foreach (QualifiedName qn in functions.TableValuedFunctionQualifiedNames)
        {
            if (tvfWithDescriptor.Contains(qn)) continue;
            functionSigs.Add(new FunctionSignature { SchemaName = qn.Schema, Name = qn.Name, Parameters = [], IsTableValued = true });
        }

        // Keywords list = clause keywords + bool/null literals. Type names and
        // date-part names are intentionally not in this list — Monaco draws
        // them from separate Monarch arrays for distinct coloring, and the
        // completion provider can pick them up via its own logic.
        List<string> keywords = new(SqlKeywordRegistry.ClauseKeywords.Count + SqlKeywordRegistry.BoolNullKeywords.Count);
        keywords.AddRange(SqlKeywordRegistry.ClauseKeywords);
        keywords.AddRange(SqlKeywordRegistry.BoolNullKeywords);

        // Models registered in the catalog's ModelCatalog flow into the
        // manifest so the language server can offer `models.<name>(...)`
        // completions and surface metadata in hover tooltips. Null when no
        // model catalog is attached to the table catalog.
        IReadOnlyList<ModelEntry>? models = null;
        if (catalog.Models is not null)
        {
            Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver = catalog.Models.PathResolver;
            List<ModelEntry> modelEntries = new(catalog.Models.Entries.Count);
            HashSet<string> seenIdentifiers = new(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, Models.ModelCatalogEntry> entry in catalog.Models.Entries)
            {
                // Output shape: preserve the array marker the catalog
                // entry already captured. Without this, `RETURNS Array<Float32>`
                // would surface as just `Float32` in the model-completion
                // popup and the model-call signature help. Struct returns
                // with declared field shapes (`RETURNS Struct<depth ...,
                // intrinsics ...>`) render with the full field list so the
                // hover popup names every projected output.
                string outputKindLabel = BuildModelOutputKindLabel(entry.Value);
                IReadOnlyList<StructFieldSignature>? structFields =
                    BuildModelOutputStructFieldSignatures(entry.Value);
                ModelInstallStatus status = ResolveInstallStatus(entry.Value, pathResolver);
                // Pull task contracts from the vocabulary when the identifier
                // is catalog-declared. Engine-only builtins (the 22 hardcoded
                // C# registrations not in catalog.json) have no vocab entry
                // and surface with Tasks=null — completion falls back to
                // [Category] rendering for them.
                IReadOnlyList<string>? tasks = null;
                string? activeVersion = null;
                string? latestVersion = null;
                string? catalogEntryId = null;
                if (catalog.CatalogVocabulary is { } vocabForRegistered
                    && vocabForRegistered.ByIdentifier.TryGetValue(entry.Value.Name, out ModelLibrary.CatalogVocabularyEntry? vocabRegEntry))
                {
                    tasks = vocabRegEntry.OwnerEntry.Tasks;
                    activeVersion = pathResolver.GetActiveVersion(vocabRegEntry.OwnerVariant.Id);
                    latestVersion = vocabRegEntry.OwnerVariant.Versions[0].Version;
                    catalogEntryId = vocabRegEntry.OwnerVariant.Id;
                }
                modelEntries.Add(new ModelEntry
                {
                    Name = entry.Value.Name,
                    Status = status,
                    OutputKind = outputKindLabel,
                    Category = entry.Value.Category,
                    Backend = entry.Value.Backend,
                    DisplayName = entry.Value.DisplayName,
                    Parameters = BuildModelParameters(entry.Value),
                    OutputStructFields = structFields,
                    Tasks = tasks,
                    CatalogEntryId = catalogEntryId,
                    ActiveVersion = activeVersion,
                    LatestVersion = latestVersion,
                });
                seenIdentifiers.Add(entry.Value.Name);
            }

            // Union in catalog-declared identifiers with no live registration
            // — the "discovered" tier from system.models. Lets autocomplete
            // surface every model the catalog ships, dimmed, before its
            // weights are on disk. Calling one trips parse-time pre-flight
            // and prompts an install. Parameters / output shape are
            // unavailable until installSql runs, so they stay empty here;
            // the install modal carries enough metadata to drive the UX.
            if (catalog.CatalogVocabulary is { } vocab)
            {
                foreach ((string identifier, ModelLibrary.CatalogVocabularyEntry vocabEntry) in vocab.ByIdentifier)
                {
                    if (seenIdentifiers.Contains(identifier)) continue;
                    modelEntries.Add(new ModelEntry
                    {
                        Name = identifier,
                        Status = ModelInstallStatus.Discovered,
                        DisplayName = vocabEntry.OwnerVariant.DisplayName,
                        Parameters = Array.Empty<ParameterSignature>(),
                        Tasks = vocabEntry.OwnerEntry.Tasks,
                        CatalogEntryId = vocabEntry.OwnerVariant.Id,
                        ActiveVersion = pathResolver.GetActiveVersion(vocabEntry.OwnerVariant.Id),
                        LatestVersion = vocabEntry.OwnerVariant.Versions[0].Version,
                    });
                }
            }

            models = modelEntries;

            // Back-fill OutputStructFields onto the model's scalar-function
            // FunctionSignature too — SQL-defined models are dual-registered
            // (FunctionRegistry + ModelCatalog), and HoverProvider's
            // `models.X(...)` lookup goes through Functions, not Models.
            // Without this pass, hover would still see opaque `Struct`
            // through that path even though ModelEntry has full detail.
            BackfillModelStructFieldsOntoFunctionSignatures(functionSigs, modelEntries);
        }

        // UDFs and procedures registered in the catalog flow into the
        // manifest so the language server can resolve `schema.fn(...)` /
        // `CALL schema.proc(...)` qualified call sites, walk search_path
        // for unqualified calls, and surface signature info in hover
        // tooltips. Always non-null on a live catalog (registries exist
        // from construction); empty lists when nothing is registered.
        List<UdfEntry> udfEntries = new(catalog.Udfs.Entries.Count);
        foreach (UdfDescriptor descriptor in catalog.Udfs.Entries)
        {
            udfEntries.Add(new UdfEntry
            {
                SchemaName = descriptor.SchemaName,
                Name = descriptor.Name,
                ReturnType = descriptor.ReturnTypeName,
                BodyKind = descriptor.IsProcedural ? "procedural" : "macro",
                IsPure = descriptor.IsPure,
                Parameters = BuildUdfParameters(descriptor),
            });
        }

        List<ProcedureEntry> procedureEntries = new(catalog.Procedures.Entries.Count);
        foreach (ProcedureDescriptor descriptor in catalog.Procedures.Entries)
        {
            procedureEntries.Add(new ProcedureEntry
            {
                SchemaName = descriptor.SchemaName,
                Name = descriptor.Name,
                Parameters = BuildProcedureParameters(descriptor),
            });
        }

        IReadOnlyList<Manifest.DatasetEntry>? datasetEntries =
            datasetBinder is null ? null : BuildDatasetEntries(datasetBinder);

        return new LanguageServerManifest
        {
            Tables = tables,
            Functions = functionSigs,
            Keywords = keywords,
            Models = models,
            Udfs = udfEntries,
            Procedures = procedureEntries,
            Datasets = datasetEntries,
            // Engine-side named-type vocabulary shipped to the LS so
            // hover / completion paths can resolve `LabeledDetection` /
            // `BoundingBox` / etc. into their struct field maps without
            // the LSP assembly taking a reference on the engine. Snapshot
            // — entries are static, so this list is effectively constant
            // across builds.
            NamedTypes = BuildNamedTypeEntries(),
            // Capture the catalog's current search_path so the LSP can
            // resolve unqualified names against the same precedence the
            // engine uses at execution time. Snapshot — subsequent
            // `SET search_path` calls won't bleed into this manifest;
            // re-build for an updated view.
            SearchPath = catalog.SearchPath,
            // Snapshot the built-in function-context registry so the LS
            // can resolve lambda parameter scopes (animation, particle, ...)
            // for hover + completion. The runtime registry isn't accessible
            // through TableCatalog today; using CreateDefault matches the
            // engine's startup wiring and covers every shipped context.
            FunctionContexts = BuildFunctionContextEntries(),
        };
    }

    private static IReadOnlyList<Manifest.DatasetEntry> BuildDatasetEntries(DatasetSchemaBinder binder)
    {
        List<Manifest.DatasetEntry> entries = new();
        foreach (DatasetSchemaBinder.DatasetBindingDescriptor desc in binder.EnumerateBindings())
        {
            entries.Add(new Manifest.DatasetEntry
            {
                Schema = desc.Schema,
                Name = desc.Table,
                VariantId = desc.VariantId,
                EntryName = desc.EntryName,
                DisplayName = desc.DisplayName,
                Version = desc.Version,
                Modalities = desc.Modalities,
                LicenseIds = desc.LicenseIds,
                ApproxArchiveBytes = desc.ApproxArchiveBytes,
                ApproxIngestedBytes = desc.ApproxIngestedBytes,
                Status = desc.IsInstalled
                    ? DatasetInstallStatus.Installed
                    : DatasetInstallStatus.Discovered,
            });
        }
        return entries;
    }

    /// <summary>
    /// Snapshots the engine's built-in <see cref="FunctionContextRegistry"/>
    /// into <see cref="FunctionContextEntry"/> records for the manifest.
    /// Mirrors the runtime registry's defaults so the LS sees the same
    /// scope graph the executor uses.
    /// </summary>
    private static IReadOnlyList<FunctionContextEntry> BuildFunctionContextEntries()
    {
        FunctionContextRegistry registry = FunctionContextRegistry.CreateDefault();
        List<FunctionContextEntry> entries = new(registry.Names.Count);
        foreach (string name in registry.Names)
        {
            FunctionContextDescriptor? descriptor = registry.TryGet(name);
            if (descriptor is null) continue;
            List<LambdaParameterEntry> parameters = new(descriptor.Parameters.Count);
            foreach (LambdaParameterSpec spec in descriptor.Parameters)
            {
                parameters.Add(new LambdaParameterEntry
                {
                    Name = spec.Name,
                    Kind = spec.Kind.ToString(),
                });
            }
            entries.Add(new FunctionContextEntry
            {
                Name = descriptor.Name,
                Parameters = parameters,
                ParentName = descriptor.ParentName,
                Borrows = new List<string>(descriptor.Borrows),
            });
        }
        return entries;
    }

    /// <summary>
    /// Builds the positional parameter list for a procedure entry. Mirrors
    /// <see cref="BuildUdfParameters"/> — procedures and UDFs share the
    /// same <see cref="UdfParameter"/> parameter shape.
    /// </summary>
    private static IReadOnlyList<ParameterSignature> BuildProcedureParameters(ProcedureDescriptor descriptor)
    {
        if (descriptor.Parameters.Count == 0) return Array.Empty<ParameterSignature>();
        ParameterSignature[] sigs = new ParameterSignature[descriptor.Parameters.Count];
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            UdfParameter p = descriptor.Parameters[i];
            sigs[i] = new ParameterSignature
            {
                Name = p.Name,
                Kind = p.TypeName,
                IsOptional = p.Default is not null,
                StructFields = ParameterStructFieldResolver.TryResolveSignatures(p.TypeName),
            };
        }
        return sigs;
    }

    /// <summary>
    /// Builds the positional parameter list for a UDF entry. Parameters
    /// with a non-null <c>Default</c> expression are flagged optional —
    /// the inliner falls back to the default at the call site when the
    /// argument is omitted.
    /// </summary>
    private static IReadOnlyList<ParameterSignature> BuildUdfParameters(UdfDescriptor descriptor)
    {
        if (descriptor.Parameters.Count == 0) return Array.Empty<ParameterSignature>();
        ParameterSignature[] sigs = new ParameterSignature[descriptor.Parameters.Count];
        for (int i = 0; i < descriptor.Parameters.Count; i++)
        {
            UdfParameter p = descriptor.Parameters[i];
            sigs[i] = new ParameterSignature
            {
                Name = p.Name,
                Kind = p.TypeName,
                IsOptional = p.Default is not null,
                StructFields = ParameterStructFieldResolver.TryResolveSignatures(p.TypeName),
            };
        }
        return sigs;
    }

    /// <summary>
    /// Builds a <see cref="FunctionSignature"/> for one scalar function
    /// (re-used for the primary name and each alias). Reads the first
    /// signature variant — most functions have one shape, and the editor
    /// only needs a single hint.
    /// </summary>
    private static FunctionSignature BuildSignatureFromDescriptor(
        string surfacedName, FunctionDescriptor descriptor)
    {
        FunctionSignatureVariant? first = descriptor.Signatures.Count > 0
            ? descriptor.Signatures[0]
            : null;

        List<ParameterSignature> parameters = new();
        string? returnType = null;

        if (first is not null)
        {
            foreach (ParameterSpec spec in first.Parameters)
            {
                parameters.Add(new ParameterSignature
                {
                    Name = spec.Name,
                    Kind = DescribeParameterShape(spec.Kind, spec.IsArray),
                    IsOptional = spec.IsOptional,
                    LambdaContextName = (spec.Kind as LambdaMatcher)?.ContextName,
                    EnumValues = (spec.Kind as StringEnumMatcher)?.Values,
                });
            }
            if (first.VariadicTrailing is not null)
            {
                // Render the variadic as a single optional positional named
                // with an ellipsis prefix; the popup's `name: kind?` format
                // makes the variadic shape obvious without a new field.
                parameters.Add(new ParameterSignature
                {
                    Name = $"...{first.VariadicTrailing.Name}",
                    Kind = DescribeParameterShape(first.VariadicTrailing.Kind, first.VariadicTrailing.IsArray),
                    IsOptional = first.VariadicTrailing.MinOccurrences == 0,
                    LambdaContextName = (first.VariadicTrailing.Kind as LambdaMatcher)?.ContextName,
                    EnumValues = (first.VariadicTrailing.Kind as StringEnumMatcher)?.Values,
                });
            }

            // Static return kind when the rule reports one; otherwise fall
            // back to the rule's textual description ("same as argument 0",
            // a custom blurb, …) so users still see *something*.
            // Two render paths depending on whether a StaticHint is
            // available:
            //
            //   StaticHint set → the hint is the bare element kind
            //   (e.g. `Float32`). Wrap in `Array<…>` ourselves when
            //   ProducesArray is true.
            //
            //   StaticHint null → fall back to `Describe()`, which is a
            //   self-describing string that ALREADY includes the
            //   `Array<…>` wrapper for ArrayOf-typed rules. Don't wrap
            //   again — wrapping a `Describe()` whose text was
            //   `Array<custom-blurb>` produced
            //   `Array<Array<custom-blurb>>` and caused every `array()`
            //   call site (the `[…]` desugar target) to mis-report.
            if (first.ReturnType.StaticHint is DataKind staticHint)
            {
                string elementName = staticHint.ToString();
                returnType = first.ReturnType.ProducesArray
                    ? $"Array<{elementName}>"
                    : elementName;
            }
            else
            {
                returnType = first.ReturnType.Describe();
            }
        }

        // Multi-variant functions (e.g. point_cloud_from_depth_pinhole's
        // Image-depth + Float32[]-depth overloads) carry every variant past
        // the primary so the semantic analyzer's argument-type validation
        // can try each shape before warning. Without this it sees only the
        // first variant and flags every call site that matches a later one.
        List<IReadOnlyList<ParameterSignature>>? additional = null;
        if (descriptor.Signatures.Count > 1)
        {
            additional = new List<IReadOnlyList<ParameterSignature>>(descriptor.Signatures.Count - 1);
            for (int i = 1; i < descriptor.Signatures.Count; i++)
            {
                additional.Add(BuildVariantParameters(descriptor.Signatures[i]));
            }
        }

        return new FunctionSignature
        {
            SchemaName = descriptor.SchemaName,
            Name = surfacedName,
            Parameters = parameters,
            ReturnType = returnType,
            Description = descriptor.Description,
            Category = descriptor.Category,
            AdditionalParameterShapes = additional,
            // Per-function context membership. Empty list collapses to null
            // (globally visible) — the consumer convention everywhere else.
            Contexts = descriptor.Contexts is { Count: > 0 } ctxList
                ? new List<string>(ctxList)
                : null,
        };
    }

    /// <summary>
    /// Builds a <see cref="FunctionSignature"/> for one aggregate function.
    /// Reuses the scalar parameter / return-type rendering helpers and stamps
    /// <see cref="FunctionSignature.IsAggregate"/> to <see langword="true"/>.
    /// Mirrors <see cref="BuildSignatureFromDescriptor"/> for scalars; the
    /// minor variations (no <see cref="FunctionDescriptor.Contexts"/>, no
    /// body-scope filter) keep this dedicated to the aggregate code path.
    /// </summary>
    private static FunctionSignature BuildAggregateSignatureFromDescriptor(
        string surfacedName, AggregateFunctionDescriptor descriptor)
    {
        FunctionSignatureVariant? first = descriptor.Signatures.Count > 0
            ? descriptor.Signatures[0]
            : null;

        IReadOnlyList<ParameterSignature> parameters = first is not null
            ? BuildVariantParameters(first)
            : Array.Empty<ParameterSignature>();

        string? returnType = null;
        if (first is not null)
        {
            if (first.ReturnType.StaticHint is DataKind staticHint)
            {
                string elementName = staticHint.ToString();
                returnType = first.ReturnType.ProducesArray
                    ? $"Array<{elementName}>"
                    : elementName;
            }
            else
            {
                returnType = first.ReturnType.Describe();
            }
        }

        List<IReadOnlyList<ParameterSignature>>? additional = null;
        if (descriptor.Signatures.Count > 1)
        {
            additional = new List<IReadOnlyList<ParameterSignature>>(descriptor.Signatures.Count - 1);
            for (int i = 1; i < descriptor.Signatures.Count; i++)
            {
                additional.Add(BuildVariantParameters(descriptor.Signatures[i]));
            }
        }

        return new FunctionSignature
        {
            SchemaName = descriptor.SchemaName,
            Name = surfacedName,
            Parameters = parameters,
            ReturnType = returnType,
            Description = descriptor.Description,
            Category = descriptor.Category,
            AdditionalParameterShapes = additional,
            IsAggregate = true,
        };
    }

    /// <summary>
    /// Renders a single signature variant's parameters as a
    /// <see cref="ParameterSignature"/> list — the same shape the primary
    /// variant produces inline, factored out so additional overloads can
    /// reuse the rendering logic (kind + array marker + variadic suffix).
    /// </summary>
    private static IReadOnlyList<ParameterSignature> BuildVariantParameters(FunctionSignatureVariant variant)
    {
        List<ParameterSignature> result = new(variant.Parameters.Count + (variant.VariadicTrailing is not null ? 1 : 0));
        foreach (ParameterSpec spec in variant.Parameters)
        {
            result.Add(new ParameterSignature
            {
                Name = spec.Name,
                Kind = DescribeParameterShape(spec.Kind, spec.IsArray),
                IsOptional = spec.IsOptional,
                LambdaContextName = (spec.Kind as LambdaMatcher)?.ContextName,
                EnumValues = (spec.Kind as StringEnumMatcher)?.Values,
            });
        }
        if (variant.VariadicTrailing is not null)
        {
            result.Add(new ParameterSignature
            {
                Name = $"...{variant.VariadicTrailing.Name}",
                Kind = DescribeParameterShape(variant.VariadicTrailing.Kind, variant.VariadicTrailing.IsArray),
                IsOptional = variant.VariadicTrailing.MinOccurrences == 0,
                LambdaContextName = (variant.VariadicTrailing.Kind as LambdaMatcher)?.ContextName,
                EnumValues = (variant.VariadicTrailing.Kind as StringEnumMatcher)?.Values,
            });
        }
        return result;
    }

    /// <summary>
    /// Renders a parameter shape as a single string for the manifest's
    /// <see cref="ParameterSignature.Kind"/> field. Wraps the kind matcher's
    /// describe output in <c>Array&lt;...&gt;</c> when the slot requires an
    /// array. Without this, an <c>Array&lt;Float32&gt;</c> parameter slot
    /// surfaced as the bare element kind (<c>Float32</c>), so the semantic
    /// analyzer's type-compatibility check rejected legitimate
    /// <c>Array&lt;Float32&gt;</c> arguments (e.g. <c>models.X(img)</c>
    /// returning <c>Array&lt;Float32&gt;</c> flowing into <c>pose_from_rgbd</c>'s
    /// array-typed slot).
    /// </summary>
    private static string DescribeParameterShape(DataKindMatcher kind, ArrayMatch arrayMatch) => arrayMatch switch
    {
        ArrayMatch.Array
            or ArrayMatch.FlatArray
            or ArrayMatch.MultiDimArray => $"Array<{kind.Describe()}>",
        _ => kind.Describe(),
    };

    /// <summary>
    /// Renders a model's output kind label, surfacing struct field shapes
    /// when the catalog entry's <see cref="Models.ModelCatalogEntry.OutputStructFields"/>
    /// is populated. Falls back to the scalar <c>Array&lt;Kind&gt;</c> /
    /// <c>Kind</c> rendering for non-struct outputs and for opaque
    /// <c>RETURNS Struct</c> models.
    /// </summary>
    /// <summary>
    /// Computes the install state of a catalog entry against the host's
    /// model directory. Mirrors the same three-state classification that
    /// <c>ModelsTableProvider</c> renders into <c>system.models.status</c>,
    /// so completion + introspection agree on what's installed.
    /// </summary>
    private static ModelInstallStatus ResolveInstallStatus(
        Models.ModelCatalogEntry entry, Heliosoph.DatumV.ModelLibrary.IModelPathResolver pathResolver)
    {
        // Synthetic backends declare no RelativePath (EchoModel and friends);
        // they're always loadable.
        if (entry.RelativePath is null)
        {
            return string.Equals(entry.Backend, "python", System.StringComparison.OrdinalIgnoreCase)
                ? ModelInstallStatus.Bridge
                : ModelInstallStatus.Available;
        }
        // RelativePath is id-prefixed; route through the resolver so the
        // status check sees the active-version folder rather than the
        // version-less <root>/<id>/ that doesn't hold weights any more.
        string resolved = pathResolver.ResolveIdPrefixedPath(entry.RelativePath);
        if (!System.IO.File.Exists(resolved))
        {
            return ModelInstallStatus.Missing;
        }
        return string.Equals(entry.Backend, "python", System.StringComparison.OrdinalIgnoreCase)
            ? ModelInstallStatus.Bridge
            : ModelInstallStatus.Available;
    }

    /// <summary>
    /// Snapshots the engine's <see cref="NamedTypeRegistry"/> as a list of
    /// manifest <see cref="NamedTypeEntry"/> records — name + canonical
    /// <c>Struct&lt;…&gt;</c> description. Ships through the manifest so
    /// the language server can resolve named-type references encountered
    /// in function return types and column kinds without taking an
    /// engine-assembly reference.
    /// </summary>
    private static IReadOnlyList<NamedTypeEntry> BuildNamedTypeEntries()
    {
        NamedTypeEntry[] result = new NamedTypeEntry[NamedTypeRegistry.Entries.Count];
        for (int i = 0; i < NamedTypeRegistry.Entries.Count; i++)
        {
            NamedTypeRegistry.NamedTypeDefinition def = NamedTypeRegistry.Entries[i];
            result[i] = new NamedTypeEntry { Name = def.Name, Description = def.Description };
        }
        return result;
    }

    private static string BuildModelOutputKindLabel(Models.ModelCatalogEntry entry)
    {
        // User-written annotation wins when present — preserves named-type
        // identity (`Array<LabeledDetection>`) that the OutputKind + IsArray
        // pair below would flatten to `Array<Struct>`. Built-in models leave
        // OutputKindLabel null and fall through to the structured path.
        if (!string.IsNullOrEmpty(entry.OutputKindLabel))
        {
            return entry.OutputKindLabel;
        }

        if (entry.OutputStructFields is { Count: > 0 } structFields)
        {
            System.Text.StringBuilder sb = new();
            sb.Append("Struct<");
            for (int i = 0; i < structFields.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                Models.ModelStructFieldInfo f = structFields[i];
                // Prefer the raw KindLabel (preserves dim suffixes the
                // user declared); fall back to the re-rendered form only
                // when the catalog entry was built from a path that
                // didn't capture the original annotation.
                string fieldKind = !string.IsNullOrEmpty(f.KindLabel)
                    ? f.KindLabel
                    : (f.IsArray ? $"Array<{f.Kind}>" : f.Kind.ToString());
                sb.Append(f.Name).Append(": ").Append(fieldKind);
            }
            sb.Append('>');
            return sb.ToString();
        }

        return entry.OutputIsArray
            ? $"Array<{entry.OutputKind}>"
            : entry.OutputKind.ToString();
    }

    /// <summary>
    /// Translates a model's catalog-side <see cref="Models.ModelStructFieldInfo"/>
    /// list into the manifest's <see cref="StructFieldSignature"/> shape.
    /// Returns <see langword="null"/> for non-struct outputs so the LS can
    /// short-circuit field resolution. Same shape lives on both
    /// <see cref="ModelEntry.OutputStructFields"/> and (for the model's
    /// scalar-function descriptor)
    /// <see cref="FunctionSignature.OutputStructFields"/>.
    /// </summary>
    private static IReadOnlyList<StructFieldSignature>? BuildModelOutputStructFieldSignatures(
        Models.ModelCatalogEntry entry)
    {
        if (entry.OutputStructFields is not { Count: > 0 } structFields) return null;
        StructFieldSignature[] result = new StructFieldSignature[structFields.Count];
        for (int i = 0; i < structFields.Count; i++)
        {
            Models.ModelStructFieldInfo f = structFields[i];
            string fieldKind = !string.IsNullOrEmpty(f.KindLabel)
                ? f.KindLabel
                : (f.IsArray ? $"Array<{f.Kind}>" : f.Kind.ToString());
            result[i] = new StructFieldSignature { Name = f.Name, Kind = fieldKind };
        }
        return result;
    }

    /// <summary>
    /// Replaces each <c>models.X</c> entry in <paramref name="functionSigs"/>
    /// with a copy carrying its model's <see cref="StructFieldSignature"/>
    /// list when available. The hover provider's primary lookup path goes
    /// through Functions (not Models), so without this pass the scalar-
    /// function view of a struct-returning model would surface as opaque
    /// <c>Struct</c> even when <see cref="ModelEntry.OutputStructFields"/>
    /// has the full shape. Matches by (schema=<c>models</c>, name); entries
    /// without a matching model are left untouched.
    /// </summary>
    private static void BackfillModelStructFieldsOntoFunctionSignatures(
        List<FunctionSignature> functionSigs, List<ModelEntry> modelEntries)
    {
        Dictionary<string, ModelEntry> byName = new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelEntry m in modelEntries)
        {
            byName[m.Name] = m;
        }
        if (byName.Count == 0) return;

        for (int i = 0; i < functionSigs.Count; i++)
        {
            FunctionSignature sig = functionSigs[i];
            if (!string.Equals(sig.SchemaName, "models", StringComparison.OrdinalIgnoreCase)) continue;
            if (!byName.TryGetValue(sig.Name, out ModelEntry? model)) continue;

            // Per-parameter struct fields rebound onto the function-signature
            // copy of the model's parameter list so any caller that looks up
            // `models.X(...)` parameters through Functions (e.g. the
            // completion provider's ResolveParameterVariants path) sees the
            // same struct shape the ModelEntry surface does.
            IReadOnlyList<ParameterSignature> parameters = sig.Parameters;
            if (model.Parameters is { Count: > 0 } modelParams
                && AnyParameterHasStructFields(modelParams))
            {
                parameters = RebindParameterStructFields(sig.Parameters, modelParams);
            }

            // Output struct fields + refreshed ReturnType: only swap in when
            // the model actually carries a non-null shape AND the function
            // entry didn't already have one. Independent of the parameter
            // rebind above so a model with struct-typed inputs but a scalar
            // return doesn't get a bogus opaque output replaced.
            IReadOnlyList<StructFieldSignature>? outputFields = sig.OutputStructFields;
            string? returnType = sig.ReturnType;
            if (outputFields is null && model.OutputStructFields is { Count: > 0 } modelOutput)
            {
                outputFields = modelOutput;
                returnType = StructTypeAnnotation.Format(
                    modelOutput.Select(f => new StructFieldShape(f.Name, f.Kind)).ToArray());
            }

            // Always prefer the model's resolved OutputKind label over the
            // descriptor-derived ReturnType when it carries richer info — a
            // SQL-defined model with `RETURNS Array<LabeledDetection>` keeps
            // its named-type identity in the model entry's OutputKind, and
            // the descriptor's StaticHint path would otherwise flatten it
            // to `Array<Struct>` here. Order is intentional: when both the
            // struct-field rebuild AND a named-type label are available,
            // the label wins because it preserves the source-text identity
            // (`LabeledDetection`) that the struct-field render path
            // expands to (`Struct<bbox: …, label: …, score: …>`).
            if (!string.IsNullOrEmpty(model.OutputKind)
                && !string.Equals(model.OutputKind, returnType, StringComparison.Ordinal))
            {
                returnType = model.OutputKind;
            }

            // Skip the rewrite when nothing changed — keeps the original
            // FunctionSignature object identity for the vast majority of
            // non-struct models.
            if (ReferenceEquals(parameters, sig.Parameters)
                && ReferenceEquals(outputFields, sig.OutputStructFields)
                && string.Equals(returnType, sig.ReturnType, StringComparison.Ordinal))
            {
                continue;
            }

            functionSigs[i] = new FunctionSignature
            {
                SchemaName = sig.SchemaName,
                Name = sig.Name,
                Parameters = parameters,
                ReturnType = returnType,
                Description = sig.Description,
                Category = sig.Category,
                IsTableValued = sig.IsTableValued,
                OutputColumns = sig.OutputColumns,
                AdditionalParameterShapes = sig.AdditionalParameterShapes,
                OutputStructFields = outputFields,
                IsAggregate = sig.IsAggregate,
                IsWindowFunction = sig.IsWindowFunction,
            };
        }
    }

    private static bool AnyParameterHasStructFields(IReadOnlyList<ParameterSignature> parameters)
    {
        for (int i = 0; i < parameters.Count; i++)
        {
            if (parameters[i].StructFields is { Count: > 0 }) return true;
        }
        return false;
    }

    /// <summary>
    /// Returns a copy of the function-signature parameter list where each
    /// slot's <see cref="ParameterSignature.StructFields"/> is back-filled
    /// from the matching positional slot of the model's parameter list
    /// (matched by name when possible, otherwise by index). The function-
    /// descriptor path (<see cref="BuildSignatureFromDescriptor"/>) loses
    /// the named-type identity because <see cref="ParameterSpec"/> only
    /// carries a <see cref="DataKindMatcher"/>, so this re-attaches the
    /// shape from the parallel <see cref="ModelEntry.Parameters"/> list,
    /// which preserved it via <see cref="Models.ModelParameterInfo.StructFields"/>.
    /// </summary>
    private static ParameterSignature[] RebindParameterStructFields(
        IReadOnlyList<ParameterSignature> functionParameters,
        IReadOnlyList<ParameterSignature> modelParameters)
    {
        ParameterSignature[] result = new ParameterSignature[functionParameters.Count];
        for (int i = 0; i < functionParameters.Count; i++)
        {
            ParameterSignature p = functionParameters[i];
            ParameterSignature? match = FindMatchingModelParameter(modelParameters, p.Name, i);
            if (match?.StructFields is not { Count: > 0 } fields)
            {
                result[i] = p;
                continue;
            }
            result[i] = new ParameterSignature
            {
                Name = p.Name,
                Kind = p.Kind,
                IsOptional = p.IsOptional,
                LambdaContextName = p.LambdaContextName,
                EnumValues = p.EnumValues,
                StructFields = fields,
            };
        }
        return result;
    }

    private static ParameterSignature? FindMatchingModelParameter(
        IReadOnlyList<ParameterSignature> modelParameters, string name, int index)
    {
        for (int i = 0; i < modelParameters.Count; i++)
        {
            if (string.Equals(modelParameters[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return modelParameters[i];
            }
        }
        // Name lookup failed (alias or generic `input` label) — fall back to
        // positional match when the lists have the same arity.
        if (index >= 0 && index < modelParameters.Count)
        {
            return modelParameters[index];
        }
        return null;
    }

    /// <summary>
    /// Translates a model's <see cref="Models.ModelStructFieldInfo"/> list —
    /// captured at registration from the parameter's declared type
    /// annotation — into manifest-side <see cref="StructFieldSignature"/>
    /// entries. <see langword="null"/> when no fields were resolved (opaque
    /// struct), so consumers can short-circuit the field-completion path.
    /// </summary>
    private static IReadOnlyList<StructFieldSignature>? BuildParameterStructFieldSignatures(
        IReadOnlyList<Models.ModelStructFieldInfo>? structFields)
    {
        if (structFields is not { Count: > 0 } fields) return null;
        StructFieldSignature[] result = new StructFieldSignature[fields.Count];
        for (int i = 0; i < fields.Count; i++)
        {
            Models.ModelStructFieldInfo f = fields[i];
            string fieldKind = !string.IsNullOrEmpty(f.KindLabel)
                ? f.KindLabel
                : (f.IsArray ? $"Array<{f.Kind}>" : f.Kind.ToString());
            result[i] = new StructFieldSignature
            {
                Name = f.Name,
                Kind = fieldKind,
                EnumValues = f.EnumValues,
            };
        }
        return result;
    }

    /// <summary>
    /// Builds a <see cref="FunctionSignature"/> for one table-valued function.
    /// Reads the first signature variant (most TVFs have one shape) and
    /// renders the <see cref="TableValuedFunctionSignatureVariant.FixedOutputSchema"/>
    /// as the return type when present so hover / signature help can show
    /// "→ table(col1 Kind1, col2 Kind2)". Schema-dependent TVFs (range — the
    /// column kind follows the widest argument) leave the schema null and
    /// fall back to "→ table".
    /// </summary>
    private static FunctionSignature BuildTvfSignatureFromDescriptor(
        TableValuedFunctionDescriptor descriptor)
    {
        TableValuedFunctionSignatureVariant? first = descriptor.Signatures.Count > 0
            ? descriptor.Signatures[0]
            : null;

        List<ParameterSignature> parameters = new();
        string? returnType = null;
        List<TableColumnEntry>? outputColumns = null;

        if (first is not null)
        {
            foreach (ParameterSpec spec in first.Parameters)
            {
                parameters.Add(new ParameterSignature
                {
                    Name = spec.Name,
                    Kind = spec.Kind.Describe(),
                    IsOptional = spec.IsOptional,
                });
            }
            if (first.VariadicTrailing is not null)
            {
                parameters.Add(new ParameterSignature
                {
                    Name = $"...{first.VariadicTrailing.Name}",
                    Kind = first.VariadicTrailing.Kind.Describe(),
                    IsOptional = first.VariadicTrailing.MinOccurrences == 0,
                });
            }

            if (first.FixedOutputSchema is not null)
            {
                Schema schema = first.FixedOutputSchema;
                List<string> columns = new(schema.Columns.Count);
                outputColumns = new List<TableColumnEntry>(schema.Columns.Count);
                foreach (ColumnInfo column in schema.Columns)
                {
                    string kindLabel = column.IsArray ? $"Array<{column.Kind}>" : column.Kind.ToString();
                    columns.Add($"{column.Name} {kindLabel}");
                    outputColumns.Add(new TableColumnEntry
                    {
                        Name = column.Name,
                        Kind = kindLabel,
                        Nullable = column.Nullable,
                    });
                }
                returnType = $"table({string.Join(", ", columns)})";
            }
            else
            {
                returnType = "table";
            }
        }

        return new FunctionSignature
        {
            SchemaName = descriptor.SchemaName,
            Name = descriptor.PrimaryName,
            Parameters = parameters,
            ReturnType = returnType,
            Description = descriptor.Description,
            Category = descriptor.Category,
            IsTableValued = true,
            OutputColumns = outputColumns,
        };
    }

    /// <summary>
    /// Synthesises a positional parameter list for a model from its
    /// <c>InputKinds</c> (required) and <c>OptionalArgKinds</c> (trailing
    /// hyperparameters). Catalog entries don't carry parameter names, so
    /// we use generic positional labels — the kind information is the
    /// useful part.
    /// </summary>
    private static IReadOnlyList<ParameterSignature> BuildModelParameters(
        Models.ModelCatalogEntry entry)
    {
        // SQL-defined models attach a rich per-parameter snapshot at
        // registration (name, kind, array-ness). Prefer that when present
        // so hover / signature help can show `img: Image` instead of the
        // generic positional `input: <kind>` we synthesize from
        // <see cref="Models.ModelCatalogEntry.InputKinds"/> for built-ins
        // that lack the snapshot.
        if (entry.ParameterInfos is { Count: > 0 } infos)
        {
            ParameterSignature[] result = new ParameterSignature[infos.Count];
            for (int i = 0; i < infos.Count; i++)
            {
                Models.ModelParameterInfo info = infos[i];
                string kindLabel = info.IsArray
                    ? $"Array<{info.Kind}>"
                    : info.Kind.ToString();
                result[i] = new ParameterSignature
                {
                    Name = info.Name,
                    Kind = kindLabel,
                    IsOptional = info.IsOptional,
                    StructFields = BuildParameterStructFieldSignatures(info.StructFields),
                };
            }
            return result;
        }

        int requiredCount = entry.InputKinds.Count;
        int optionalCount = entry.OptionalArgKinds?.Count ?? 0;
        if (requiredCount == 0 && optionalCount == 0)
        {
            return Array.Empty<ParameterSignature>();
        }

        List<ParameterSignature> parameters = new(requiredCount + optionalCount);
        for (int i = 0; i < requiredCount; i++)
        {
            parameters.Add(new ParameterSignature
            {
                Name = requiredCount == 1 ? "input" : $"input{i + 1}",
                Kind = entry.InputKinds[i].ToString(),
            });
        }
        if (entry.OptionalArgKinds is not null)
        {
            for (int i = 0; i < entry.OptionalArgKinds.Count; i++)
            {
                parameters.Add(new ParameterSignature
                {
                    Name = optionalCount == 1 ? "option" : $"option{i + 1}",
                    Kind = entry.OptionalArgKinds[i].ToString(),
                    IsOptional = true,
                });
            }
        }
        return parameters;
    }
}
