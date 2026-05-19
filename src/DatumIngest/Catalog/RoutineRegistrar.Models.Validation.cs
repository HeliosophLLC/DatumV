using Heliosoph.DatumV.Catalog.Registries;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Functions;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;
using Heliosoph.DatumV.Models;
using Heliosoph.DatumV.Parsing.Ast;

namespace Heliosoph.DatumV.Catalog;

internal sealed partial class RoutineRegistrar
{
    /// <summary>
    /// Validates that <paramref name="create"/>'s parameter list and return
    /// type match the named task contract from
    /// <see cref="TaskTypeRegistry"/>. Field-by-field comparison is
    /// type-only (parameter names are documentation, not contract); named
    /// types match by name through the type-annotation resolver.
    /// </summary>
    /// <remarks>
    /// Throws <see cref="InvalidOperationException"/> with a clear message
    /// printing both the contract's expected signature and the model's
    /// declared signature when a mismatch fires. Errors surface at
    /// <c>CREATE MODEL</c> time so users find out before any inference
    /// dispatcher loads weights.
    /// </remarks>
    private static void ValidateImplementsContract(
        CreateModelStatement create, string taskName)
    {
        TaskTypeRegistry.TaskContract? contract = TaskTypeRegistry.TryGet(taskName);
        if (contract is null)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS '{taskName}' references an unknown task contract. "
                + "See `SELECT name FROM system.task_contracts` for the registered vocabulary.");
        }

        // Parameter arity check: the model's required (non-default) parameters
        // must match the contract's input list exactly. Models may declare
        // *additional* optional parameters (defaults at the tail) beyond the
        // contract — those are extra runtime knobs the model exposes, like
        // YOLOX's confidence/IoU thresholds. The contract still defines the
        // minimum invocation shape; optional params are additive.
        int requiredCount = 0;
        foreach (UdfParameter p in create.Parameters)
        {
            if (p.Default is null) requiredCount++;
            else break; // Defaults are contiguous at the tail (enforced elsewhere).
        }
        if (requiredCount != contract.InputKinds.Count)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} requires "
                + $"{contract.InputKinds.Count} required parameter(s) but the model declares {requiredCount}. "
                + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
        }

        // Per-parameter type match against the contract — applies only to
        // the required (leading) parameters. Names are documentation; only
        // kinds matter. Optional trailing parameters are model-specific
        // knobs and aren't validated against the contract.
        for (int i = 0; i < contract.InputKinds.Count; i++)
        {
            UdfParameter param = create.Parameters[i];
            if (param.TypeName is null
                || !TypeAnnotationResolver.TryParse(param.TypeName, out DataKind kind, out bool isArray))
            {
                throw new QueryPlanException(
                    $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} parameter "
                    + $"#{i + 1} ('{param.Name}') has unresolved type '{param.TypeName}'.");
            }
            string? namedTypeName = TypeAnnotationResolver.IsNamedType(StripArrayWrapperForName(param.TypeName))
                ? StripArrayWrapperForName(param.TypeName)
                : null;
            if (!contract.InputKinds[i].Matches(kind, isArray, namedTypeName))
            {
                throw new QueryPlanException(
                    $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} parameter "
                    + $"#{i + 1} expected {contract.InputKinds[i]}, got "
                    + $"{(isArray ? $"Array<{namedTypeName ?? kind.ToString()}>" : namedTypeName ?? kind.ToString())}. "
                    + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
            }
        }

        // Return type match.
        if (!TypeAnnotationResolver.TryParse(create.ReturnTypeName, out DataKind returnKind, out bool returnIsArray))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} return type "
                + $"'{create.ReturnTypeName}' is not a recognized type annotation.");
        }
        string? returnNamedTypeName = TypeAnnotationResolver.IsNamedType(StripArrayWrapperForName(create.ReturnTypeName))
            ? StripArrayWrapperForName(create.ReturnTypeName)
            : null;
        if (!contract.ReturnKind.Matches(returnKind, returnIsArray, returnNamedTypeName))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: IMPLEMENTS {contract.Name} return expected "
                + $"{contract.ReturnKind}, got "
                + $"{(returnIsArray ? $"Array<{returnNamedTypeName ?? returnKind.ToString()}>" : returnNamedTypeName ?? returnKind.ToString())}. "
                + $"Expected signature: ({string.Join(", ", contract.InputKinds)}) → {contract.ReturnKind}.");
        }
    }

    /// <summary>
    /// Pass A + Pass B body-walk typecheck. When the declared return type
    /// is a named struct (or <c>Array&lt;NamedStruct&gt;</c>), verifies the
    /// body's tail RETURN expression against the contract.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Pass A — struct-literal returns.</strong> When the body's
    /// tail RETURN is a struct literal (<c>RETURN { class: ..., score: ... }</c>),
    /// compares the literal's field names against the named type's. Field-kind
    /// verification is intentionally skipped because the parser's literal-kind
    /// inference has known quirks (small numeric literals parse to <c>Int8</c>,
    /// not <c>Int32</c>; <c>RETURN { class: 1, score: 0.5 }</c> would
    /// false-positive on kind).
    /// </para>
    /// <para>
    /// <strong>Pass B — indirect returns.</strong> Compares the model's
    /// declared return-type annotation against the RETURN expression's
    /// derived annotation, recovered from:
    /// <list type="bullet">
    /// <item>Variable references — looked up against the body's DECLAREs +
    /// parameters by name.</item>
    /// <item>UDF / model calls — looked up by qualified name against the
    /// registries; comparison is on the callee's declared <c>RETURNS T</c>
    /// annotation.</item>
    /// <item>CAST expressions — use the cast target as the actual type.</item>
    /// <item>Array literals (<c>[ {...}, {...} ]</c> against
    /// <c>RETURNS Array&lt;NamedStruct&gt;</c>) — recurse Pass A's
    /// field-name check into each element.</item>
    /// <item>Built-in scalar functions — arity/array-ness check only
    /// (named-type names aren't surfaced through <see cref="FunctionDescriptor"/>
    /// today, so a same-kind same-array-ness call passes through).</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>What doesn't fire.</strong> SET reassignment after DECLARE
    /// isn't tracked (only the DECLARE's annotation is consulted); RETURN
    /// expressions inside non-tail control flow are ignored (Pass B looks
    /// at the body's tail statement only).
    /// </para>
    /// </remarks>
    private void ValidateBodyReturnShape(CreateModelStatement create)
    {
        // Resolve the expected return-type triple (kind, isArray, optional
        // named-type name). Skip when the annotation isn't a named type
        // (primitives have no struct shape to verify) and isn't an
        // array of a named type.
        (DataKind Kind, bool IsArray, string? NamedTypeName)? expected =
            ParseAnnotationTriple(create.ReturnTypeName);
        if (expected is null) return;
        if (expected.Value.NamedTypeName is null)
        {
            // No named type in the declared return — there's nothing
            // structural to compare against.
            return;
        }

        // Find the tail RETURN. Bodies must end with RETURN (enforced by
        // ValidateProceduralBody at parse time), but be defensive against
        // an empty body — earlier validation should have caught it.
        if (create.StatementBody.Count == 0) return;
        if (create.StatementBody[^1] is not ReturnStatement ret) return;

        // Pass A — struct-literal-only check against a named (non-array)
        // struct return. Direct comparison of field names without
        // exercising the registries.
        if (ret.Value is StructLiteralExpression literal && !expected.Value.IsArray)
        {
            ValidatePassAStructLiteral(create, expected.Value.NamedTypeName, literal);
            return;
        }

        // Pass B — indirect returns. Walk DECLAREs + parameters into a
        // variable-name → type-annotation map once, then dispatch on
        // the RETURN expression shape.
        Dictionary<string, string> declaredVars = CollectDeclaredVarTypes(create);
        ValidatePassBReturnExpression(create, expected.Value, ret.Value, declaredVars);
    }

    /// <summary>
    /// Parses a textual type annotation into the <c>(kind, isArray,
    /// optional named-type name)</c> triple used by Pass A / Pass B
    /// comparisons. Returns <see langword="null"/> when the annotation
    /// is unrecognised — caller treats that as "skip the check" rather
    /// than throwing, since IMPLEMENTS validation already enforced
    /// resolvability at CREATE time.
    /// </summary>
    private static (DataKind Kind, bool IsArray, string? NamedTypeName)? ParseAnnotationTriple(
        string? annotation)
    {
        if (string.IsNullOrEmpty(annotation)) return null;
        if (!TypeAnnotationResolver.TryParse(annotation, out DataKind kind, out bool isArray))
        {
            return null;
        }
        string inner = StripArrayWrapperForName(annotation);
        string? name = TypeAnnotationResolver.IsNamedType(inner) ? inner : null;
        return (kind, isArray, name);
    }

    /// <summary>
    /// Pass A field-name comparison against a named struct return.
    /// Throws <see cref="QueryPlanException"/> with both expected + actual
    /// field lists on mismatch.
    /// </summary>
    private static void ValidatePassAStructLiteral(
        CreateModelStatement create,
        string expectedNamedType,
        StructLiteralExpression literal)
    {
        IReadOnlyList<StructFieldDescriptor>? expectedFields = ResolveNamedStructFields(expectedNamedType);
        if (expectedFields is null) return; // Defensive — registry/resolver out of sync.

        HashSet<string> expectedNameSet = new(
            expectedFields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
        var actualNameSet = new HashSet<string>(
            literal.Fields.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);

        if (!expectedNameSet.SetEquals(actualNameSet))
        {
            string expectedList = string.Join(", ", expectedFields.Select(f => f.Name));
            string actualList = string.Join(", ", literal.Fields.Select(f => f.Name));
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN struct literal fields don't match declared "
                + $"return type '{expectedNamedType}'. "
                + $"Expected fields: [{expectedList}]. Got: [{actualList}].");
        }
    }

    /// <summary>
    /// Resolves the field list for a named struct via a fresh
    /// <see cref="TypeRegistry"/> — its constructor pre-interns the
    /// vocabulary, so the lookup hits without any external state. Returns
    /// <see langword="null"/> if the registry and resolver are out of sync
    /// (a programming error; callers treat it as "skip").
    /// </summary>
    private static IReadOnlyList<StructFieldDescriptor>? ResolveNamedStructFields(string namedType)
    {
        TypeRegistry registry = new();
        int typeId = registry.GetTypeIdByName(namedType);
        if (typeId == TypeRegistry.NoType) return null;
        TypeDescriptor? desc = registry.GetDescriptor(typeId);
        return desc?.Fields;
    }

    /// <summary>
    /// Walks the body's DECLARE statements and the model's parameter list
    /// into a name → declared-type-annotation map. Used by Pass B to
    /// resolve <c>RETURN varname</c> against the variable's declared
    /// type. SET-after-DECLARE reassignment isn't tracked — only the
    /// initial DECLARE annotation is consulted.
    /// </summary>
    private static Dictionary<string, string> CollectDeclaredVarTypes(CreateModelStatement create)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (UdfParameter param in create.Parameters)
        {
            if (param.TypeName is not null)
            {
                result[param.Name] = param.TypeName;
            }
        }
        foreach (Statement stmt in create.StatementBody)
        {
            CollectDeclaredVarTypesInStatement(stmt, result);
        }
        return result;
    }

    private static void CollectDeclaredVarTypesInStatement(
        Statement stmt, Dictionary<string, string> result)
    {
        switch (stmt)
        {
            case DeclareStatement decl when decl.TypeName is not null:
                // First DECLARE wins per scope. Re-DECLARE in inner blocks
                // would shadow but Pass B doesn't model nested scopes — the
                // outer DECLARE drives the check, which matches the
                // straight-line bodies Pass B targets.
                result.TryAdd(decl.VariableName, decl.TypeName);
                break;
            case BlockStatement block:
                foreach (Statement inner in block.Statements)
                {
                    CollectDeclaredVarTypesInStatement(inner, result);
                }
                break;
            case IfStatement ifStmt:
                CollectDeclaredVarTypesInStatement(ifStmt.Then, result);
                if (ifStmt.Else is not null)
                {
                    CollectDeclaredVarTypesInStatement(ifStmt.Else, result);
                }
                break;
            case WhileStatement whileStmt:
                CollectDeclaredVarTypesInStatement(whileStmt.Body, result);
                break;
        }
    }

    /// <summary>
    /// Pass B dispatch on the RETURN expression. Compares against the
    /// declared return-type triple via <see cref="MatchAnnotationTriples"/>;
    /// shapes that can't be resolved to a concrete annotation skip the
    /// check rather than false-positiving (the runtime path will still
    /// surface a clean error if the return is genuinely wrong).
    /// </summary>
    private void ValidatePassBReturnExpression(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        Expression value,
        IReadOnlyDictionary<string, string> declaredVars)
    {
        // Array literal (parsed as `array(...)` function call).
        if (value is FunctionCallExpression arrayCall
            && arrayCall.SchemaName is null
            && string.Equals(arrayCall.FunctionName, "array", StringComparison.OrdinalIgnoreCase))
        {
            ValidatePassBArrayLiteral(create, expected, arrayCall);
            return;
        }

        // Variable / parameter reference. Bare identifiers parse as
        // ColumnReference(TableName=null) inside a model body; the
        // parameter-ref form ($name) parses as ParameterExpression.
        string? actualAnnotation = null;
        switch (value)
        {
            case ColumnReference col when col.TableName is null && col.SchemaName is null:
                declaredVars.TryGetValue(col.ColumnName, out actualAnnotation);
                break;

            case ParameterExpression param:
                declaredVars.TryGetValue(param.Name, out actualAnnotation);
                break;

            case CastExpression cast:
                actualAnnotation = cast.TargetType;
                break;

            case FunctionCallExpression fnCall:
                actualAnnotation = ResolveCalleeReturnAnnotation(fnCall);
                if (actualAnnotation is null)
                {
                    // Built-in scalar function — fall back to
                    // arity/array-ness check via descriptor signatures.
                    ValidatePassBBuiltinCall(create, expected, fnCall);
                    return;
                }
                break;
        }

        if (actualAnnotation is null) return; // Unresolvable — skip.

        (DataKind, bool, string?)? actual = ParseAnnotationTriple(actualAnnotation);
        if (actual is null) return; // Unparseable — skip.

        if (!MatchAnnotationTriples(expected, actual.Value))
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN expression type '{actualAnnotation}' "
                + $"doesn't match declared return type '{create.ReturnTypeName}'. "
                + $"Expected: {FormatTriple(expected)}. Got: {FormatTriple(actual.Value)}.");
        }
    }

    /// <summary>
    /// Pass B array-literal check. When the declared return is
    /// <c>Array&lt;NamedStruct&gt;</c> and the body returns <c>[{...}, ...]</c>,
    /// applies Pass A's field-name comparison to each struct-literal
    /// element. Empty arrays skip — Pass B can't infer per-element shape
    /// from an empty literal.
    /// </summary>
    private static void ValidatePassBArrayLiteral(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        FunctionCallExpression arrayCall)
    {
        if (!expected.IsArray)
        {
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN expression is an array literal but "
                + $"declared return type '{create.ReturnTypeName}' is not an array.");
        }

        if (expected.NamedTypeName is null) return; // Array<scalar> — nothing structural to check.

        foreach (Expression element in arrayCall.Arguments)
        {
            if (element is StructLiteralExpression elementLiteral)
            {
                ValidatePassAStructLiteral(create, expected.NamedTypeName, elementLiteral);
            }
            // Non-literal elements (variable refs, function calls) inside
            // an array literal would need recursive Pass B dispatch.
            // Defer until a real case arrives — most array-literal returns
            // are inlined struct literals.
        }
    }

    /// <summary>
    /// Looks up <paramref name="fnCall"/> against the UDF and declared-model
    /// registries (in that order) and returns the callee's declared
    /// <c>RETURNS T</c> annotation. Returns <see langword="null"/> when the
    /// call resolves to a built-in or to nothing (the caller falls back to
    /// the built-in shape check or skips).
    /// </summary>
    private string? ResolveCalleeReturnAnnotation(FunctionCallExpression fnCall)
    {
        // Explicit `models.X(...)` qualifier — go straight to DeclaredModels.
        if (string.Equals(fnCall.SchemaName, "models", StringComparison.OrdinalIgnoreCase))
        {
            QualifiedName modelQn = new("models", fnCall.FunctionName);
            if (_catalog.DeclaredModels.TryGet(modelQn, out ModelDescriptor? model))
            {
                return model.ReturnTypeName;
            }
            return null;
        }

        // UDF lookup — explicit schema or search-path walk.
        if (_udfs.TryResolve(fnCall.SchemaName, fnCall.FunctionName, _catalog.SearchPath,
            out UdfDescriptor? udf))
        {
            return udf.ReturnTypeName;
        }

        // Unqualified call that didn't resolve as a UDF — could still be a
        // model in the `models` schema.
        if (fnCall.SchemaName is null)
        {
            QualifiedName modelQn = new("models", fnCall.FunctionName);
            if (_catalog.DeclaredModels.TryGet(modelQn, out ModelDescriptor? model))
            {
                return model.ReturnTypeName;
            }
        }

        return null;
    }

    /// <summary>
    /// Pass B built-in fallback. When the RETURN expression resolves to a
    /// registered built-in scalar function, check whether every matched
    /// signature variant produces an array vs. a scalar. If all variants
    /// agree and disagree with the declared <c>RETURNS T</c> array-ness,
    /// throw. Otherwise skip (named-type names aren't surfaced through
    /// <see cref="FunctionDescriptor"/>, so we can't compare by name).
    /// </summary>
    private void ValidatePassBBuiltinCall(
        CreateModelStatement create,
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        FunctionCallExpression fnCall)
    {
        FunctionDescriptor? descriptor = _functions.TryGetScalarDescriptor(fnCall.CallName);
        if (descriptor is null || descriptor.Signatures.Count == 0) return;

        bool allProduceArray = true;
        bool noneProduceArray = true;
        foreach (FunctionSignatureVariant variant in descriptor.Signatures)
        {
            if (variant.ReturnType.ProducesArray) noneProduceArray = false;
            else allProduceArray = false;
        }

        // Mixed signatures — can't decide statically; skip.
        if (!allProduceArray && !noneProduceArray) return;

        bool builtinIsArray = allProduceArray;
        if (builtinIsArray != expected.IsArray)
        {
            string actualShape = builtinIsArray ? "Array<...>" : "scalar";
            throw new QueryPlanException(
                $"CREATE MODEL {create.Name}: RETURN built-in '{fnCall.CallName}' produces "
                + $"{actualShape} but declared return type '{create.ReturnTypeName}' is "
                + $"{(expected.IsArray ? "an array" : "a scalar")}.");
        }
    }

    /// <summary>
    /// Pass B comparison primitive. Triples match when kind + isArray are
    /// equal and either the expected has no named-type constraint or the
    /// actual carries the same name (case-insensitive).
    /// </summary>
    private static bool MatchAnnotationTriples(
        (DataKind Kind, bool IsArray, string? NamedTypeName) expected,
        (DataKind Kind, bool IsArray, string? NamedTypeName) actual)
    {
        if (expected.Kind != actual.Kind) return false;
        if (expected.IsArray != actual.IsArray) return false;
        if (expected.NamedTypeName is null) return true;
        // Expected has a named-type constraint. Actual must carry the
        // same name; an unnamed actual (e.g. a built-in returning bare
        // Struct) doesn't match.
        return actual.NamedTypeName is not null
            && string.Equals(expected.NamedTypeName, actual.NamedTypeName,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Human-readable rendering of a triple for Pass B error messages.</summary>
    private static string FormatTriple((DataKind Kind, bool IsArray, string? NamedTypeName) triple)
        => (triple.IsArray, triple.NamedTypeName) switch
        {
            (false, null) => triple.Kind.ToString(),
            (true, null) => $"Array<{triple.Kind}>",
            (false, _) => triple.NamedTypeName!,
            (true, _) => $"Array<{triple.NamedTypeName}>",
        };

    /// <summary>
    /// Returns the inner identifier when <paramref name="annotation"/> is
    /// wrapped in <c>Array&lt;...&gt;</c>, otherwise <paramref name="annotation"/>
    /// unchanged. Used by contract validation to check whether an
    /// annotation's element is a named type independent of array-ness.
    /// </summary>
    private static string StripArrayWrapperForName(string annotation)
    {
        const string Prefix = "Array<";
        if (annotation.Length > Prefix.Length + 1
            && annotation.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)
            && annotation[^1] == '>')
        {
            return annotation[Prefix.Length..^1].Trim();
        }
        return annotation;
    }
}
