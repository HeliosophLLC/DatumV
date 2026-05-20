namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Parsing.Tokens;
using Superpower.Model;

/// <summary>
/// Walks the token stream once to determine which lambda parameter
/// scopes are active at a given cursor offset. Used by the hover and
/// completion providers so a reference to <c>t</c> or <c>x</c> inside an
/// <c>animate_frames</c> or <c>draw_particles</c> lambda body resolves
/// to a lambda parameter instead of falling through to the column /
/// variable lookup.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Token-based, not AST-based.</strong> The AST nodes carry only
/// the arrow's <see cref="Heliosoph.DatumV.Parsing.Ast.SourceSpan"/> — not the
/// body's full extent — so deriving "cursor is inside this lambda's
/// body" from the AST would need a recursive span union that doesn't
/// generalise to multi-line lambdas. Tracking parenthesis depth across
/// the raw token stream gives us the body span for free: a lambda's body
/// runs from the <c>-&gt;</c> until either a closing bracket pops back
/// below the depth the lambda was declared at, or a comma at exactly
/// that depth ends the enclosing call's current argument.
/// </para>
/// <para>
/// <strong>Outer-call context resolution.</strong> When the lambda is the
/// argument to a function call, we also identify the call name and the
/// argument position the lambda fills. The hover provider feeds those
/// into the manifest to learn the lambda's
/// <see cref="ParameterSignature.LambdaContextName"/>, which in turn
/// drives the parameter's display-time description (e.g. <em>"current
/// frame's normalised time"</em> for <c>AnimationContext</c>).
/// </para>
/// </remarks>
internal static class LambdaScopeWalker
{
    /// <summary>
    /// Convenience overload: tokenises <paramref name="sql"/> and looks up
    /// the innermost lambda scope's context name from the manifest. Returns
    /// <see langword="null"/> when the cursor isn't inside a lambda body
    /// whose outer call's parameter slot declares a
    /// <see cref="ParameterSignature.LambdaContextName"/>. Used by the
    /// completion provider to filter the function whitelist when the
    /// cursor's inside an animation / particle lambda body.
    /// </summary>
    public static string? TryFindCurrentLambdaContextName(
        string sql, int cursorOffset, LanguageServerManifest manifest)
    {
        List<TokenHit> tokens = Tokenize(sql);
        IReadOnlyList<LambdaScope> active = FindActiveScopes(tokens, cursorOffset);
        if (active.Count == 0) return null;
        // Innermost lambda wins on shadowing — same precedence the hover
        // provider uses.
        LambdaScope inner = active[^1];
        if (inner.OuterCallName is null || inner.OuterArgIndex < 0) return null;
        return ResolveLambdaContextName(manifest, inner.OuterCallName, inner.OuterArgIndex);
    }

    /// <summary>
    /// Detects "the cursor is on a lambda parameter being declared", e.g.
    /// the <c>t</c> in <c>(t) -&gt; body</c> when the cursor sits on the
    /// parameter name rather than inside the body. The normal
    /// <see cref="FindActiveScopes"/> walker only pushes a scope once it
    /// processes the <c>-&gt;</c> token, so a cursor on the parameter
    /// declaration returns no active scope — and hover sees nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Looks forward from <paramref name="identifierIndex"/> for an
    /// arrow token, validating that the path between is a plausible
    /// lambda parameter list (single identifier, or
    /// <c>(name1, name2, …)</c>). If matched, synthesises the
    /// <see cref="LambdaScope"/> the walker would have pushed once it
    /// reached the arrow, so callers can format the parameter's hover
    /// exactly as they would for a body-usage of the same name.
    /// </para>
    /// </remarks>
    public static LambdaScope? TryFindLambdaScopeForParameterDeclaration(
        IReadOnlyList<TokenHit> tokens, int identifierIndex)
    {
        int? arrowIndex = FindArrowForParameterDeclaration(tokens, identifierIndex);
        if (arrowIndex is null) return null;

        // Re-simulate the walker forward to the arrow position so we have
        // the same depth + argIndexStack state TryDeclareLambdaScope expects.
        int depth = 0;
        Stack<int> stack = new();
        for (int i = 0; i < arrowIndex.Value; i++)
        {
            switch (tokens[i].Kind)
            {
                case SqlToken.LeftParen:
                case SqlToken.LeftBracket:
                    depth++;
                    stack.Push(0);
                    break;
                case SqlToken.RightParen:
                case SqlToken.RightBracket:
                    if (stack.Count > 0) stack.Pop();
                    depth--;
                    break;
                case SqlToken.Comma:
                    if (stack.Count > 0)
                    {
                        int top = stack.Pop();
                        stack.Push(top + 1);
                    }
                    break;
            }
        }
        return TryDeclareLambdaScope(tokens, arrowIndex.Value, depth, stack);
    }

