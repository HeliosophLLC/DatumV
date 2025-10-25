using DatumIngest.Catalog;

namespace DatumIngest.Tests;

internal static class TestTableDescriptor
{
    internal static TableDescriptor Default { get; } = new("test", "test");
}
