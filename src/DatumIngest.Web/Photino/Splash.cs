namespace DatumIngest.Web.Photino;

// Branded splash shown while Vite is starting up in dev mode. Replaces
// WebView2's "can't reach this page" chrome-error UI with something that
// looks like the app. Theme tracks `prefers-color-scheme` so we don't have
// to read settings.json synchronously.
//
// The splash polls Vite itself and navigates when it's ready. We deliberately
// avoid C# → JS SendWebMessage during startup — calling it before WebView2's
// inner IPC channel is fully wired up produced an access-violation crash in
// Photino's native layer.
internal static class Splash
{
    public const string Html = """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8" />
        <style>
          html, body {
            margin: 0; padding: 0; height: 100%;
            -webkit-app-region: drag;
            user-select: none;
            font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
            background: #ffffff;
            color: #0a0a0a;
          }
          @media (prefers-color-scheme: dark) {
            html, body {
              background: #0a0a0a;
              color: #fafafa;
            }
          }
          .splash {
            height: 100%;
            display: flex;
            flex-direction: column;
            align-items: center;
            justify-content: center;
            gap: 0.5rem;
          }
          .splash h1 { font-size: 1.25rem; font-weight: 600; margin: 0; }
          .splash p  { font-size: 0.75rem; opacity: 0.6; margin: 0; }
          .splash .err { color: #ef4444; }
        </style>
        </head>
        <body>
        <div class="splash">
          <h1>DatumIngest</h1>
          <p id="status">starting…</p>
        </div>
        <script>
          // Self-polling: probe Vite via fetch(no-cors). Connection refused
          // throws; any response (opaque, 4xx, etc.) resolves — meaning the
          // server is up. Top-level navigation has no CORS, so we can just
          // assign location.href once Vite answers.
          var VITE_URL = 'http://localhost:5173/';
          var DEADLINE = Date.now() + 60000;
          function poll() {
            if (Date.now() > DEADLINE) {
              var s = document.getElementById('status');
              s.className = 'err';
              s.textContent = 'Vite did not start within 60s';
              return;
            }
            fetch(VITE_URL, { mode: 'no-cors', cache: 'no-store' })
              .then(function () { window.location.href = VITE_URL; })
              .catch(function () { setTimeout(poll, 250); });
          }
          poll();
        </script>
        </body>
        </html>
        """;
}
