using DatumIngest.Catalog;
using DatumIngest.Catalog.Registries;
using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;
using DatumIngest.Web.Dtos.Functions;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

/// <summary>
/// Read-only catalog endpoints for the function / UDF / model surface.
/// Lets the Execute-Function tab enumerate built-in scalars, user-defined
/// functions, and SQL-defined models with full signature metadata —
/// argument names, accepted kinds, optionality, variadics, return-type
/// rules, parameter <see cref="ParameterCheckDto">constraints</see>,
/// defaults, and units — so the client can render a form per overload
/// without re-implementing the SQL grammar.
/// </summary>
/// <remarks>
/// <para>
/// Three endpoints, one shape per registry:
/// <list type="bullet">
///   <item><description><c>GET scalar</c> — every registered scalar function,
///   sourced from <see cref="FunctionRegistry.ScalarDescriptors"/>. Built-ins,
///   procedural-UDF adapters, and SQL-defined model adapters all appear here
///   (they're all <c>IScalarFunction</c>s under the hood). Functions that only
///   make sense inside a procedural context (e.g. <c>infer()</c> inside a
///   <c>CREATE MODEL</c> body) still appear with their <c>BodyScope</c> set
///   so the client can decide whether to disable them in the picker.</description></item>
///   <item><description><c>GET udfs</c> — only SQL UDFs (macro + procedural),
///   sourced from <see cref="TableCatalog.Udfs"/>. Carries body-shape, purity
///   flag, return-type annotation, and verbatim source text — fields specific
///   to user-authored routines.</description></item>
///   <item><description><c>GET models</c> — only SQL-defined models, sourced
///   from <see cref="TableCatalog.DeclaredModels"/>. Carries the <c>USING</c>
///   binding path and any <c>IMPLEMENTS TaskName</c> contract.</description></item>
/// </list>
/// </para>
/// </remarks>
[ApiController]
[Route("api/functions")]
public sealed class FunctionCatalogController(TableCatalog catalog) : ControllerBase
{
    /// <summary>
    /// Lists every registered scalar function with its full signature
    /// metadata. Aliases ride along on the primary entry — no separate
    /// row per alias — so consumers can decide how to surface them.
    /// </summary>
    [HttpGet("scalar")]
    public ActionResult<ScalarFunctionListResponse> ListScalar()
    {
        IReadOnlyList<FunctionDescriptor> descriptors = catalog.Functions.ScalarDescriptors;
        List<ScalarFunctionDto> functions = new(descriptors.Count);
        foreach (FunctionDescriptor d in descriptors)
        {
            functions.Add(ToDto(d));
        }
        return new ScalarFunctionListResponse(functions);
    }

    /// <summary>
    /// Lists every SQL UDF registered against the catalog. Mirrors
    /// <see cref="ListScalar"/>'s shape but adds body-kind, purity, and
    /// source-text fields specific to user-authored routines, and projects
    /// parameter defaults verbatim through <see cref="QueryExplainer.FormatExpression"/>.
    /// </summary>
    [HttpGet("udfs")]
    public ActionResult<UdfListResponse> ListUdfs()
    {
        IReadOnlyCollection<UdfDescriptor> entries = catalog.Udfs.Entries;
        List<UdfDto> udfs = new(entries.Count);
        foreach (UdfDescriptor d in entries)
        {
            udfs.Add(ToDto(d));
        }
        return new UdfListResponse(udfs);
    }

    /// <summary>
    /// Lists every SQL procedure registered against the catalog via
    /// <c>CREATE PROCEDURE</c>. Same parameter projection as
    /// <see cref="ListUdfs"/>; procedures lack a return type because they
    /// run for effect under <c>CALL</c>.
    /// </summary>
    [HttpGet("procedures")]
    public ActionResult<ProcedureListResponse> ListProcedures()
    {
        IReadOnlyCollection<ProcedureDescriptor> entries = catalog.Procedures.Entries;
        List<ProcedureDto> procedures = new(entries.Count);
        foreach (ProcedureDescriptor d in entries)
        {
            ScalarFunctionParameterDto[] parameters = new ScalarFunctionParameterDto[d.Parameters.Count];
            for (int i = 0; i < d.Parameters.Count; i++)
            {
                parameters[i] = ToParameterDto(d.Parameters[i]);
            }
            procedures.Add(new ProcedureDto(
                Schema: d.SchemaName,
                Name: d.Name,
                Parameters: parameters,
                SourceText: d.SourceText));
        }
        return new ProcedureListResponse(procedures);
    }

