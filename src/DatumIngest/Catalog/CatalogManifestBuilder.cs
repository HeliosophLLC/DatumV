using DatumIngest.Catalog.Registries;
using DatumIngest.Execution.Contexts;
using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Catalog;

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
    /// </summary>
    public static LanguageServerManifest Build(TableCatalog catalog, FunctionRegistry functions)
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
        // `SELECT * FROM datum_catalog.functions WHERE body_scope = 'modelbody'`,
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

        // Aggregate / window functions don't yet carry static signature
        // metadata — register-by-instance only — so their argument lists
        // stay empty until the registries gain descriptors. SchemaName
        // flows in from the registry's QualifiedName keys so completion can
        // filter templates.X / etc. correctly.
        foreach (QualifiedName qn in functions.AggregateFunctionQualifiedNames)
        {
            functionSigs.Add(new FunctionSignature { SchemaName = qn.Schema, Name = qn.Name, Parameters = [], IsAggregate = true });
        }
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
            string modelDirectory = catalog.Models.ModelDirectory;
            List<ModelEntry> modelEntries = new(catalog.Models.Entries.Count);
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
                ModelInstallStatus status = ResolveInstallStatus(entry.Value, modelDirectory);
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
                });
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

        return new LanguageServerManifest
        {
            Tables = tables,
            Functions = functionSigs,
            Keywords = keywords,
            Models = models,
            Udfs = udfEntries,
            Procedures = procedureEntries,
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
        Models.ModelCatalogEntry entry, string modelDirectory)
    {
        // Synthetic backends declare no RelativePath (EchoModel and friends);
        // they're always loadable.
        if (entry.RelativePath is null)
        {
            return string.Equals(entry.Backend, "python", System.StringComparison.OrdinalIgnoreCase)
                ? ModelInstallStatus.Bridge
                : ModelInstallStatus.Available;
        }
        string resolved = System.IO.Path.Combine(modelDirectory, entry.RelativePath);
        if (!System.IO.File.Exists(resolved))
        {
            return ModelInstallStatus.Missing;
        }
        return string.Equals(entry.Backend, "python", System.StringComparison.OrdinalIgnoreCase)
            ? ModelInstallStatus.Bridge
            : ModelInstallStatus.Available;
    }

    private static string BuildModelOutputKindLabel(Models.ModelCatalogEntry entry)
    {
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
        Dictionary<string, IReadOnlyList<StructFieldSignature>> byName =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ModelEntry m in modelEntries)
        {
            if (m.OutputStructFields is not null) byName[m.Name] = m.OutputStructFields;
        }
        if (byName.Count == 0) return;

        for (int i = 0; i < functionSigs.Count; i++)
        {
            FunctionSignature sig = functionSigs[i];
            if (!string.Equals(sig.SchemaName, "models", StringComparison.OrdinalIgnoreCase)) continue;
            if (sig.OutputStructFields is not null) continue;
            if (!byName.TryGetValue(sig.Name, out IReadOnlyList<StructFieldSignature>? fields)) continue;

            // FunctionSignature is init-only, so emit a replacement carrying
            // every existing field plus OutputStructFields. ReturnType also
            // gets refreshed so the hover popup shows the full struct shape
            // instead of bare `Struct`.
            string refreshedReturnType = StructTypeAnnotation.Format(
                fields.Select(f => new StructFieldShape(f.Name, f.Kind)).ToArray());
            functionSigs[i] = new FunctionSignature
            {
                SchemaName = sig.SchemaName,
                Name = sig.Name,
                Parameters = sig.Parameters,
                ReturnType = refreshedReturnType,
                Description = sig.Description,
                Category = sig.Category,
                IsTableValued = sig.IsTableValued,
                OutputColumns = sig.OutputColumns,
                AdditionalParameterShapes = sig.AdditionalParameterShapes,
                OutputStructFields = fields,
                IsAggregate = sig.IsAggregate,
                IsWindowFunction = sig.IsWindowFunction,
            };
        }
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