    /// <summary>
    /// Returns the token index of the <c>-&gt;</c> that <paramref name="identifierIndex"/>
    /// is a parameter for, or <see langword="null"/> if not a lambda
    /// parameter position. Two shapes recognised:
    /// <list type="bullet">
    ///   <item>Single-parameter form: <c>name -&gt; …</c> — identifier directly followed by Arrow.</item>
    ///   <item>Paren-wrapped form: <c>(a, name, b) -&gt; …</c> — identifier sits inside <c>(...)</c> whose closing <c>)</c> is followed by Arrow.</item>
    /// </list>
    /// </summary>
    private static int? FindArrowForParameterDeclaration(
        IReadOnlyList<TokenHit> tokens, int identifierIndex)
    {
        if (identifierIndex < 0 || identifierIndex >= tokens.Count) return null;

        // Single-parameter form.
        if (identifierIndex + 1 < tokens.Count
            && tokens[identifierIndex + 1].Kind == SqlToken.Arrow)
        {
            return identifierIndex + 1;
        }

        // Paren-wrapped form: scan forward, balance brackets. depth starts at 1
        // because we assume the identifier sits one level inside a `(`.
        int depth = 1;
        for (int k = identifierIndex + 1; k < tokens.Count; k++)
        {
            SqlToken kind = tokens[k].Kind;
            switch (kind)
            {
                case SqlToken.LeftParen:
                case SqlToken.LeftBracket:
                    depth++;
                    break;
                case SqlToken.RightParen:
                case SqlToken.RightBracket:
                    depth--;
                    if (depth == 0)
                    {
                        // Closing paren of the enclosing group — next token
                        // must be Arrow for this to be a lambda param list.
                        return k + 1 < tokens.Count && tokens[k + 1].Kind == SqlToken.Arrow
                            ? k + 1
                            : null;
                    }
                    break;
                case SqlToken.Comma:
                case SqlToken.Identifier:
                    // Allowed inside the param list.
                    break;
                default:
                    // Any other token at depth 1 means this isn't a clean
                    // parameter list (e.g. a function call or expression
                    // — we're in a regular grouped expression, not a lambda).
                    if (depth == 1) return null;
                    break;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns the name and argument index of the function call that
    /// immediately encloses <paramref name="cursorOffset"/>, or
    /// <c>(null, -1)</c> when the cursor isn't inside a call's argument
    /// list. Used by the completion provider to surface enumerated-string
    /// values (see <see cref="ParameterSignature.EnumValues"/>) when the
    /// cursor sits inside a string literal in a known argument slot —
    /// <c>blend(content, 'a|dd')</c> resolves to <c>("blend", 1)</c>.
    /// </summary>
    /// <remarks>
    /// Re-walks the token stream up to <paramref name="cursorOffset"/>
    /// tracking the same paren-depth + per-depth argument index state
    /// <see cref="FindActiveScopes"/> uses. On return, the top of the
    /// arg-index stack is the current call's arg index, and a quick
    /// look-back from the most recent unclosed <c>(</c> recovers the
    /// call name.
    /// </remarks>
    public static (string? CallName, int ArgIndex) FindEnclosingCallAndArgIndex(
        IReadOnlyList<TokenHit> tokens, int cursorOffset)
    {
        // We need to track WHICH `(` opened the current call AND its arg
        // index. Stack stores (callNameTokenIndex, argIndex) pairs — the
        // top is always the innermost call we're inside.
        // The call name is the token at index (openParenIndex - 1) when
        // that token is an Identifier or keyword-as-function; otherwise
        // the bracket isn't a call (e.g. array literal `[...]`).
        Stack<(int OpenParenIndex, int ArgIndex)> stack = new();

        for (int i = 0; i < tokens.Count; i++)
        {
            TokenHit token = tokens[i];
            if (token.AbsoluteOffset >= cursorOffset) break;

            switch (token.Kind)
            {
                case SqlToken.LeftParen:
                case SqlToken.LeftBracket:
                    stack.Push((i, 0));
                    break;
                case SqlToken.RightParen:
                case SqlToken.RightBracket:
                    if (stack.Count > 0) stack.Pop();
                    break;
                case SqlToken.Comma:
                    if (stack.Count > 0)
                    {
                        (int openIdx, int argIdx) = stack.Pop();
                        stack.Push((openIdx, argIdx + 1));
                    }
                    break;
            }
        }

        if (stack.Count == 0) return (null, -1);
        (int openParenIndex, int currentArgIndex) = stack.Peek();
        // Only treat as a call when the bracket has a function-name token
        // directly preceding it. Array literals (`[1, 2, 3]`) and
        // grouping parens (`(a + b)`) are NOT calls.
        if (openParenIndex == 0) return (null, currentArgIndex);
        if (tokens[openParenIndex].Kind == SqlToken.LeftBracket) return (null, currentArgIndex);
        SqlToken prevKind = tokens[openParenIndex - 1].Kind;
        if (prevKind != SqlToken.Identifier && !IsKeywordToken(prevKind))
        {
            return (null, currentArgIndex);
        }
        return (tokens[openParenIndex - 1].Text, currentArgIndex);
    }

    /// <summary>
    /// Returns the deduplicated set of lambda parameter names visible at
    /// <paramref name="cursorOffset"/>, walking every enclosing lambda body
    /// (innermost first; outer scopes still contribute names that aren't
    /// shadowed by an inner). Used by the completion provider so a lambda
    /// body that has bound <c>t</c> or <c>x</c> suggests those names as
    /// completion items — without this, users see context functions but
    /// not the parameter that triggered the context in the first place.
    /// </summary>
    public static IReadOnlyList<string> GetActiveLambdaParameterNames(
        string sql, int cursorOffset)
    {
        List<TokenHit> tokens = Tokenize(sql);
        IReadOnlyList<LambdaScope> active = FindActiveScopes(tokens, cursorOffset);
        if (active.Count == 0) return Array.Empty<string>();
        // Walk innermost-first so an inner shadow of `t` keeps the inner's
        // entry on top — even though we dedupe by name, this ordering
        // determines first-wins for any downstream consumer that cares.
        HashSet<string> seen = new(StringComparer.Ordinal);
        List<string> names = new();
        for (int i = active.Count - 1; i >= 0; i--)
        {
            foreach (string name in active[i].Parameters)
            {
                if (seen.Add(name)) names.Add(name);
            }
        }
        return names;
    }

    /// <summary>
    /// Walks the manifest to find the function call's parameter at the
    /// supplied index and returns its
    /// <see cref="ParameterSignature.LambdaContextName"/>, preferring an
    /// <see cref="FunctionSignature.AdditionalParameterShapes"/> variant
    /// whose slot at that index IS a lambda (matters for multi-variant
    /// signatures like <c>draw_particles</c> where the lambda sprite
    /// variant only appears in the additional shapes).
    /// </summary>
    private static string? ResolveLambdaContextName(
        LanguageServerManifest manifest, string callName, int argIndex)
    {
        foreach (FunctionSignature fn in manifest.Functions)
        {
            if (!string.Equals(fn.Name, callName, System.StringComparison.OrdinalIgnoreCase)) continue;
            if (argIndex < fn.Parameters.Count
                && fn.Parameters[argIndex].LambdaContextName is { } primary)
            {
                return primary;
            }
            if (fn.AdditionalParameterShapes is not null)
            {
                foreach (IReadOnlyList<ParameterSignature> variant in fn.AdditionalParameterShapes)
                {
                    if (argIndex < variant.Count
                        && variant[argIndex].LambdaContextName is { } alt)
                    {
                        return alt;
                    }
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Tokenises <paramref name="sql"/> into <see cref="TokenHit"/> records.
    /// Shared with <c>HoverProvider</c> so both providers walk the same
    /// token stream; lifted to the walker here because the
    /// <c>TryFindCurrentLambdaContextName</c> entry point needs it
    /// internally and HoverProvider's private copy isn't visible from
    /// CompletionProvider.
    /// </summary>
    internal static List<TokenHit> Tokenize(string sql)
    {
        List<TokenHit> result = new();
        try
        {
            TokenList<SqlToken> tokens = SqlTokenizer.Instance.Tokenize(sql);
            foreach (Token<SqlToken> token in tokens)
            {
                string text = token.ToStringValue();
                int line = token.Position.Line - 1;
                int column = token.Position.Column - 1;
                int absolute = token.Position.Absolute;
                result.Add(new TokenHit(token.Kind, text, line, column, absolute));
            }
        }
        catch
        {
            // Partial tokenization is acceptable — same posture as HoverProvider's
            // approach. Empty or partial token lists still let downstream rules fire.
        }
        return result;
    }

    /// <summary>
    /// Finds every lambda scope that's active at <paramref name="cursorOffset"/>,
    /// innermost last. Each scope carries the parameter name(s), the outer
    /// function call name (if any), and the argument position the lambda fills.
    /// Returns an empty list if the cursor isn't inside any lambda body.
    /// </summary>
    public static IReadOnlyList<LambdaScope> FindActiveScopes(
        IReadOnlyList<TokenHit> tokens, int cursorOffset)
    {
        List<LambdaScope> active = new();
        // Per-bracket-depth state: when did the current argument index for the
        // call at this depth start? (Used to record argIndex on `->`.)
        // We also remember the function name and the start parenDepth when
        // each call opened — both are recovered on demand by looking back
        // from the OpenParen token's index.
        int depth = 0;
        // Track argument index per open call (stack indexed by depth).
        // Comma at depth d advances callArgIndex[d - 1] (the call whose '('
        // opened at depth d - 1).
        Stack<int> argIndexStack = new();

        for (int i = 0; i < tokens.Count; i++)
        {
            TokenHit token = tokens[i];

            // Cursor reached — snapshot active scopes and return. The check
            // uses < so a cursor positioned exactly at a token's start still
            // counts the previous scope as active (e.g. just inside the
            // arrow); a token under the cursor must finish processing first
            // for accurate parenDepth at that exact point.
            if (token.AbsoluteOffset >= cursorOffset)
            {
                PurgeClosedScopes(active, depth, justSawComma: false);
                return active;
            }

            switch (token.Kind)
            {
                case SqlToken.LeftParen:
                case SqlToken.LeftBracket:
                    depth++;
                    argIndexStack.Push(0);
                    break;
                case SqlToken.RightParen:
                case SqlToken.RightBracket:
                    if (argIndexStack.Count > 0) argIndexStack.Pop();
                    depth--;
                    PurgeClosedScopes(active, depth, justSawComma: false);
                    break;
                case SqlToken.Comma:
                    // Comma at the current depth advances the enclosing call's
                    // argument index AND ends any lambda whose body was at
                    // exactly this depth.
                    if (argIndexStack.Count > 0)
                    {
                        int top = argIndexStack.Pop();
                        argIndexStack.Push(top + 1);
                    }
                    PurgeClosedScopes(active, depth, justSawComma: true);
                    break;
                case SqlToken.Arrow:
                    LambdaScope? scope = TryDeclareLambdaScope(tokens, i, depth, argIndexStack);
                    if (scope is not null) active.Add(scope);
                    break;
            }
        }

        // Cursor past end of tokens — return whatever's still open.
        return active;
    }

    /// <summary>
    /// Reads the tokens immediately before <c>-&gt;</c> at index
    /// <paramref name="arrowIndex"/> to extract the lambda's parameter
    /// names, and walks back from the most recent unclosed
    /// <c>(</c> to discover the outer call's name and argument index.
    /// Returns <see langword="null"/> if the parameter list is malformed.
    /// </summary>
    private static LambdaScope? TryDeclareLambdaScope(
        IReadOnlyList<TokenHit> tokens,
        int arrowIndex,
        int currentDepth,
        Stack<int> argIndexStack)
    {
        // Two parameter shapes: `x -> ...` (single identifier) and
        // `(a, b) -> ...` (paren-wrapped list).
        if (arrowIndex == 0) return null;
        List<string> parameters = new();
        if (tokens[arrowIndex - 1].Kind == SqlToken.RightParen)
        {
            // Walk back through `(a, b)` until we find the matching LeftParen.
            int depth = 1;
            int k = arrowIndex - 2;
            while (k >= 0 && depth > 0)
            {
                if (tokens[k].Kind == SqlToken.RightParen) depth++;
                else if (tokens[k].Kind == SqlToken.LeftParen) depth--;
                else if (depth == 1 && tokens[k].Kind == SqlToken.Identifier)
                {
                    parameters.Add(tokens[k].Text);
                }
                k--;
            }
            // Identifiers were collected in reverse — fix the order.
            parameters.Reverse();
        }
        else if (tokens[arrowIndex - 1].Kind == SqlToken.Identifier)
        {
            parameters.Add(tokens[arrowIndex - 1].Text);
        }
        else
        {
            return null;
        }

        // Resolve the outer call name + arg index by scanning back from the
        // most recent unclosed `(` at depth == currentDepth (i.e., the one
        // that opened this argument list). The function-call name is the
        // identifier (or keyword-as-function) immediately before that paren.
        (string? outerName, int outerArgIndex) = FindOuterCall(tokens, arrowIndex, currentDepth, argIndexStack);

        // The lambda body is now active. It will close on either:
        //   - a comma at currentDepth (next arg of the enclosing call), OR
        //   - a closing bracket that pops below currentDepth.
        return new LambdaScope(
            Parameters: parameters,
            BodyMinDepth: currentDepth,
            OuterCallName: outerName,
            OuterArgIndex: outerArgIndex);
    }

    /// <summary>
    /// Locates the enclosing function call's name and the lambda's argument
    /// position by scanning the token stream backward from the arrow. The
    /// outer call is the immediately-enclosing <c>(</c> at parenthesis
    /// depth <c>currentDepth - 1</c>; its name is the token just before that
    /// open-paren. Returns <c>(null, -1)</c> for lambdas not nested inside
    /// a function call (top-level, array-literal, etc.).
    /// </summary>
    private static (string? OuterName, int OuterArgIndex) FindOuterCall(
        IReadOnlyList<TokenHit> tokens, int arrowIndex, int currentDepth, Stack<int> argIndexStack)
    {
        if (currentDepth == 0)
        {
            return (null, -1);
        }
        // Argument index at the enclosing call — top of stack is currentDepth's
        // bucket. Stack reversed traversal isn't needed; we just want the top.
        int argIndex = argIndexStack.Count > 0 ? argIndexStack.Peek() : -1;

        // Scan backward for the LeftParen at exactly depth == currentDepth - 1
        // when first seen, then take the token before it. We walk depth ourselves
        // because the token stream's depth at the arrow is currentDepth.
        int depth = currentDepth;
        for (int k = arrowIndex - 1; k >= 0; k--)
        {
            SqlToken kind = tokens[k].Kind;
            if (kind == SqlToken.RightParen || kind == SqlToken.RightBracket)
            {
                depth++;
                continue;
            }
            if (kind == SqlToken.LeftParen || kind == SqlToken.LeftBracket)
            {
                depth--;
                if (depth == currentDepth - 1)
                {
                    // The function name is the token at k - 1 if it's an
                    // identifier or keyword-as-function. Array-literal `[` has
                    // no name to its left, return (null, argIndex) so the
                    // caller knows it isn't a function-call slot.
                    if (kind == SqlToken.LeftBracket || k == 0) return (null, argIndex);
                    SqlToken prevKind = tokens[k - 1].Kind;
                    if (prevKind == SqlToken.Identifier || IsKeywordToken(prevKind))
                    {
                        return (tokens[k - 1].Text, argIndex);
                    }
                    return (null, argIndex);
                }
            }
        }
        return (null, argIndex);
    }

    /// <summary>
    /// Pops scopes that have closed at the current bracket depth. A lambda
    /// closes when bracket depth drops below where it was declared, OR when
    /// a comma at exactly its declaration depth begins the next argument
    /// (only meaningful when <paramref name="justSawComma"/> is true).
    /// </summary>
    private static void PurgeClosedScopes(List<LambdaScope> active, int depth, bool justSawComma)
    {
        for (int i = active.Count - 1; i >= 0; i--)
        {
            LambdaScope scope = active[i];
            bool closedByBracket = depth < scope.BodyMinDepth;
            bool closedByComma = justSawComma && depth == scope.BodyMinDepth;
            if (closedByBracket || closedByComma)
            {
                active.RemoveAt(i);
            }
        }
    }

    private static bool IsKeywordToken(SqlToken kind) => kind < SqlToken.Identifier;
}

/// <summary>
/// One active lambda scope at a particular cursor position. A list of
/// these (innermost last) describes the chain of nested lambdas the
/// cursor sits inside.
/// </summary>
/// <param name="Parameters">Lambda parameter names in declaration order.</param>
/// <param name="BodyMinDepth">Bracket depth the body opened at — used internally to determine when the scope closes; consumers ignore.</param>
/// <param name="OuterCallName">Name of the enclosing function call, when the lambda is one of its arguments. <see langword="null"/> when the lambda isn't an argument (top-level, array literal, …).</param>
/// <param name="OuterArgIndex">Zero-based index of the lambda within the outer call's argument list. <c>-1</c> when <see cref="OuterCallName"/> is <see langword="null"/>.</param>
internal sealed record LambdaScope(
    IReadOnlyList<string> Parameters,
    int BodyMinDepth,
    string? OuterCallName,
    int OuterArgIndex);
