using DatumIngest.Catalog.Registries;
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
            List<ModelEntry> modelEntries = new(catalog.Models.Entries.Count);
            foreach (KeyValuePair<string, Models.ModelCatalogEntry> entry in catalog.Models.Entries)
            {
                modelEntries.Add(new ModelEntry
                {
                    Name = entry.Value.Name,
                    OutputKind = entry.Value.OutputKind.ToString(),
                    Category = entry.Value.Category,
                    Backend = entry.Value.Backend,
                    DisplayName = entry.Value.DisplayName,
                    Parameters = BuildModelParameters(entry.Value),
                });
            }
            models = modelEntries;
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
        };
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
                    Kind = spec.Kind.Describe(),
                    IsOptional = spec.IsOptional,
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
                    Kind = first.VariadicTrailing.Kind.Describe(),
                    IsOptional = first.VariadicTrailing.MinOccurrences == 0,
                });
            }

            // Static return kind when the rule reports one; otherwise fall
            // back to the rule's textual description ("same as argument 0",
            // a custom blurb, …) so users still see *something*.
            string baseReturn = first.ReturnType.StaticHint?.ToString()
                ?? first.ReturnType.Describe();
            returnType = first.ReturnType.ProducesArray
                ? $"Array<{baseReturn}>"
                : baseReturn;
        }

        return new FunctionSignature
        {
            SchemaName = descriptor.SchemaName,
            Name = surfacedName,
            Parameters = parameters,
            ReturnType = returnType,
            Description = descriptor.Description,
            Category = descriptor.Category,
        };
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
