using System.Runtime.InteropServices;
using Photino.NET;

namespace DatumIngest.Web.Photino;

// Central JS↔C# IPC dispatcher for a single Photino window. Mirrors the
// JS-side HostBridge in src/host/index.ts. Messages are namespaced strings
// ("host:window.minimize", "host:dialog.open", etc.) so future modules
// don't collide. Today wires window controls + the dialog protocol;
// file pickers, native menus, and Lua applet bridges register via On(...).
//
// Message format:
//   - Bare kind: just the kind string ("host:window.minimize"). No payload.
//   - With payload: "{kind}|{JSON}" — split on the first '|'. The JSON is
//     opaque at this layer; consumers parse it themselves.
//
// Dialog wiring: when a DialogCoordinator is passed, the bridge auto-
// registers `host:dialog.open` (any window can initiate) and — for
// windows that ARE dialogs (constructed with a non-null dialogRequestId)
// — `host:dialog.resolve` / `host:dialog.close`. See DialogCoordinator
// for the cross-window routing logic.
internal sealed class HostBridge
{
    private readonly PhotinoWindow _window;
    private readonly DialogCoordinator? _coordinator;
    private readonly Guid? _dialogRequestId;
    private readonly Dictionary<string, Action<string?>> _handlers = new();
    // Last maximized state we pushed to JS. Photino fires WindowRestored
    // on every pixel of a drag-resize (not only on the maximize→normal
    // transition the name suggests), so without dedup we send a flood of
    // identical "host:window.normal" messages. SendWebMessage runs across
    // the WebView2 IPC channel; a flood can crash WebView2 with
    // STATUS_FATAL_USER_CALLBACK_EXCEPTION (0xc000041d). Tracking the last
    // value sent lets PushWindowState skip the redundant ones.
    private bool? _lastSentMaximized;

    public HostBridge(
        PhotinoWindow window,
        DialogCoordinator? coordinator = null,
        Guid? dialogRequestId = null)
    {
        _window = window;
        _coordinator = coordinator;
        _dialogRequestId = dialogRequestId;
        _window.RegisterWebMessageReceivedHandler((_, message) => Dispatch(message));
        WireWindowControls();
        WireDialogProtocol();
    }

    /// <summary>The window this bridge is attached to.</summary>
    public PhotinoWindow Window => _window;

    // Register a JS→C# message handler that takes no payload.
    public HostBridge On(string kind, Action handler)
        => On(kind, _ => handler());

    // Register a JS→C# message handler that may receive a payload string
    // (null when the message arrived without one).
    public HostBridge On(string kind, Action<string?> handler)
    {
        _handlers[kind] = handler;
        return this;
    }

    // Push a bare kind to JS.
    public void Send(string message)
    {
        Console.WriteLine($"[Bridge] → {message}");
        _window.SendWebMessage(message);
    }

    // Push a kind + JSON payload to JS as "{kind}|{json}".
    public void Send(string kind, string payloadJson)
    {
        string composed = $"{kind}|{payloadJson}";
        Console.WriteLine($"[Bridge] → {kind}|<payload {payloadJson.Length}B>");
        _window.SendWebMessage(composed);
    }

    private void Dispatch(string message)
    {
        int pipe = message.IndexOf('|');
        string kind = pipe < 0 ? message : message[..pipe];
        string? payload = pipe < 0 ? null : message[(pipe + 1)..];

        if (payload is null)
        {
            Console.WriteLine($"[Bridge] ← {kind}");
        }
        else
        {
            Console.WriteLine($"[Bridge] ← {kind}|<payload {payload.Length}B>");
        }

        if (_handlers.TryGetValue(kind, out var handler))
        {
            handler(payload);
        }
        else
        {
            Console.WriteLine($"[Bridge] no handler for '{kind}'");
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

    // Dialog protocol: open is initiable from any window; resolve / close
    // only mean something from a window that IS a dialog (carries a
    // requestId). See DialogCoordinator for routing.
    private void WireDialogProtocol()
    {
        if (_coordinator is null) return;

        On("host:dialog.open", payload =>
        {
            if (payload is null)
            {
                Console.Error.WriteLine("[Bridge] host:dialog.open without payload — dropping.");
                return;
            }
            _coordinator.HandleOpen(_window, payload);
        });

        if (_dialogRequestId is { } reqId)
        {
            On("host:dialog.resolve", payload =>
            {
                _coordinator.HandleResolve(reqId, payload);
            });
            On("host:dialog.close", () => _coordinator.HandleClose(reqId));
        }
    }

    // Photino fires WindowRestored for both un-maximize and un-minimize —
    // we snapshot the current Maximized property to disambiguate. Also
    // dedupes against the last-sent value to absorb the per-pixel
    // WindowRestored flood during drag-resize, which would otherwise
    // overwhelm WebView2's IPC channel and crash the host process.
    private void PushWindowState()
    {
        bool current = _window.Maximized;
        if (_lastSentMaximized == current) return;
        _lastSentMaximized = current;
        Send(current ? "host:window.maximized" : "host:window.normal");
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
