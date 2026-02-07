using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Photino.NET;

namespace DatumIngest.Web.Photino;

// Process-level coordinator for cross-window dialog flow. One instance per
// app, shared by every Photino window's HostBridge. See plans/dialog-ipc.md
// for the full design.
//
// Responsibilities:
//   1. Spawn a new PhotinoWindow when host:dialog.open arrives from any
//      window. The new window loads {spaBaseUrl}#/dialog/{kind}?... so the
//      same SPA bundle serves dialog content via a hash-routed second
//      mount root (see main.tsx).
//   2. Route host:dialog.resolve from a dialog window back to its
//      originator via SendWebMessage on the originator window.
//   3. Detect WindowClosing on a dialog without prior resolve and
//      synthesise a null result so the originator's Promise doesn't hang.
//
// Threading: every entry point here runs on the Photino UI thread (the
// WebMessageReceived callback and WindowClosing event both dispatch on
// the STA main thread). Internal state is guarded by a lock anyway —
// future async paths shouldn't have to discover the threading
// assumption.
public sealed class DialogCoordinator
{
    private readonly string _spaBaseUrl;
    private readonly bool _devMode;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, DialogEntry> _entries = new();

    public DialogCoordinator(string spaBaseUrl, bool devMode)
    {
        _spaBaseUrl = spaBaseUrl.TrimEnd('/');
        _devMode = devMode;
    }

    private sealed class DialogEntry
    {
        public required Guid RequestId { get; init; }
        public required PhotinoWindow Originator { get; init; }
        public PhotinoWindow? DialogWindow { get; set; }
        public bool Resolved { get; set; }
    }

