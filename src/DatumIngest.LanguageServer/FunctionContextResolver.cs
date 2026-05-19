namespace Heliosoph.DatumV.LanguageServer;

using Heliosoph.DatumV.Manifest;

/// <summary>
/// Computes the effective function whitelist for a lambda body scoped to a
/// named <see cref="FunctionContextEntry"/>. The completion provider calls
/// this when the cursor sits inside a lambda parameter slot whose
/// <see cref="ParameterSignature.LambdaContextName"/> is non-null, so the
/// suggested-function set is restricted to what's actually callable inside
/// the body.
/// </summary>
/// <remarks>
/// <para>
/// The effective whitelist is the union of three groups, computed by
/// walking the context's ancestor chain:
/// </para>
/// <list type="bullet">
///   <item>
///     <strong>Function-tagged members</strong> — every function whose
///     <see cref="FunctionSignature.Contexts"/> list contains the
///     context's name (or any ancestor's name).
///   </item>
///   <item>
///     <strong>Borrows</strong> — every function name explicitly listed
///     in any ancestor context's
///     <see cref="FunctionContextEntry.Borrows"/>.
///   </item>
///   <item>
///     <strong>Globally-visible functions</strong> — every function
///     whose <see cref="FunctionSignature.Contexts"/> list is empty or
///     <see langword="null"/>. Globals are visible inside any context, so
///     this group is the same regardless of the requested context name.
///   </item>
/// </list>
/// <para>
/// The resolver mirrors
/// <c>FunctionRegistry.IsVisibleInContext</c> at edit time. Both walk the
/// same ancestor chain in the same order; the difference is the data
/// source (manifest entries here, runtime descriptors there) and the
/// return shape (set of names here, single yes/no there).
/// </para>
/// </remarks>
public static class FunctionContextResolver
{
    /// <summary>
    /// Returns the set of fully-qualified-by-name functions visible inside
    /// a lambda body scoped to <paramref name="contextName"/>. Names are
    /// the function's bare identifier; the LS pairs them with their schema
    /// when rendering completion items.
    /// </summary>
    /// <param name="contextName">Name of the enclosing lambda's context.</param>
    /// <param name="manifest">Source of context definitions + function signatures.</param>
    public static HashSet<string> EffectiveWhitelist(
        string contextName, LanguageServerManifest manifest)
    {
        HashSet<string> result = new(StringComparer.Ordinal);

        if (manifest.FunctionContexts is null)
        {
            // No contexts in the manifest — fall back to "everything globally
            // visible". This matches the legacy posture before contexts existed.
            AddGlobalFunctions(result, manifest);
            return result;
        }

        // Build the set of context names (the target + every ancestor).
        HashSet<string> contextChain = new(StringComparer.Ordinal);
        WalkContextAncestors(contextName, manifest, contextChain);

        if (contextChain.Count == 0)
        {
            // Unknown context — surface global functions only. Consumers
            // that wanted strictness can check the chain themselves.
            AddGlobalFunctions(result, manifest);
            return result;
        }

        // Globally-visible functions are always callable.
        AddGlobalFunctions(result, manifest);

        // Function-tagged: any function whose Contexts intersects the chain.
        foreach (FunctionSignature fn in manifest.Functions)
        {
            if (fn.Contexts is null or { Count: 0 })
            {
                continue;
            }
            foreach (string ctx in fn.Contexts)
            {
                if (contextChain.Contains(ctx))
                {
                    result.Add(fn.Name);
                    break;
                }
            }
        }

        // Borrows: every ancestor's borrow list extends the whitelist.
        foreach (FunctionContextEntry entry in manifest.FunctionContexts)
        {
            if (!contextChain.Contains(entry.Name))
            {
                continue;
            }
            foreach (string borrowed in entry.Borrows)
            {
                result.Add(borrowed);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns the canonical parameter list for a context. Used by the
    /// completion provider to pre-fill <c>t -&gt; </c>-style suggestions
    /// when the user starts typing a lambda inside a parameter slot.
    /// </summary>
    public static IReadOnlyList<LambdaParameterEntry>? GetCanonicalParameters(
        string contextName, LanguageServerManifest manifest)
    {
        if (manifest.FunctionContexts is null)
        {
            return null;
        }
        foreach (FunctionContextEntry entry in manifest.FunctionContexts)
        {
            if (StringComparer.Ordinal.Equals(entry.Name, contextName))
            {
                return entry.Parameters;
            }
        }
        return null;
    }

    private static void WalkContextAncestors(
        string contextName, LanguageServerManifest manifest, HashSet<string> visited)
    {
        if (manifest.FunctionContexts is null) return;
        string? current = contextName;
        while (current is not null && visited.Add(current))
        {
            FunctionContextEntry? entry = FindContext(current, manifest);
            if (entry is null)
            {
                visited.Remove(current);  // walked into an unknown — undo and stop
                return;
            }
            current = entry.ParentName;
        }
    }

    private static FunctionContextEntry? FindContext(
        string contextName, LanguageServerManifest manifest)
    {
        if (manifest.FunctionContexts is null) return null;
        foreach (FunctionContextEntry entry in manifest.FunctionContexts)
        {
            if (StringComparer.Ordinal.Equals(entry.Name, contextName))
            {
                return entry;
            }
        }
        return null;
    }

    private static void AddGlobalFunctions(HashSet<string> result, LanguageServerManifest manifest)
    {
        foreach (FunctionSignature fn in manifest.Functions)
        {
            if (fn.Contexts is null or { Count: 0 })
            {
                result.Add(fn.Name);
            }
        }
    }
}
