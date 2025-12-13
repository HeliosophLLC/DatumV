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
/// </remarks>
public sealed class VariableScope
{
    private readonly Stack<Dictionary<string, DataValue>> _frames = new();

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
    /// the lifetime of its block).
    /// </summary>
    public void Declare(string name, DataValue value)
    {
        Dictionary<string, DataValue> top = _frames.Peek();
        if (top.ContainsKey(name))
        {
            throw new InvalidOperationException(
                $"Variable '@{name}' is already declared in the current block.");
        }
        top[name] = value;
    }

    /// <summary>
    /// Mutates an existing binding. Walks frames from innermost outward,
    /// updates the first frame that contains the name. Throws if the
    /// name is not bound anywhere in the chain.
    /// </summary>
    public void Set(string name, DataValue value)
    {
        foreach (Dictionary<string, DataValue> frame in _frames)
        {
            if (frame.ContainsKey(name))
            {
                frame[name] = value;
                return;
            }
        }
        throw new InvalidOperationException(
            $"Variable '@{name}' is not declared in any enclosing scope.");
    }

    /// <summary>
    /// Looks up a binding. Walks frames innermost-first; the first
    /// matching frame wins (so inner declarations shadow outer ones).
    /// Returns <see langword="false"/> when no matching binding exists.
    /// </summary>
    public bool TryGet(string name, out DataValue value)
    {
        foreach (Dictionary<string, DataValue> frame in _frames)
        {
            if (frame.TryGetValue(name, out value!))
            {
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

    private static Dictionary<string, DataValue> NewFrame()
        => new(StringComparer.OrdinalIgnoreCase);
}
