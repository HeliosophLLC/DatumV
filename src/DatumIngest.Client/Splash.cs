namespace DatumIngest.Client;

// Branded splash shown while Vite is starting up in dev mode. Replaces
// WebView2's "can't reach this page" chrome-error UI with something that
// looks like the app. Theme tracks `prefers-color-scheme` so we don't have
// to read settings.json synchronously. JS listens for a single IPC kind —
// `splash:navigate:<url>` — and swaps the location when Vite is ready.
internal static class Splash
{
    public const string NavigateKind = "splash:navigate:";

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
          if (window.external && window.external.receiveMessage) {
            window.external.receiveMessage(function (msg) {
              if (typeof msg !== 'string') return;
              if (msg.indexOf('splash:navigate:') === 0) {
                window.location.href = msg.slice('splash:navigate:'.length);
              } else if (msg.indexOf('splash:error:') === 0) {
                var status = document.getElementById('status');
                status.className = 'err';
                status.textContent = msg.slice('splash:error:'.length);
              }
            });
          }
        </script>
        </body>
        </html>
        """;
}
