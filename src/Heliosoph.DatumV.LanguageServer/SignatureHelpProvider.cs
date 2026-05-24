using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower.Model;

namespace Heliosoph.DatumV.LanguageServer;

/// <summary>
/// Resolves the function call enclosing a cursor position and produces a
/// <see cref="SignatureHelp"/> describing the function's call shape — name,
/// parameter list, return kind — plus which parameter the cursor is
/// currently filling in. Editors render this as the floating tooltip that
/// stays visible while typing arguments.
/// </summary>
/// <remarks>
/// <para>
/// <strong>How the lookup works.</strong> The cursor's enclosing call is
/// the unmatched left paren reached by walking back token-by-token while
/// tracking paren depth. The function name comes from the token immediately
/// before that paren (with optional <c>udf.</c> or <c>models.</c>
/// qualifier). The active parameter index is the count of commas at the
/// call's depth between the paren and the cursor.
/// </para>
/// <para>
/// <strong>Overloads.</strong> Built-in functions with multiple call shapes
/// (the manifest's <see cref="FunctionSignature.AdditionalParameterShapes"/>)
/// surface every variant; the editor renders them as a "1 of N" carousel
/// via LSP <c>ActiveSignature</c>. The default pick prefers the variant
/// whose parameter at the cursor slot matches the first token of the
/// current argument — <c>{…}</c> picks the scalar <c>Struct</c> shape,
/// <c>[…]</c> picks the <c>Array&lt;…&gt;</c> shape — with arity as the
/// tiebreaker.
/// </para>
/// <para>
/// <strong>What it doesn't do.</strong> It doesn't recover gracefully from
/// deeply malformed SQL — the token stream is what it is, and a missing
/// left paren just means no signature help fires.
/// </para>
/// </remarks>
public sealed class SignatureHelpProvider
{
    private readonly LanguageServerManifest _manifest;

    /// <summary>Creates a provider over the given manifest.</summary>
    public SignatureHelpProvider(LanguageServerManifest manifest)
    {
        _manifest = manifest;
    }

    /// <summary>
    /// Resolves the function call enclosing <paramref name="offset"/> in
    /// <paramref name="sql"/>. Returns <see langword="null"/> when the
    /// cursor isn't inside any function call's argument list.
    /// </summary>
    public SignatureHelp? GetSignatureHelp(string sql, int offset)
    {
        if (offset < 0 || offset > sql.Length) return null;

        // Tokenize the prefix up to the cursor. Trailing partial input
        // ("models." just typed, in-progress identifier, etc.) is handled
        // by best-effort fallback — we don't need a perfect parse, just
        // enough of the token stream to walk back through.
        if (!TryTokenize(sql[..offset], out List<Token<SqlToken>> tokens))
        {
            return null;
        }

        if (!TryFindEnclosingCall(tokens, out string functionName, out int activeParameter, out SqlToken? activeArgFirstToken))
        {
            return null;
        }

        return ResolveSignature(functionName, activeParameter, activeArgFirstToken);
    }

    /// <summary>
    /// Tokenizes <paramref name="prefix"/>, retrying once with a trailing
    /// sigil stripped so a partial <c>@</c> / <c>$</c> at the cursor doesn't
    /// fail the whole pass. Mirrors <c>CompletionContext.TokenizeSafely</c>'s
    /// behaviour.
    /// </summary>
    private static bool TryTokenize(string prefix, out List<Token<SqlToken>> tokens)
    {
        tokens = new List<Token<SqlToken>>();
        try
        {
            foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(prefix))
            {
                tokens.Add(t);
            }
            return true;
        }
        catch
        {
        }

        if (prefix.Length > 0 && (prefix[^1] == '@' || prefix[^1] == '$'))
        {
            tokens = new List<Token<SqlToken>>();
            try
            {
                foreach (Token<SqlToken> t in SqlTokenizer.Instance.Tokenize(prefix[..^1]))
                {
                    tokens.Add(t);
                }
                return true;
            }
            catch
            {
            }
        }

