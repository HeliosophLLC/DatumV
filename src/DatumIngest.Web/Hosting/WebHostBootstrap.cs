namespace DatumIngest.Web.Hosting;

// What the *host* (Client or standalone Web) hands to WebHost.Start.
// The URL doubles as the standalone-vs-embedded signal:
//   - "http://127.0.0.1:0"   → ephemeral, in-process (Photino's choice)
//   - "http://0.0.0.0:5000"  → fixed, standalone (Web/Program.cs's choice)
public sealed record WebHostBootstrap(string[] Args, string Url, WebHostOptions Options);
