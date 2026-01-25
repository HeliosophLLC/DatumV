using Photino.NET;

namespace DatumIngest.Client;

// Central JS↔C# IPC dispatcher for the Photino host. Mirrors the JS-side
// HostBridge in src/host/index.ts. Messages are namespaced strings
// ("host:window.minimize", "host:dialog.open", etc.) so future modules
// don't collide. Today wires window controls; dialogs, file pickers,
// native menus, and Lua applet bridges register through On(...).
internal sealed class HostBridge
{
    private readonly PhotinoWindow _window;
    private readonly Dictionary<string, Action> _handlers = new();

    public HostBridge(PhotinoWindow window)
    {
        _window = window;
        _window.RegisterWebMessageReceivedHandler((_, message) => Dispatch(message));
        WireWindowControls();
    }

    // Register a JS→C# message handler. Last registration wins for a kind.
    public HostBridge On(string kind, Action handler)
    {
        _handlers[kind] = handler;
        return this;
    }

    // Push a message C#→JS.
    public void Send(string message)
    {
        Console.WriteLine($"[Bridge] → {message}");
        _window.SendWebMessage(message);
    }

    private void Dispatch(string message)
    {
        Console.WriteLine($"[Bridge] ← {message}");
        if (_handlers.TryGetValue(message, out var handler))
        {
            handler();
        }
        else
        {
            Console.WriteLine($"[Bridge] no handler for '{message}'");
        }
    }

    // Window controls module. JS↔C# pairs:
    //   ←  host:window.minimize          / .toggleMaximize / .close
    //   →  host:window.maximized         / .normal     (state echo)
    private void WireWindowControls()
    {
        On("host:window.minimize", () => _window.Minimized = true);
        On("host:window.toggleMaximize", () => _window.Maximized = !_window.Maximized);
        On("host:window.close", () => _window.Close());

        _window.WindowMaximized += (_, _) => PushWindowState();
        _window.WindowRestored += (_, _) => PushWindowState();
    }

    // Photino fires WindowRestored for both un-maximize and un-minimize —
    // we snapshot the current Maximized property to disambiguate.
    private void PushWindowState()
    {
        Send(_window.Maximized ? "host:window.maximized" : "host:window.normal");
    }
}
