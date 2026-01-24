using DatumIngest.Web.Hosting;

namespace DatumIngest.Web.Compute;

// Resolves the ICatalogService for a given compute node. Today the only
// node is "local" and the only implementation throws — we register the seam
// so DI graphs are shaped right, but actual catalog operations aren't wired
// to TableCatalog yet. That binding lands in the next round.
public interface ICatalogServiceFactory
{
    ICatalogService ForNode(ComputeNodeRef node);
}

internal sealed class UnboundCatalogServiceFactory : ICatalogServiceFactory
{
    public ICatalogService ForNode(ComputeNodeRef node) =>
        throw new NotImplementedException(
            "ICatalogService is not yet wired to TableCatalog. " +
            "This seam exists so principal/context machinery can be tested. " +
            $"Requested node: {node.Id}.");
}
