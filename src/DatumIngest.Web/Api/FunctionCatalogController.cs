using DatumIngest.Functions;
using DatumIngest.Manifest;
using DatumIngest.Model;
using DatumIngest.Web.Dtos.Functions;
using Microsoft.AspNetCore.Mvc;

namespace DatumIngest.Web.Api;

/// <summary>
/// Read-only catalog endpoints for the function registry. Lets the
/// Execute-Function tab enumerate scalar functions with full signature
/// metadata — argument names, accepted kinds, optionality, variadics, and
/// the return-type rule — so the client can render a form per overload.
/// </summary>
/// <remarks>
/// <para>
/// Sourced from <see cref="FunctionRegistry.ScalarDescriptors"/>. Functions
/// that only make sense inside a procedural context (e.g. <c>infer()</c>
/// inside a <c>CREATE MODEL</c> body) still appear in the listing with
/// their <c>BodyScope</c> set, so the client can decide whether to
/// disable them in the picker.
/// </para>
/// </remarks>
[ApiController]
[Route("api/functions")]
public sealed class FunctionCatalogController(FunctionRegistry registry) : ControllerBase
{
    /// <summary>
    /// Lists every registered scalar function with its full signature
    /// metadata. Aliases ride along on the primary entry — no separate
    /// row per alias — so consumers can decide how to surface them.
    /// </summary>
    [HttpGet("scalar")]
    public ActionResult<ScalarFunctionListResponse> ListScalar()
    {
        IReadOnlyList<FunctionDescriptor> descriptors = registry.ScalarDescriptors;
        List<ScalarFunctionDto> functions = new(descriptors.Count);
        foreach (FunctionDescriptor d in descriptors)
        {
            functions.Add(ToDto(d));
        }
        return new ScalarFunctionListResponse(functions);
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