    /// <summary>
    /// Lists every SQL-defined model registered against the catalog via
    /// <c>CREATE MODEL</c>. Surfaces the <c>USING</c> path (raw + resolved),
    /// any <c>IMPLEMENTS</c> task-contract declaration, and full parameter
    /// metadata.
    /// </summary>
    [HttpGet("models")]
    public ActionResult<ModelListResponse> ListModels()
    {
        IReadOnlyCollection<ModelDescriptor> entries = catalog.DeclaredModels.Entries;
        List<ModelDto> models = new(entries.Count);
        foreach (ModelDescriptor d in entries)
        {
            models.Add(ToDto(d));
        }
        return new ModelListResponse(models);
    }

    private static UdfDto ToDto(UdfDescriptor d)
    {
        ScalarFunctionParameterDto[] parameters = new ScalarFunctionParameterDto[d.Parameters.Count];
        for (int i = 0; i < d.Parameters.Count; i++)
        {
            parameters[i] = ToParameterDto(d.Parameters[i]);
        }
        return new UdfDto(
            Schema: d.SchemaName,
            Name: d.Name,
            BodyKind: d.IsProcedural ? "procedural" : "macro",
            IsPure: d.IsPure,
            Parameters: parameters,
            ReturnType: d.ReturnTypeName,
            ReturnIsNotNull: d.ReturnIsNotNull,
            SourceText: d.SourceText);
    }

    private static ModelDto ToDto(ModelDescriptor d)
    {
        ScalarFunctionParameterDto[] parameters = new ScalarFunctionParameterDto[d.Parameters.Count];
        for (int i = 0; i < d.Parameters.Count; i++)
        {
            parameters[i] = ToParameterDto(d.Parameters[i]);
        }
        return new ModelDto(
            Schema: d.SchemaName,
            Name: d.Name,
            Parameters: parameters,
            ReturnType: d.ReturnTypeName,
            ReturnIsNotNull: d.ReturnIsNotNull,
            UsingPath: d.UsingPath,
            ResolvedUsingPath: d.ResolvedUsingPath,
            ImplementsTask: d.ImplementsTaskName,
            SourceText: d.SourceText);
    }

    /// <summary>
    /// Projects a parsed <see cref="UdfParameter"/> into the same
    /// <see cref="ScalarFunctionParameterDto"/> the built-in scalar projection uses,
    /// so client-side rendering code can switch on a single parameter shape
    /// regardless of whether the function is a built-in, UDF, or model.
    /// The <c>CHECK</c> AST is canonicalised through
    /// <see cref="ParameterCheckWalker.Canonicalise"/> so the wire payload
    /// carries the same typed discriminator (<c>"between"</c>, <c>"in"</c>, …)
    /// as built-in scalars.
    /// </summary>
    private static ScalarFunctionParameterDto ToParameterDto(UdfParameter p)
    {
        // Declared-type → DataKind: a parsed annotation pins the slot to one kind.
        // If the annotation doesn't resolve (custom type, parser quirk), fall
        // back to "accepts any" so the client renders a permissive widget
        // rather than failing closed.
        bool typeResolved = TypeAnnotationResolver.TryParse(p.TypeName, out DataKind kind, out bool isArray);
        IReadOnlyList<string> acceptedKinds = typeResolved
            ? [kind.ToString()]
            : Array.Empty<string>();
        bool acceptsAny = !typeResolved;
        string arrayMatch = isArray ? ArrayMatch.Array.ToString() : ArrayMatch.Either.ToString();

        ParameterCheck? check = p.Check is null
            ? null
            : ParameterCheckWalker.Canonicalise(p.Check, p.Name);

        return new ScalarFunctionParameterDto(
            Name: p.Name,
            KindLabel: p.TypeName,
            AcceptedKinds: acceptedKinds,
            AcceptsAnyKind: acceptsAny,
            IsOptional: p.Default is not null,
            ArrayMatch: arrayMatch,
            DefaultExpression: p.Default is null
                ? null
                : QueryExplainer.FormatExpression(p.Default),
            Check: ToCheckDto(check),
            Step: p.Step,
            Unit: p.Unit,
            Description: p.Description);
    }

    private static ScalarFunctionDto ToDto(FunctionDescriptor d)
    {
        ScalarFunctionSignatureDto[] signatures = new ScalarFunctionSignatureDto[d.Signatures.Count];
        for (int i = 0; i < d.Signatures.Count; i++)
        {
            signatures[i] = ToDto(d.Signatures[i]);
        }
        return new ScalarFunctionDto(
            Schema: d.SchemaName,
            Name: d.PrimaryName,
            Aliases: d.Aliases,
            Category: d.Category.ToString(),
            Description: d.Description,
            BodyScope: d.BodyScope.ToString(),
            Signatures: signatures);
    }

    private static ScalarFunctionSignatureDto ToDto(FunctionSignatureVariant variant)
    {
        ScalarFunctionParameterDto[] parameters = new ScalarFunctionParameterDto[variant.Parameters.Count];
        for (int i = 0; i < variant.Parameters.Count; i++)
        {
            parameters[i] = ToDto(variant.Parameters[i]);
        }
        ScalarFunctionVariadicDto? variadic = variant.VariadicTrailing is null
            ? null
            : ToDto(variant.VariadicTrailing);
        return new ScalarFunctionSignatureDto(parameters, variadic, ToDto(variant.ReturnType));
    }

