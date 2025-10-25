using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Model;
using DatumIngest.Pooling;

namespace DatumIngest.Tests;

public static class TestTableCatalog
{
    public static TableCatalog Create()
    {
        return new TableCatalog(new Pool(GlobalPool.Backing));
    }

    public static TableCatalog CreateCatalog(params (string Name, Row[] Rows)[] tables)
    {
        TableCatalog catalog = Create();

        foreach ((string name, Row[] rows) in tables)
        {
            catalog.Add(new InMemoryTableProvider(name, rows));
        }

        return catalog;
    }
}