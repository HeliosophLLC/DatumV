using DatumIngest.Model;

namespace DatumIngest.Execution;

/// <summary>
/// Block-scoped visibility for procedural variables. Internally a stack
/// of frames; each <c>BEGIN</c> pushes a new frame, each <c>END</c> pops
/// the topmost. <c>DECLARE</c> binds in the topmost frame; <c>SET</c>
/// walks outward to find the frame holding the named binding; lookups
/// resolve from inside out so an inner declaration shadows an outer one.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Scope vs storage.</strong> This class only tracks visibility
/// — name → <see cref="DataValue"/>. Byte payloads (strings, byte arrays,
/// nested structs/arrays) live in the <see cref="BatchContext.VariableStore"/>
/// arena; this scope holds DataValues whose offsets point into that
/// arena. The producer (the procedural executor binding the variable)
/// is responsible for stabilising the payload into <c>VariableStore</c>
/// before calling <see cref="Declare"/> or <see cref="Set"/>.
/// </para>
/// <para>
/// <strong>Case-insensitive names.</strong> <c>@X</c> and <c>@x</c>
/// resolve to the same binding, matching how the codebase handles UDF
/// names and SQL identifiers generally.
/// </para>
/// <para>
/// <strong>Optional struct field names.</strong> Bindings whose value is
/// a <see cref="DataKind.Struct"/> can carry an ordered list of field
/// names alongside the value. Used by <c>FOR @row IN (...)</c> so
/// <c>@row['column']</c> can resolve a named field at evaluation time
/// without per-row schema lookup. Non-struct bindings leave field
/// names <see langword="null"/>. A future per-query type registry
/// (planned but not yet shipped) will replace this localised tracking
/// with a self-describing <see cref="DataValue"/> carrying a type id;
/// the <c>TryGetFieldNames</c> entry point keeps the consumer-side
/// surface stable through that migration.
/// </para>
/// </remarks>
public sealed class VariableScope
{
    private readonly Stack<Dictionary<string, Binding>> _frames = new();

    /// <summary>
    /// Internal per-binding shape: the bound <see cref="DataValue"/>
    /// plus optional struct field names. Keeping these together avoids
    /// a parallel dictionary and keeps lookup atomic.
    /// </summary>
    private readonly record struct Binding(
        DataValue Value,
        IReadOnlyList<string>? StructFieldNames);

    /// <summary>
    /// Creates a scope with a single root frame. The root corresponds to
    /// the procedure's outermost lexical scope — variables declared at
    /// the procedure's top level live here. Sub-blocks push additional
    /// frames on top.
    /// </summary>
    public VariableScope()
    {
        _frames.Push(NewFrame());
    }

    /// <summary>
    /// Number of frames currently on the stack. Always at least 1 (the
    /// root). Useful for diagnostics; the executor doesn't need this in
    /// the hot path.
    /// </summary>
    public int FrameCount => _frames.Count;

    /// <summary>
    /// Pushes a new frame, opening a fresh inner scope. Subsequent
    /// <see cref="Declare"/> calls bind in this frame; the matching
    /// <see cref="PopFrame"/> drops every binding it contains.
    /// </summary>
    public void PushFrame() => _frames.Push(NewFrame());

    /// <summary>
    /// Pops the topmost frame. Throws if the root frame is the only one
    /// left — the executor must never pop more than it pushed.
    /// </summary>
    public void PopFrame()
    {
        if (_frames.Count <= 1)
        {
            throw new InvalidOperationException(
                "Cannot pop the root variable scope frame; check that BEGIN/END nesting is balanced.");
        }
        _frames.Pop();
    }

    /// <summary>
    /// Adds a new binding in the topmost frame. Throws if the name is
    /// already declared in the same frame; shadowing across frames is
    /// allowed (an inner <c>DECLARE @x</c> can hide an outer one for
    /// the lifetime of its block). When <paramref name="structFieldNames"/>
    /// is non-<see langword="null"/>, the names attach to this binding so
    /// downstream <c>@var['field']</c> access can resolve a position.
    /// </summary>
    public void Declare(
        string name,
        DataValue value,
        IReadOnlyList<string>? structFieldNames = null)
    {
        Dictionary<string, Binding> top = _frames.Peek();
        if (top.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Variable '@{name}' is already declared in the current block.");
        }
        top[name] = new Binding(value, structFieldNames);
    }

    /// <summary>
    /// Mutates an existing binding. Walks frames from innermost outward,
    /// updates the first frame that contains the name. Throws if the
    /// name is not bound anywhere in the chain. Preserves the existing
    /// struct field names for the binding (SET re-assigns the value but
    /// not the binding's structural identity).
    /// </summary>
    public void Set(string name, DataValue value)
    {
        foreach (Dictionary<string, Binding> frame in _frames)
        {
            if (frame.TryGetValue(name, out Binding existing))
            {
                frame[name] = new Binding(value, existing.StructFieldNames);
                return;
            }
        }
        throw new InvalidOperationException(
            $"Variable '@{name}' is not declared in any enclosing scope.");
    }

    /// <summary>
    /// Looks up a binding's value. Walks frames innermost-first; the
    /// first matching frame wins (so inner declarations shadow outer
    /// ones). Returns <see langword="false"/> when no matching binding
    /// exists.
    /// </summary>
    public bool TryGet(string name, out DataValue value)
    {
        foreach (Dictionary<string, Binding> frame in _frames)
        {
            if (frame.TryGetValue(name, out Binding b))
            {
                value = b.Value;
                return true;
            }
        }
        value = default;
        return false;
    }

    /// <summary>
    /// Looks up a binding. Throws if the name is not bound — use
    /// <see cref="TryGet"/> when the absence of a binding is a valid
    /// outcome.
    /// </summary>
    public DataValue Get(string name)
    {
        if (TryGet(name, out DataValue value)) return value;
        throw new InvalidOperationException($"Variable '@{name}' is not declared.");
    }

    /// <summary>
    /// Looks up the struct field names attached to a binding. Returns
    /// <see langword="true"/> if the binding exists (regardless of
    /// whether field names are present); <paramref name="fieldNames"/>
    /// is <see langword="null"/> when the binding is a non-struct or
    /// was declared without a field-name list. Used by the expression
    /// evaluator to resolve <c>@row['column']</c> at evaluation time.
    /// </summary>
    public bool TryGetFieldNames(string name, out IReadOnlyList<string>? fieldNames)
    {
        foreach (Dictionary<string, Binding> frame in _frames)
        {
            if (frame.TryGetValue(name, out Binding b))
            {
                fieldNames = b.StructFieldNames;
                return true;
            }
        }
        fieldNames = null;
        return false;
    }

    /// <summary>
    /// Enumerates every visible binding across the scope chain, with
    /// inner-frame entries shadowing outer-frame entries with the same
    /// name. Yields each name at most once. Order is innermost-first.
    /// Useful for snapshotting state at batch end and for diagnostic
    /// printing.
    /// </summary>
    public IEnumerable<KeyValuePair<string, DataValue>> EnumerateVisible()
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Dictionary<string, Binding> frame in _frames)
        {
            foreach (KeyValuePair<string, Binding> entry in frame)
            {
                if (seen.Add(entry.Key))
                {
                    yield return new KeyValuePair<string, DataValue>(entry.Key, entry.Value.Value);
                }
            }
        }
    }

    private static Dictionary<string, Binding> NewFrame()
        => new(StringComparer.OrdinalIgnoreCase);
}