    private static ScalarFunctionParameterDto ToDto(ParameterSpec spec)
    {
        (IReadOnlyList<string> kinds, bool acceptsAny) = EnumerateAcceptedKinds(spec.Kind);
        // Metadata is null for parameters without declared hints; the DTO
        // forwards null straight through so clients see "no constraint"
        // rather than empty-string sentinels.
        ParameterMetadata? meta = spec.Metadata;
        return new ScalarFunctionParameterDto(
            Name: spec.Name,
            KindLabel: spec.Kind.Describe(),
            AcceptedKinds: kinds,
            AcceptsAnyKind: acceptsAny,
            IsOptional: spec.IsOptional,
            ArrayMatch: spec.IsArray.ToString(),
            // Built-in scalars don't carry defaults today (defaults live on
            // procedural UDFs / models); reserved field for future SQL-model
            // surface integration.
            DefaultExpression: null,
            Check: ToCheckDto(meta?.Check),
            Step: meta?.Step,
            Unit: meta?.Unit,
            Description: meta?.Description);
    }

    /// <summary>
    /// Engine → DTO mapping for the discriminated <see cref="ParameterCheck"/>
    /// hierarchy. The C# pattern-match runs exactly once per parameter at
    /// request time; the DTO records carry the same field shapes so
    /// projection is a direct construction call per branch. <c>CustomCheck</c>
    /// crosses the engine/wire boundary as a pretty-printed SQL string
    /// (the <c>Expression</c> AST itself doesn't serialise).
    /// </summary>
    private static ParameterCheckDto? ToCheckDto(ParameterCheck? check) => check switch
    {
        null => null,
        BetweenCheck b => new BetweenCheckDto(b.Min, b.Max),
        RangeCheck r => new RangeCheckDto(r.Min, r.Max, r.MinInclusive, r.MaxInclusive),
        GreaterThanCheck g => new GreaterThanCheckDto(g.Min, g.Inclusive),
        LessThanCheck l => new LessThanCheckDto(l.Max, l.Inclusive),
        InCheck i => new InCheckDto(i.Values),
        RegexCheck rx => new RegexCheckDto(rx.Pattern),
        CustomCheck c => new CustomCheckDto(c.Expr.ToString() ?? string.Empty),
        _ => throw new InvalidOperationException(
            $"Unmapped ParameterCheck subclass '{check.GetType().Name}'. "
            + "Add a switch arm in FunctionCatalogController.ToCheckDto."),
    };

    private static ScalarFunctionVariadicDto ToDto(VariadicSpec spec)
    {
        (IReadOnlyList<string> kinds, bool acceptsAny) = EnumerateAcceptedKinds(spec.Kind);
        return new ScalarFunctionVariadicDto(
            Name: spec.Name,
            KindLabel: spec.Kind.Describe(),
            AcceptedKinds: kinds,
            AcceptsAnyKind: acceptsAny,
            MinOccurrences: spec.MinOccurrences,
            RequireSameKindAcrossArgs: spec.RequireSameKindAcrossArgs,
            ArrayMatch: spec.IsArray.ToString());
    }

    private static ScalarFunctionReturnTypeDto ToDto(ReturnTypeRule rule)
    {
        return new ScalarFunctionReturnTypeDto(
            Description: rule.Describe(),
            StaticHint: rule.StaticHint?.ToString(),
            ProducesArray: rule.ProducesArray);
    }

    /// <summary>
    /// Probes the matcher against every <see cref="DataKind"/> value to
    /// produce a concrete accepted-kinds list. Cheaper than adding a new
    /// API surface on <see cref="DataKindMatcher"/> and good enough for a
    /// catalog endpoint that runs once per UI session. Returns
    /// <c>(empty, true)</c> when the matcher accepts every kind — the
    /// client treats that as "no filter" and skips serialising a 30-entry
    /// list that says nothing.
    /// </summary>
    private static (IReadOnlyList<string> Kinds, bool AcceptsAny) EnumerateAcceptedKinds(DataKindMatcher matcher)
    {
        DataKind[] all = (DataKind[])Enum.GetValues(typeof(DataKind));
        List<string> accepted = new(all.Length);
        int acceptedCount = 0;
        int candidateCount = 0;
        foreach (DataKind k in all)
        {
            // Skip Unknown — it's a sentinel, never a legitimate column kind.
            if (k == DataKind.Unknown) continue;
            candidateCount++;
            if (matcher.Matches(k))
            {
                accepted.Add(k.ToString());
                acceptedCount++;
            }
        }
        if (acceptedCount == candidateCount)
        {
            return (Array.Empty<string>(), true);
        }
        return (accepted, false);
    }
}
