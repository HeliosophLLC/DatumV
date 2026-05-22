namespace Heliosoph.DatumV.Web.Hosting;

// What Program.Main hands to WebHost.Start. The URL is whatever the
// Electron shell passes via DATUMV_WEB_URL — pinned to 5050 in dev so
// Vite's proxy stays static; ephemeral (port 0) in prod so two
// instances can't collide.
public sealed record WebHostBootstrap(string[] Args, string Url, WebHostOptions Options);
