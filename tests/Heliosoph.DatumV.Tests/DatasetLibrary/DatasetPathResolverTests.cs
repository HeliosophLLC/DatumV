using Heliosoph.DatumV.DatasetLibrary;

namespace Heliosoph.DatumV.Tests.DatasetLibrary;

/// <summary>
/// Tests <see cref="VersionedDatasetPathResolver"/>: the split-root path
/// resolver that separates the expendable raw-archive cache (under
/// <c>$DATUM_DATASETS</c>) from the ingested-datasets root (under
/// <c>&lt;CatalogRootPath&gt;/datasets/</c>).
/// </summary>
public sealed class DatasetPathResolverTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly string _catalogRoot;

    public DatasetPathResolverTests()
    {
        string scratch = Path.Combine(Path.GetTempPath(),
            "Heliosoph.DatumV.DatasetPathResolverTests", Guid.NewGuid().ToString("N"));
        _cacheRoot = Path.Combine(scratch, "cache");
        _catalogRoot = Path.Combine(scratch, "catalog");
        Directory.CreateDirectory(_cacheRoot);
        Directory.CreateDirectory(_catalogRoot);
    }

    public void Dispose()
    {
        try
        {
            string scratch = Path.GetDirectoryName(_cacheRoot)!;
            Directory.Delete(scratch, recursive: true);
        }
        catch (IOException) { /* best-effort */ }
    }

    [Fact]
    public void Resolver_NoVersionPin_FallsBackToVersionlessFolders()
    {
        VersionedDatasetPathResolver resolver = NewResolver();

        Assert.Equal(Path.Combine(_cacheRoot, "coco_test2017"),
            resolver.GetRawCacheRoot("coco_test2017"));
        Assert.Equal(Path.Combine(_catalogRoot, "datasets", "coco_test2017"),
            resolver.GetIngestedRoot("coco_test2017"));
    }

    [Fact]
    public void Resolver_VersionPin_InjectsVersionSegmentUnderBothRoots()
    {
        VersionedDatasetPathResolver resolver = NewResolver();

        Assert.Equal(Path.Combine(_cacheRoot, "coco_test2017", "2017"),
            resolver.GetRawCacheRoot("coco_test2017", versionPin: "2017"));
        Assert.Equal(Path.Combine(_catalogRoot, "datasets", "coco_test2017", "2017"),
            resolver.GetIngestedRoot("coco_test2017", versionPin: "2017"));
    }

    [Fact]
    public void Resolver_RootsAreSurfacedAsProperties()
    {
        VersionedDatasetPathResolver resolver = NewResolver();
        Assert.Equal(_cacheRoot, resolver.DatasetsCacheRoot);
        Assert.Equal(Path.Combine(_catalogRoot, "datasets"), resolver.IngestedDatasetsRoot);
    }

    [Fact]
    public void IsVersionOnDisk_ReturnsFalseWhenIngestedFolderMissing()
    {
        VersionedDatasetPathResolver resolver = NewResolver();
        Assert.False(resolver.IsVersionOnDisk("coco_test2017", "2017"));
    }

    [Fact]
    public void IsVersionOnDisk_ReturnsTrueAfterIngestedFolderCreated()
    {
        VersionedDatasetPathResolver resolver = NewResolver();
        Directory.CreateDirectory(
            Path.Combine(_catalogRoot, "datasets", "coco_test2017", "2017"));
        Assert.True(resolver.IsVersionOnDisk("coco_test2017", "2017"));
    }

    [Fact]
    public void IsVersionOnDisk_OnlyChecksIngestedRoot_NotRawCache()
    {
        // Raw-cache presence is irrelevant — IsVersionOnDisk gates whether
        // the engine should treat `datasets.X` as installed, which is about
        // the .datum files under the ingested root, not the archive.
        VersionedDatasetPathResolver resolver = NewResolver();
        Directory.CreateDirectory(
            Path.Combine(_cacheRoot, "coco_test2017", "2017"));
        Assert.False(resolver.IsVersionOnDisk("coco_test2017", "2017"));
    }

    [Fact]
    public void Resolver_OptionsCtor_DerivesIngestedRootUnderCatalogRoot()
    {
        DatasetLibraryOptions options = new(
            CatalogRootPath: _catalogRoot,
            DatasetsCacheDirectory: _cacheRoot);
        VersionedDatasetPathResolver resolver = new(options);

        Assert.Equal(_cacheRoot, resolver.DatasetsCacheRoot);
        Assert.Equal(Path.Combine(_catalogRoot, "datasets"), resolver.IngestedDatasetsRoot);
    }

    [Fact]
    public void Resolver_RejectsNullArguments()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new VersionedDatasetPathResolver(datasetsCacheRoot: null!, _catalogRoot));
        Assert.Throws<ArgumentNullException>(() =>
            new VersionedDatasetPathResolver(_cacheRoot, ingestedDatasetsRoot: null!));
    }

    [Fact]
    public void Resolver_RejectsEmptyOrNullDatasetId()
    {
        VersionedDatasetPathResolver resolver = NewResolver();
        Assert.Throws<ArgumentException>(() => resolver.GetRawCacheRoot(""));
        Assert.Throws<ArgumentException>(() => resolver.GetIngestedRoot(""));
        Assert.Throws<ArgumentException>(() => resolver.IsVersionOnDisk("", "2017"));
    }

    private VersionedDatasetPathResolver NewResolver()
        => new(_cacheRoot, Path.Combine(_catalogRoot, "datasets"));
}
