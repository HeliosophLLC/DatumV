using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.DatumFile.V2.Encoding;

/// <summary>
/// Per-column zone-map hierarchy builder. Accepts page-level
/// <see cref="DatumZoneMap"/>s as the writer flushes pages, aggregates
/// every <see cref="DatumFormatV2.PagesPerChapter"/> of them into a
/// chapter-level zone map, and aggregates every
/// <see cref="DatumFormatV2.ChaptersPerVolume"/> of those into a
/// volume-level zone map. At end-of-write the builder finalizes any
/// partial trailing chapter / volume.
/// </summary>
internal sealed class ZoneMapHierarchyBuilderV2
{
    private readonly int _pagesPerChapter;
    private readonly int _chaptersPerVolume;
    private readonly List<DatumZoneMap> _chapters = new();
    private readonly List<DatumZoneMap> _volumes = new();

    private DataKind _chapterKind = DataKind.Unknown;
    private uint _chapterNullCount;
    private object? _chapterMin;
    private object? _chapterMax;
    private int _pagesInCurrentChapter;

    private DataKind _volumeKind = DataKind.Unknown;
    private uint _volumeNullCount;
    private object? _volumeMin;
    private object? _volumeMax;
    private int _chaptersInCurrentVolume;

    public ZoneMapHierarchyBuilderV2()
        : this(DatumFormatV2.PagesPerChapter, DatumFormatV2.ChaptersPerVolume)
    {
    }

    public ZoneMapHierarchyBuilderV2(int pagesPerChapter, int chaptersPerVolume)
    {
        if (pagesPerChapter <= 0) throw new ArgumentOutOfRangeException(nameof(pagesPerChapter));
        if (chaptersPerVolume <= 0) throw new ArgumentOutOfRangeException(nameof(chaptersPerVolume));
        _pagesPerChapter = pagesPerChapter;
        _chaptersPerVolume = chaptersPerVolume;
    }

    /// <summary>Records a page's zone map; rolls up into chapters/volumes as the page count crosses the boundaries.</summary>
    public void AddPage(DatumZoneMap pageMap)
    {
        MergeIntoChapter(pageMap);
        _pagesInCurrentChapter++;

        if (_pagesInCurrentChapter >= _pagesPerChapter)
        {
            CloseChapter();
        }
    }

    /// <summary>
    /// Flushes any partial trailing chapter / volume and returns the
    /// final lists. <paramref name="emitVolumes"/> controls whether the
    /// volume-level list is returned at all (gated on file row count
    /// per <see cref="DatumFormatV2.VolumeEmitRowThreshold"/>).
    /// </summary>
    public (IReadOnlyList<DatumZoneMap> Chapters, IReadOnlyList<DatumZoneMap>? Volumes) Finalize(bool emitVolumes)
    {
        if (_pagesInCurrentChapter > 0)
        {
            CloseChapter();
        }
        if (emitVolumes && _chaptersInCurrentVolume > 0)
        {
            CloseVolume();
        }

        return (_chapters, emitVolumes ? _volumes : null);
    }

    // ──────────────────── Aggregation primitives ────────────────────

    private void MergeIntoChapter(DatumZoneMap pageMap)
    {
        _chapterNullCount += pageMap.NullCount;
        if (pageMap.Minimum is null || pageMap.Maximum is null) return;

        if (_chapterKind == DataKind.Unknown)
        {
            _chapterKind = pageMap.Kind;
        }
        _chapterMin = MergeMin(_chapterMin, pageMap.Minimum);
        _chapterMax = MergeMax(_chapterMax, pageMap.Maximum);
    }

    private void CloseChapter()
    {
        DatumZoneMap chapter = (_chapterMin is null || _chapterMax is null)
            ? new DatumZoneMap(_chapterNullCount)
            : new DatumZoneMap(_chapterNullCount, _chapterKind, _chapterMin, _chapterMax);

        _chapters.Add(chapter);
        MergeIntoVolume(chapter);
        _chaptersInCurrentVolume++;

        _chapterKind = DataKind.Unknown;
        _chapterNullCount = 0;
        _chapterMin = null;
        _chapterMax = null;
        _pagesInCurrentChapter = 0;

        if (_chaptersInCurrentVolume >= _chaptersPerVolume)
        {
            CloseVolume();
        }
    }

    private void MergeIntoVolume(DatumZoneMap chapter)
    {
        _volumeNullCount += chapter.NullCount;
        if (chapter.Minimum is null || chapter.Maximum is null) return;

        if (_volumeKind == DataKind.Unknown)
        {
            _volumeKind = chapter.Kind;
        }
        _volumeMin = MergeMin(_volumeMin, chapter.Minimum);
        _volumeMax = MergeMax(_volumeMax, chapter.Maximum);
    }

    private void CloseVolume()
    {
        DatumZoneMap volume = (_volumeMin is null || _volumeMax is null)
            ? new DatumZoneMap(_volumeNullCount)
            : new DatumZoneMap(_volumeNullCount, _volumeKind, _volumeMin, _volumeMax);

        _volumes.Add(volume);

        _volumeKind = DataKind.Unknown;
        _volumeNullCount = 0;
        _volumeMin = null;
        _volumeMax = null;
        _chaptersInCurrentVolume = 0;
    }

    private static object MergeMin(object? existing, object candidate)
    {
        if (existing is null) return candidate;
        return ((IComparable)candidate).CompareTo(existing) < 0 ? candidate : existing;
    }

    private static object MergeMax(object? existing, object candidate)
    {
        if (existing is null) return candidate;
        return ((IComparable)candidate).CompareTo(existing) > 0 ? candidate : existing;
    }
}
