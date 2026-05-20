namespace Heliosoph.DatumV.Indexing.BTree.Mutable;

/// <summary>
/// Constants and page-type enum for the mutable B+Tree file format
/// (<c>.datum-pkindex</c>). Distinct from the bulk-loaded B+Tree in
/// the parent namespace because mutable trees use uncompressed leaves,
/// dual-slot crash-safe headers, and a free-page list for COW writes.
/// </summary>
internal static class MutableBPlusTreeConstants
{
    /// <summary>Common page header size (PageType + KeyCount + Reserved).</summary>
    internal const int CommonPageHeaderSize = 4;

    /// <summary>
    /// Leaf page header after the common header:
    /// PrevLeaf(4) + NextLeaf(4) + PayloadLength(4) = 12 bytes.
    /// </summary>
    internal const int LeafExtraHeaderSize = 12;

    /// <summary>Total leaf header size including the common header.</summary>
    internal const int LeafHeaderSize = CommonPageHeaderSize + LeafExtraHeaderSize;

    /// <summary>Sentinel page id meaning "no link" (empty root, empty free list, end of leaf chain).</summary>
    internal const uint NoLinkedPage = uint.MaxValue;

    /// <summary>Size of one header slot in bytes.</summary>
    internal const int HeaderSlotSize = 256;

    /// <summary>File offset of header slot A.</summary>
    internal const long HeaderSlotAOffset = 0;

    /// <summary>File offset of header slot B.</summary>
    internal const long HeaderSlotBOffset = HeaderSlotSize;

    /// <summary>Total bytes occupied by both header slots before the first page.</summary>
    internal const long PagesBaseOffset = HeaderSlotSize * 2;

    /// <summary>
    /// File magic ("PKBT" little-endian). Identifies the file as a mutable
    /// B+Tree primary-key index. Distinct from the unified-index magic.
    /// </summary>
    internal const uint FileMagic = 0x54424B50; // 'P' 'K' 'B' 'T'

    /// <summary>Current on-disk format version.</summary>
    internal const uint CurrentVersion = 1;
}

/// <summary>Page-type discriminator stored in the first byte of every page.</summary>
internal enum MutableBPlusTreePageType : byte
{
    /// <summary>Page is on the free list. First 4 bytes after the common header are the next free page id.</summary>
    Free = 0,

    /// <summary>Internal (branch) node containing separator keys + child page ids.</summary>
    Internal = 1,

    /// <summary>Leaf node containing key/value entries (uncompressed) and prev/next leaf links.</summary>
    Leaf = 2,
}
