// Clean-room implementation from Apple TN1150 "HFS Plus Volume Format", section "Catalog File".
// Catalog keys are ordered by parentID (unsigned), then by nodeName using the volume's comparison
// (case-insensitive folding for 'H+' and for HFSX with keyCompareType 0xCF, binary for HFSX with 0xBC).
// A folder's thread record has the key {folderID, ""} and is therefore the first record of that parent,
// immediately followed by the folder's children: listing a directory is one search plus a forward scan.
namespace LuminaMonitor.Formats.Hfs;

internal sealed class Catalog
{
    public const uint RootParentId = 1, RootFolderId = 2, FirstUserCnid = 16;

    private readonly BTreeFile _tree;

    public Catalog(BTreeFile tree, bool volumeIsHfsx)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        // TN1150: an 'H+' catalog is always case-insensitive; only HFSX honours keyCompareType.
        CaseSensitive = volumeIsHfsx && tree.KeyCompareType == BTreeFile.BinaryCompare;
    }

    public bool CaseSensitive { get; }

    /// <summary>Finds the child named <paramref name="name"/> (any Unicode form) of folder <paramref name="parentId"/>.</summary>
    public CatalogEntry? Find(uint parentId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string diskName = HfsName.ToDiskForm(name);
        var cursor = _tree.Search(Comparer(parentId, diskName));
        if (cursor is { Exact: true } hit)
        {
            var node = _tree.ReadNode(hit.Node);
            var entry = CatalogEntry.TryParse(node.Key(hit.Index), node.Data(hit.Index));
            if (entry is not null) return entry;
        }
        // The folding table is approximated for non-ASCII names (see HfsName.cs): fall back to a scan of
        // the directory, whose extent only depends on parentID ordering, so the lookup stays exact.
        if (CaseSensitive || IsAscii(diskName)) return null;
        foreach (var child in List(parentId))
        {
            if (HfsName.CompareFolded(child.Name, diskName) == 0) return child;
        }
        return null;
    }

    /// <summary>Folder and file records whose parent is <paramref name="parentId"/>, in catalog order.</summary>
    public IEnumerable<CatalogEntry> List(uint parentId)
    {
        foreach (var record in _tree.RecordsFromKey(Comparer(parentId, string.Empty)))
        {
            uint p = CatalogKey.KeyParentId(record.Key);
            if (p < parentId) continue;
            if (p > parentId) break;
            var entry = CatalogEntry.TryParse(record.Key, record.Data);
            if (entry is not null) yield return entry;
        }
    }

    /// <summary>Thread record of <paramref name="cnid"/>: its parent folder and its name.</summary>
    public CatalogThread? Thread(uint cnid)
    {
        var cursor = _tree.Search(Comparer(cnid, string.Empty));
        if (cursor is not { Exact: true } hit) return null;
        var node = _tree.ReadNode(hit.Node);
        return CatalogThread.TryParse(node.Data(hit.Index));
    }

    private BTreeKeyComparison Comparer(uint parentId, string diskName) => candidate =>
    {
        if (candidate.Length < CatalogKey.MinLength) throw new InvalidDataException("Catalog key shorter than 6 bytes.");
        uint candidateParent = CatalogKey.ParentId(candidate);
        if (parentId != candidateParent) return parentId < candidateParent ? -1 : 1;
        Span<char> buffer = stackalloc char[CatalogKey.MaxNameUnits];
        int n = CatalogKey.ReadName(candidate, buffer);
        return HfsName.Compare(diskName, buffer[..n], CaseSensitive);
    };

    private static bool IsAscii(string s)
    {
        foreach (char c in s)
        {
            if (c >= 0x80) return false;
        }
        return true;
    }
}
