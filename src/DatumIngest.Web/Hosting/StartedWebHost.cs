namespace Heliosoph.DatumV.Web.Hosting;

public sealed record StartedWebHost(WebApplication App, Uri Url) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => new(App.StopAsync());
}