        return false;
    }

    /// <summary>
    /// Walks the token stream backward from the end, balancing paren depth.
    /// Returns the function name, zero-based active parameter index, and
    /// the first token of the active argument (or <see langword="null"/>
    /// when the argument is empty so far) when the unmatched left paren is
    /// preceded by a callable identifier (optionally namespace-qualified
    /// with <c>udf.</c> or <c>models.</c>).
    /// </summary>
    /// <remarks>
    /// The active-argument first token feeds overload disambiguation: a
    /// <c>{</c> signals a struct literal and biases toward
    /// <c>Struct</c>-shaped slots, <c>[</c> signals an array literal and
    /// biases toward <c>Array&lt;…&gt;</c> slots. Without it, two variants
    /// with the same arity (e.g. <c>image_draw_bounding_boxes(image, boxes)</c>
    /// where <c>boxes</c> is either a single <c>Struct</c> or
    /// <c>Array&lt;Struct&gt;</c>) would tie and always render the primary
    /// variant first.
    /// </remarks>
    private static bool TryFindEnclosingCall(
        IReadOnlyList<Token<SqlToken>> tokens,
        out string functionName,
        out int activeParameter,
        out SqlToken? activeArgFirstToken)
    {
        functionName = "";
        activeParameter = 0;
        activeArgFirstToken = null;

        int depth = 0;
        int commaCount = 0;
        // Index of the first token belonging to the current (active) argument.
        // Walking back, this is the token after the most recent depth-0 comma
        // (or after the unmatched left paren when no comma was seen).
        int activeArgStart = tokens.Count;
        for (int i = tokens.Count - 1; i >= 0; i--)
        {
            SqlToken kind = tokens[i].Kind;
            switch (kind)
            {
                case SqlToken.RightParen:
                case SqlToken.RightBracket:
                case SqlToken.RightBrace:
                    depth++;
                    continue;
                case SqlToken.LeftBracket:
                case SqlToken.LeftBrace:
                    if (depth > 0) depth--;
                    continue;
                case SqlToken.LeftParen:
                    if (depth > 0)
                    {
                        depth--;
                        continue;
                    }
                    // Unmatched left paren — this is the enclosing argument list.
                    if (activeArgStart > i + 1 && activeArgStart < tokens.Count)
                    {
                        activeArgFirstToken = tokens[activeArgStart].Kind;
                    }
                    else if (i + 1 < tokens.Count && commaCount == 0)
                    {
                        activeArgFirstToken = tokens[i + 1].Kind;
                    }
                    return TryReadFunctionName(tokens, i, out functionName, commaCount, out activeParameter);
                case SqlToken.Comma:
                    if (depth == 0)
                    {
                        // The comma we just walked past closes the current
                        // arg from the back; everything after this index is
                        // a *later* arg, everything from i+1 onward up to
                        // the next depth-0 comma is the active arg if no
                        // earlier depth-0 comma was seen first.
                        if (commaCount == 0) activeArgStart = i + 1;
                        commaCount++;
                    }
                    continue;
                default:
                    continue;
            }
        }
        return false;
    }

    /// <summary>
    /// Reads the function name from the tokens immediately before
    /// <paramref name="parenIndex"/>. Accepts <c>name(</c>,
    /// <c>namespace.name(</c> and <c>NameKeyword(</c> (e.g. <c>CAST</c>,
    /// <c>SUM</c>) — anything that the parser treats as a callable.
    /// </summary>
    private static bool TryReadFunctionName(
        IReadOnlyList<Token<SqlToken>> tokens,
        int parenIndex,
        out string name,
        int commaCount,
        out int activeParameter)
    {
        name = "";
        activeParameter = commaCount;

        if (parenIndex == 0) return false;
        Token<SqlToken> prev = tokens[parenIndex - 1];
        if (!IsCallableNameToken(prev.Kind)) return false;

        // Detect `qualifier.name(`. The identifier before the dot is the
        // namespace (`udf`, `models`, or in the future `tasks` / `proc`).
        if (parenIndex >= 3
            && tokens[parenIndex - 2].Kind == SqlToken.Dot
            && IsCallableNameToken(tokens[parenIndex - 3].Kind))
        {
            string qualifier = tokens[parenIndex - 3].ToStringValue();
            string member = prev.ToStringValue();
            name = $"{qualifier}.{member}";
            return true;
        }

        name = prev.ToStringValue();
        return true;
    }

    private static bool IsCallableNameToken(SqlToken kind) =>
        kind == SqlToken.Identifier
        || kind == SqlToken.TypeKeyword
        // CAST is the only reserved keyword that doubles as a function name.
        // Aggregates (sum/avg/min/max/count) tokenise as Identifier and so
        // are already accepted via the first branch.
        || kind == SqlToken.Cast;

    /// <summary>
    /// Looks up <paramref name="functionName"/> in the manifest and builds
    /// the matching signature. Resolution order:
    /// <list type="number">
    ///   <item><description>The <c>models.</c> call namespace (pre-S9; the
    ///     model registry isn't a real schema yet).</description></item>
    ///   <item><description>UDFs / procedures in an explicit schema, or walked
    ///     across the session search_path for unqualified names.</description></item>
    ///   <item><description>Built-in scalar / aggregate / window / table-valued
    ///     functions.</description></item>
    /// </list>
    /// Returns <see langword="null"/> when nothing matches — the editor then
    /// hides the tooltip.
    /// </summary>
    private SignatureHelp? ResolveSignature(string functionName, int activeParameter, SqlToken? activeArgFirstToken)
    {
        if (functionName.StartsWith("models.", StringComparison.OrdinalIgnoreCase))
        {
            return BuildModelSignature(functionName["models.".Length..], activeParameter);
        }

        // Split dotted form into (schema, name); bare name leaves schema null.
        int dot = functionName.IndexOf('.');
        string? explicitSchema = dot > 0 ? functionName[..dot] : null;
        string bareName = dot > 0 ? functionName[(dot + 1)..] : functionName;

        // UDF / procedure resolution mirrors the engine's runtime: explicit
        // schema does an exact match; unqualified walks search_path.
        SignatureHelp? routine = BuildRoutineSignature(explicitSchema, bareName, activeParameter);
        if (routine is not null) return routine;

        // Built-ins live in many schemas (system, inference, tokenizer,
        // templates, …) and TVFs do too. Search the explicit schema when
        // qualified; walk search_path when not.
        return BuildBuiltinSignature(explicitSchema, bareName, activeParameter, activeArgFirstToken);
    }

    private SignatureHelp? BuildRoutineSignature(string? explicitSchema, string name, int activeParameter)
    {
        UdfEntry? udf = ResolveUdf(explicitSchema, name);
        if (udf is not null) return BuildUdfSignature(udf, activeParameter);

        ProcedureEntry? proc = ResolveProcedure(explicitSchema, name);
        if (proc is not null) return BuildProcedureSignature(proc, activeParameter);

        return null;
    }

    private UdfEntry? ResolveUdf(string? explicitSchema, string name)
    {
        if (_manifest.Udfs is null) return null;
        if (explicitSchema is not null)
        {
            foreach (UdfEntry e in _manifest.Udfs)
            {
                if (string.Equals(e.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
            return null;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (UdfEntry e in _manifest.Udfs)
            {
                if (string.Equals(e.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }
        return null;
    }

    private ProcedureEntry? ResolveProcedure(string? explicitSchema, string name)
    {
        if (_manifest.Procedures is null) return null;
        if (explicitSchema is not null)
        {
            foreach (ProcedureEntry e in _manifest.Procedures)
            {
                if (string.Equals(e.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
            return null;
        }
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (ProcedureEntry e in _manifest.Procedures)
            {
                if (string.Equals(e.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return e;
                }
            }
        }
        return null;
    }

    private SignatureHelp BuildUdfSignature(UdfEntry entry, int activeParameter)
    {
        IReadOnlyList<ParameterSignature> parameters = entry.Parameters ?? Array.Empty<ParameterSignature>();
        (string label, IReadOnlyList<ParameterInfo> paramInfos) = BuildLabel(
            $"{entry.SchemaName}.{entry.Name}", parameters, entry.ReturnType);

        List<string> tags = new(2);
        if (entry.BodyKind is not null) tags.Add(entry.BodyKind);
        if (entry.IsPure) tags.Add("pure");
        string? doc = tags.Count > 0 ? string.Join(" · ", tags) : null;

        return new SignatureHelp
        {
            Signatures =
            [
                new SignatureInfo
                {
                    Label = label,
                    Documentation = doc,
                    Parameters = paramInfos,
                },
            ],
            ActiveSignature = 0,
            ActiveParameter = ClampActiveParameter(activeParameter, parameters.Count),
        };
    }

    private SignatureHelp BuildProcedureSignature(ProcedureEntry entry, int activeParameter)
    {
        IReadOnlyList<ParameterSignature> parameters = entry.Parameters ?? Array.Empty<ParameterSignature>();
        (string label, IReadOnlyList<ParameterInfo> paramInfos) = BuildLabel(
            $"{entry.SchemaName}.{entry.Name}", parameters, returnType: null);
        return new SignatureHelp
        {
            Signatures =
            [
                new SignatureInfo
                {
                    Label = label,
                    Documentation = "procedure · invoke via CALL",
                    Parameters = paramInfos,
                },
            ],
            ActiveSignature = 0,
            ActiveParameter = ClampActiveParameter(activeParameter, parameters.Count),
        };
    }

    private SignatureHelp? BuildModelSignature(string name, int activeParameter)
    {
        if (_manifest.Models is null) return null;
        ModelEntry? entry = FindByName(_manifest.Models, name, e => e.Name);
        if (entry is null) return null;

        IReadOnlyList<ParameterSignature> parameters = entry.Parameters ?? Array.Empty<ParameterSignature>();
        (string label, IReadOnlyList<ParameterInfo> paramInfos) = BuildLabel(
            $"models.{entry.Name}", parameters, entry.OutputKind);

        string? doc = entry.DisplayName is not null && entry.Backend is not null
            ? $"{entry.DisplayName} ({entry.Backend})"
            : entry.DisplayName ?? (entry.Backend is not null ? $"backend: {entry.Backend}" : null);

        return new SignatureHelp
        {
            Signatures =
            [
                new SignatureInfo
                {
                    Label = label,
                    Documentation = doc,
                    Parameters = paramInfos,
                },
            ],
            ActiveSignature = 0,
            ActiveParameter = ClampActiveParameter(activeParameter, parameters.Count),
        };
    }

    private SignatureHelp? BuildBuiltinSignature(string? explicitSchema, string name, int activeParameter, SqlToken? activeArgFirstToken)
    {
        FunctionSignature? entry = ResolveBuiltin(explicitSchema, name);
        if (entry is null) return null;

        // Render qualified label when the function lives outside `system`,
        // so the popup matches what the user typed (inference.devices(...)
        // rather than just devices(...)).
        string callableName = string.IsNullOrEmpty(entry.SchemaName)
            || string.Equals(entry.SchemaName, "system", StringComparison.OrdinalIgnoreCase)
            ? entry.Name
            : $"{entry.SchemaName}.{entry.Name}";

        // Assemble every declared call shape: the primary Parameters list
        // plus any AdditionalParameterShapes (overload variants — e.g.
        // image_draw_bounding_boxes' single-Struct vs Array<Struct>).
        int variantCount = 1 + (entry.AdditionalParameterShapes?.Count ?? 0);
        List<IReadOnlyList<ParameterSignature>> variants = new(variantCount)
        {
            entry.Parameters,
        };
        if (entry.AdditionalParameterShapes is { } extras)
        {
            foreach (IReadOnlyList<ParameterSignature> v in extras) variants.Add(v);
        }

        SignatureInfo[] signatures = new SignatureInfo[variants.Count];
        for (int i = 0; i < variants.Count; i++)
        {
            (string label, IReadOnlyList<ParameterInfo> paramInfos) = BuildLabel(
                callableName, variants[i], entry.ReturnType);
            signatures[i] = new SignatureInfo
            {
                Label = label,
                Documentation = entry.Description,
                Parameters = paramInfos,
            };
        }

        int activeSig = variants.Count == 1
            ? 0
            : PickActiveSignature(variants, activeParameter, activeArgFirstToken);

        return new SignatureHelp
        {
            Signatures = signatures,
            ActiveSignature = activeSig,
            ActiveParameter = ClampActiveParameter(activeParameter, variants[activeSig].Count),
        };
    }

    /// <summary>
    /// Picks the most plausible overload to highlight by default. Scoring
    /// favours variants whose slot at the cursor position exists and whose
    /// declared kind matches the first token of the current argument
    /// (<c>{</c> → scalar <c>Struct</c>, <c>[</c> → <c>Array&lt;…&gt;</c>);
    /// arity proximity to the typed-argument count is the tiebreaker.
    /// Primary variant wins ties — preserves prior behaviour when the
    /// cursor sits on the function name with no arguments typed yet.
    /// </summary>
    private static int PickActiveSignature(
        IReadOnlyList<IReadOnlyList<ParameterSignature>> variants,
        int activeParameter,
        SqlToken? activeArgFirstToken)
    {
        int typedArgs = activeParameter + 1;
        int bestIndex = 0;
        int bestScore = int.MinValue;
        for (int i = 0; i < variants.Count; i++)
        {
            IReadOnlyList<ParameterSignature> v = variants[i];
            int score = 0;
            if (activeParameter >= 0 && activeParameter < v.Count)
            {
                score += 100;
                string kind = v[activeParameter].Kind;
                bool kindIsArray = kind.StartsWith("Array<", StringComparison.OrdinalIgnoreCase)
                    || kind.EndsWith("[]", StringComparison.Ordinal);
                if (activeArgFirstToken == SqlToken.LeftBrace)
                {
                    score += kindIsArray ? -25 : 50;
                }
                else if (activeArgFirstToken == SqlToken.LeftBracket)
                {
                    score += kindIsArray ? 50 : -25;
                }
            }
            score -= Math.Abs(v.Count - typedArgs);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    /// <summary>
    /// Builds the signature label and per-parameter sub-ranges. Parameter
    /// labels are the substrings the editor highlights when each parameter
    /// is active — assembling them by hand keeps the substring positions
    /// trivially correct.
    /// </summary>
    private static (string Label, IReadOnlyList<ParameterInfo> Parameters) BuildLabel(
        string callableName,
        IReadOnlyList<ParameterSignature> parameters,
        string? returnType)
    {
        if (parameters.Count == 0)
        {
            string emptyLabel = returnType is null ? $"{callableName}()" : $"{callableName}() → {returnType}";
            return (emptyLabel, Array.Empty<ParameterInfo>());
        }

        string[] paramLabels = new string[parameters.Count];
        ParameterInfo[] paramInfos = new ParameterInfo[parameters.Count];
        for (int i = 0; i < parameters.Count; i++)
        {
            ParameterSignature p = parameters[i];
            string optMark = p.IsOptional ? "?" : "";
            string paramLabel = $"{p.Name}: {p.Kind}{optMark}";
            paramLabels[i] = paramLabel;
            paramInfos[i] = new ParameterInfo { Label = paramLabel };
        }

        string label = $"{callableName}({string.Join(", ", paramLabels)})";
        if (returnType is not null) label += $" → {returnType}";
        return (label, paramInfos);
    }

    private FunctionSignature? ResolveBuiltin(string? explicitSchema, string name)
    {
        if (explicitSchema is not null)
        {
            foreach (FunctionSignature f in _manifest.Functions)
            {
                if (string.Equals(f.SchemaName, explicitSchema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
            return null;
        }
        // Unqualified: prefer a match on a search-path schema.
        foreach (string schema in _manifest.SearchPath)
        {
            foreach (FunctionSignature f in _manifest.Functions)
            {
                if (string.Equals(f.SchemaName, schema, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return f;
                }
            }
        }
        // Fall back to any schema for callers (offline JSON manifests, etc.)
        // whose entries default SchemaName to "system" even when not on path.
        foreach (FunctionSignature f in _manifest.Functions)
        {
            if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return f;
            }
        }
        return null;
    }

    private static T? FindByName<T>(IReadOnlyList<T> entries, string name, Func<T, string> nameOf)
        where T : class
    {
        foreach (T entry in entries)
        {
            if (string.Equals(nameOf(entry), name, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Caps the active parameter index at the last declared parameter when
    /// the user has typed more commas than the function expects. Without
    /// this the popup vanishes (LSP convention is to hide when the index is
    /// out of range), but keeping the last parameter highlighted reads as
    /// "you've gone past the end" — closer to the user's mental model than
    /// silent dismissal.
    /// </summary>
    private static int ClampActiveParameter(int activeParameter, int declaredCount)
    {
        if (declaredCount == 0) return 0;
        if (activeParameter < 0) return 0;
        if (activeParameter >= declaredCount) return declaredCount - 1;
        return activeParameter;
    }
}