    // host:dialog.open handler — invoked by any HostBridge with a
    // coordinator reference.
    //
    // Payload shape (JSON):
    //   { requestId: string (UUID), kind: string, payload: object|null, modal: bool }
    public void HandleOpen(PhotinoWindow originator, string payloadJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payloadJson);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[DialogCoordinator] invalid open payload: {ex.Message}");
            return;
        }
        if (node is null) return;

        string? requestIdString = node["requestId"]?.GetValue<string>();
        string? kind = node["kind"]?.GetValue<string>();
        JsonNode? innerPayload = node["payload"];
        bool modal = node["modal"]?.GetValue<bool>() ?? true;

        if (string.IsNullOrWhiteSpace(requestIdString) || string.IsNullOrWhiteSpace(kind))
        {
            Console.Error.WriteLine("[DialogCoordinator] open payload missing requestId or kind.");
            return;
        }
        if (!Guid.TryParse(requestIdString, out Guid requestId))
        {
            Console.Error.WriteLine($"[DialogCoordinator] requestId is not a valid GUID: '{requestIdString}'.");
            return;
        }

        DialogEntry entry = new()
        {
            RequestId = requestId,
            Originator = originator,
        };
        lock (_lock) _entries[requestId] = entry;

        string url = BuildDialogUrl(kind, requestId, innerPayload);
        PhotinoWindow dialog = SpawnDialogWindow(originator, url, kind, modal, requestId);
        entry.DialogWindow = dialog;
    }

    // host:dialog.resolve handler — invoked by a dialog window's HostBridge.
    // Payload is the result JSON; we forward it verbatim to the originator.
    public void HandleResolve(Guid requestId, string? resultJson)
    {
        DispatchResolution(requestId, resultJson);
    }

    // host:dialog.close handler — explicit dismiss from the dialog SPA
    // (e.g., user clicked an "X" inside the content). Routes as a null
    // result.
    public void HandleClose(Guid requestId)
    {
        DispatchResolution(requestId, resultJson: null);
    }

    private void DispatchResolution(Guid requestId, string? resultJson)
    {
        DialogEntry? entry;
        lock (_lock)
        {
            if (!_entries.TryGetValue(requestId, out entry)) return;
            if (entry.Resolved) return;
            entry.Resolved = true;
            _entries.Remove(requestId);
        }

        JsonNode? resultNode = null;
        if (!string.IsNullOrEmpty(resultJson))
        {
            try { resultNode = JsonNode.Parse(resultJson); }
            catch (JsonException) { resultNode = null; }
        }

        JsonObject envelope = new()
        {
            ["requestId"] = entry.RequestId.ToString(),
            ["result"] = resultNode,
        };
        string envelopeJson = envelope.ToJsonString();
        try
        {
            entry.Originator.SendWebMessage($"host:dialog.resolved|{envelopeJson}");
        }
        catch (Exception ex)
        {
            // Originator may have been closed. Nothing we can do.
            Console.Error.WriteLine($"[DialogCoordinator] failed to notify originator: {ex.Message}");
        }

        // Close the dialog window if still open. Wrap in try/catch — the
        // close might race with the WindowClosing event that triggered
        // this dispatch in the first place.
        try
        {
            entry.DialogWindow?.Close();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DialogCoordinator] failed to close dialog window: {ex.Message}");
        }
    }

    private string BuildDialogUrl(string kind, Guid requestId, JsonNode? payload)
    {
        // requestId always present; flatten one level of payload string
        // fields into the query so the dialog SPA can read them without
        // a second round-trip. Anything deeper than a flat string-keyed
        // object stays untouched — small dialog payloads are flat by
        // design.
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["requestId"] = requestId.ToString();
        if (payload is JsonObject obj)
        {
            foreach (KeyValuePair<string, JsonNode?> kv in obj)
            {
                if (kv.Value is JsonValue val && val.TryGetValue(out string? s))
                {
                    query[kv.Key] = s;
                }
                else if (kv.Value is not null)
                {
                    // Non-string scalar or nested object — stringify the JSON.
                    query[kv.Key] = kv.Value.ToJsonString();
                }
            }
        }
        return $"{_spaBaseUrl}/#/dialog/{Uri.EscapeDataString(kind)}?{query}";
    }

    private PhotinoWindow SpawnDialogWindow(
        PhotinoWindow originator,
        string url,
        string kind,
        bool modal,
        Guid requestId)
    {
        _ = originator; // reserved for future per-parent centring; see note
        _ = modal;      // reserved — Photino doesn't expose a modal flag in
                        // the version we ship today, so dialog modality is a
                        // soft convention: the dialog floats and the user
                        // can technically still click the main window. The
                        // parameter stays in the protocol so non-modal
                        // callers can pass false without API churn.

        // Centring on the originator would compute originator.Location +
        // (originator.Size - dialogSize)/2; PhotinoWindow exposes Location
        // and Size as properties on Windows. v1 uses Center() which targets
        // the screen — acceptable while we have one dialog at a time.
        PhotinoWindow window = new PhotinoWindow()
            .SetTitle($"DatumIngest — {kind}")
            .SetDevToolsEnabled(true)
            .SetContextMenuEnabled(true);

        if (OperatingSystem.IsWindows())
        {
            window = window
                .SetUseOsDefaultSize(false)
                .SetUseOsDefaultLocation(false)
                .SetSize(new System.Drawing.Size(720, 600))
                .SetLocation(new System.Drawing.Point(150, 150))
                .SetResizable(true)
                .Center()
                .SetChromeless(true);
        }

        // Subscribe to close-without-resolve BEFORE the WebView wires up,
        // so a fast dismiss still routes through DispatchResolution.
        window.WindowClosing += (_, _) =>
        {
            DispatchResolution(requestId, resultJson: null);
            return false; // allow the close
        };

        window = window.Load(url);

        // Attach a HostBridge so the dialog SPA can post dialog.resolve /
        // dialog.close back to us.
        window.WindowCreated += (_, _) =>
        {
            _ = new HostBridge(window, coordinator: this, dialogRequestId: requestId);
        };

        return window;
    }
}
