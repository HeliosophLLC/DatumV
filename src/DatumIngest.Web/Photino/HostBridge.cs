using System.Runtime.InteropServices;
using Photino.NET;

namespace DatumIngest.Web.Photino;

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
    //   ←  host:window.drag              (start OS drag from current mouse position)
    //   ←  host:window.resize.<side>     (start OS resize from the given edge/corner)
    //   →  host:window.maximized         / .normal     (state echo)
    //
    // Drag + resize are Windows-native today. Mac (NSWindow.performDrag) and
    // Linux (X11 _NET_WM_MOVERESIZE / Wayland xdg_toplevel.move) need either
    // Cocoa/X11 P/Invoke or a manual-position-tracking fallback; flagged TODO.
    private void WireWindowControls()
    {
        On("host:window.minimize", () => _window.Minimized = true);
        On("host:window.toggleMaximize", () => _window.Maximized = !_window.Maximized);
        On("host:window.close", () => _window.Close());

        On("host:window.drag", () => StartNativeMove(HTCAPTION));
        On("host:window.resize.top", () => StartNativeMove(HTTOP));
        On("host:window.resize.right", () => StartNativeMove(HTRIGHT));
        On("host:window.resize.bottom", () => StartNativeMove(HTBOTTOM));
        On("host:window.resize.left", () => StartNativeMove(HTLEFT));
        On("host:window.resize.top-left", () => StartNativeMove(HTTOPLEFT));
        On("host:window.resize.top-right", () => StartNativeMove(HTTOPRIGHT));
        On("host:window.resize.bottom-left", () => StartNativeMove(HTBOTTOMLEFT));
        On("host:window.resize.bottom-right", () => StartNativeMove(HTBOTTOMRIGHT));

        _window.WindowMaximized += (_, _) => PushWindowState();
        _window.WindowRestored += (_, _) => PushWindowState();
    }

    // Photino fires WindowRestored for both un-maximize and un-minimize —
    // we snapshot the current Maximized property to disambiguate.
    private void PushWindowState()
    {
        Send(_window.Maximized ? "host:window.maximized" : "host:window.normal");
    }

    // The standard "hand the drag/resize back to the OS" technique on Windows:
    // ReleaseCapture() so WebView2 drops mouse capture, then SendMessage with
    // WM_NCLBUTTONDOWN + a hit-test value (HTCAPTION for drag, HT* edges/corners
    // for resize). The OS then runs its normal move/resize loop with snap-to-edge,
    // Aero Snap, Win+arrow handling, etc.
    private void StartNativeMove(int hitTest)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine($"[Bridge] native drag/resize not implemented on this OS (hitTest={hitTest})");
            return;
        }
        var hwnd = _window.WindowHandle;
        if (hwnd == IntPtr.Zero)
        {
            Console.Error.WriteLine("[Bridge] WindowHandle is null; cannot start native move");
            return;
        }
        ReleaseCapture();
        SendMessage(hwnd, WM_NCLBUTTONDOWN, hitTest, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, nint wParam, IntPtr lParam);

    private const uint WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;
}
