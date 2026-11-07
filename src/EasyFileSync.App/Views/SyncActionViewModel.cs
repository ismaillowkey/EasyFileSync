using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using EasyFileSync.Core.Models;

namespace EasyFileSync.App.Views;

/// <summary>
/// ViewModel untuk merepresentasikan satu baris perbandingan (Folder atau File)
/// dalam tampilan hierarkis ala Beyond Compare (Dual-Pane Folder to Folder).
/// Mendukung Expand / Reduce (Collapse) folder secara interaktif.
/// </summary>
public class SyncActionViewModel
{
    public SyncAction? Action { get; set; }

    public string DisplayName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public int Depth { get; set; }
    public Thickness IndentMargin => new Thickness(Depth * 18, 0, 0, 0);

    public bool HasLeft { get; set; }
    public bool HasRight { get; set; }
    public bool HasDifference { get; set; }

    /// <summary>
    /// Menandakan apakah folder ini seluruh perbedaannya murni merupakan file orphan.
    /// </summary>
    public bool HasOrphanOnlyDiff { get; set; }

    public long LeftTotalBytes { get; set; }
    public long RightTotalBytes { get; set; }
    public DateTime LeftDateTime { get; set; }
    public DateTime RightDateTime { get; set; }

    // Struktur Pohon Hirarki (Anak & Induk)
    public List<SyncActionViewModel> Children { get; } = new();
    public SyncActionViewModel? Parent { get; set; }

    // Status Expand / Reduce
    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get
        {
            return _isExpanded;
        }
        set
        {
            _isExpanded = value;
        }
    }

    /// <summary>
    /// Icon penanda folder terbuka (▾) atau tertutup (▸).
    /// </summary>
    public string ExpandGlyph
    {
        get
        {
            if (!IsDirectory)
            {
                return "";
            }

            if (IsExpanded)
            {
                return "▾";
            }

            return "▸";
        }
    }

    /// <summary>
    /// Menentukan apakah item adalah Orphan (hanya ada di kiri atau hanya di kanan).
    /// </summary>
    public bool IsOrphan
    {
        get
        {
            if (IsDirectory)
            {
                // Folder dianggap orphan jika fisiknya hanya ada di salah satu sisi
                if (HasLeft && !HasRight)
                {
                    return true;
                }
                if (!HasLeft && HasRight)
                {
                    return true;
                }

                // Atau jika folder ada di kedua sisi tapi seluruh isinya adalah orphan
                if (HasOrphanOnlyDiff)
                {
                    return true;
                }

                return false;
            }

            // File: ada di kiri tapi tidak di kanan, atau sebaliknya
            if (HasLeft && !HasRight)
            {
                return true;
            }
            if (!HasLeft && HasRight)
            {
                return true;
            }

            return false;
        }
    }

    // Properti visibility icon folder
    public bool IsOrphanFolder
    {
        get
        {
            return IsDirectory && IsOrphan;
        }
    }

    public bool IsDiffFolder
    {
        get
        {
            return IsDirectory && !IsOrphan && HasDifference;
        }
    }

    public bool IsSameFolder
    {
        get
        {
            return IsDirectory && !IsOrphan && !HasDifference;
        }
    }

    // Properti visibility icon file
    public bool IsOrphanFile
    {
        get
        {
            return !IsDirectory && IsOrphan;
        }
    }

    public bool IsDiffFile
    {
        get
        {
            return !IsDirectory && !IsOrphan && HasDifference;
        }
    }

    public bool IsSameFile
    {
        get
        {
            return !IsDirectory && !IsOrphan && !HasDifference;
        }
    }

    /// <summary>
    /// Warna teks nama file/folder:
    /// - UNGU (#7B1FA2): Jika orphan (hanya ada di kiri atau hanya di kanan).
    /// - MERAH (#C62828): Jika ada di kedua sisi namun berbeda konten/waktu.
    /// - NORMAL (#222222): Jika identik / sama persis di kedua sisi.
    /// </summary>
    public IBrush NameForeground
    {
        get
        {
            if (IsOrphan)
            {
                return new SolidColorBrush(Color.Parse("#7B1FA2"));
            }

            if (HasDifference)
            {
                return new SolidColorBrush(Color.Parse("#C62828"));
            }

            return new SolidColorBrush(Color.Parse("#222222"));
        }
    }

    public string LeftName
    {
        get
        {
            if (HasLeft)
            {
                return DisplayName;
            }
            return "";
        }
    }

    public string RightName
    {
        get
        {
            if (HasRight)
            {
                return DisplayName;
            }
            return "";
        }
    }

    public string LeftSize
    {
        get
        {
            if (!HasLeft)
            {
                return "";
            }

            long bytes = 0;
            if (IsDirectory)
            {
                bytes = LeftTotalBytes;
            }
            else
            {
                if (Action != null && Action.Left != null)
                {
                    bytes = Action.Left.Size;
                }
            }

            return FormatBeyondCompareSize(bytes);
        }
    }

    public string RightSize
    {
        get
        {
            if (!HasRight)
            {
                return "";
            }

            long bytes = 0;
            if (IsDirectory)
            {
                bytes = RightTotalBytes;
            }
            else
            {
                if (Action != null && Action.Right != null)
                {
                    bytes = Action.Right.Size;
                }
            }

            return FormatBeyondCompareSize(bytes);
        }
    }

    public string LeftDate
    {
        get
        {
            if (!HasLeft)
            {
                return "";
            }

            DateTime dt = DateTime.MinValue;
            if (IsDirectory)
            {
                dt = LeftDateTime;
            }
            else
            {
                if (Action != null && Action.Left != null)
                {
                    dt = Action.Left.ModifiedUtc;
                }
            }

            if (dt == DateTime.MinValue)
            {
                return "";
            }

            return dt.ToLocalTime().ToString("M/d/yyyy h:mm:ss tt");
        }
    }

    public string RightDate
    {
        get
        {
            if (!HasRight)
            {
                return "";
            }

            DateTime dt = DateTime.MinValue;
            if (IsDirectory)
            {
                dt = RightDateTime;
            }
            else
            {
                if (Action != null && Action.Right != null)
                {
                    dt = Action.Right.ModifiedUtc;
                }
            }

            if (dt == DateTime.MinValue)
            {
                return "";
            }

            return dt.ToLocalTime().ToString("M/d/yyyy h:mm:ss tt");
        }
    }

    /// <summary>
    /// Simbol aksi di kolom tengah (Beyond Compare style):
    /// - Saat folder ditutup (reduced/collapsed), menampilkan rangkuman aksi folder (panah emas atau ?).
    /// - Saat folder dibuka (expanded), kosong karena anak-anaknya terlihat di bawahnya.
    /// - Untuk file: Panah emas "➔" atau "⬅" untuk orphan, tanda tanya hitam "?" untuk beda konten.
    /// </summary>
    public string ActionGlyph
    {
        get
        {
            if (IsDirectory)
            {
                // Jika folder sedang dibuka, baris folder dikosongkan agar anak-anaknya yang menampilkan status
                if (IsExpanded)
                {
                    return "";
                }

                // Jika folder sedang ditutup (reduced), tampilkan status rangkuman folder
                if (IsOrphan)
                {
                    if (HasLeft && !HasRight)
                    {
                        return "➔";
                    }

                    if (!HasLeft && HasRight)
                    {
                        return "⬅";
                    }

                    return "➔";
                }

                if (HasDifference)
                {
                    return "?";
                }

                return "";
            }

            // Untuk file
            if (IsOrphan)
            {
                if (HasLeft && !HasRight)
                {
                    return "➔";
                }

                if (!HasLeft && HasRight)
                {
                    return "⬅";
                }

                return "➔";
            }

            if (HasDifference)
            {
                return "?";
            }

            return "";
        }
    }

    public string ActionLabel
    {
        get
        {
            if (Action != null)
            {
                return Action.Label;
            }
            return "";
        }
    }

    public IBrush ActionBrush
    {
        get
        {
            if (IsOrphan)
            {
                return new SolidColorBrush(Color.Parse("#D48800"));
            }

            if (HasDifference)
            {
                return new SolidColorBrush(Color.Parse("#111111"));
            }

            return new SolidColorBrush(Color.Parse("#757575"));
        }
    }

    public SyncActionViewModel()
    {
    }

    public SyncActionViewModel(SyncAction action)
    {
        Action = action;
        DisplayName = action.Name;
        HasLeft = (action.Left != null);
        HasRight = (action.Right != null);

        if (action.Kind != ActionKind.None && action.Kind != ActionKind.Skip)
        {
            HasDifference = true;
        }
        else
        {
            HasDifference = false;
        }

        if (action.Left != null && action.Left.IsDirectory)
        {
            IsDirectory = true;
        }
        else if (action.Right != null && action.Right.IsDirectory)
        {
            IsDirectory = true;
        }

        RelativePath = action.RelativePath;
    }

    /// <summary>
    /// Format ukuran file angka desimal persis Beyond Compare (misal 8,099,833 atau 1.17 GB).
    /// </summary>
    public static string FormatBeyondCompareSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "0";
        }

        if (bytes >= 1_073_741_824)
        {
            double gb = bytes / 1_073_741_824.0;
            return gb.ToString("0.00") + " GB";
        }

        return bytes.ToString("#,##0");
    }

    /// <summary>
    /// Format umum ukuran byte ke KB/MB/GB.
    /// </summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return bytes + " B";
        }

        double kb = bytes / 1024.0;
        if (kb < 1024.0)
        {
            return kb.ToString("0.#") + " KB";
        }

        double mb = kb / 1024.0;
        if (mb < 1024.0)
        {
            return mb.ToString("0.#") + " MB";
        }

        double gb = mb / 1024.0;
        return gb.ToString("0.##") + " GB";
    }

    private class HierarchyNode
    {
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public bool IsDirectory { get; set; }
        public SyncAction? Action { get; set; }
        public Dictionary<string, HierarchyNode> SubNodes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public long LeftTotalSize { get; set; }
        public long RightTotalSize { get; set; }
        public DateTime LeftMaxDate { get; set; }
        public DateTime RightMaxDate { get; set; }
        public bool HasDiff { get; set; }
        public bool HasLeftNode { get; set; }
        public bool HasRightNode { get; set; }
        public bool IsOrphanNode { get; set; }
        public bool IsOrphanOnly { get; set; }
    }

    /// <summary>
    /// Membangun pohon hierarkis folder dengan referensi anak (Children) sehingga dapat di-expand dan di-reduce.
    /// </summary>
    public static List<SyncActionViewModel> BuildTree(IEnumerable<SyncAction> actions)
    {
        var root = new HierarchyNode { Name = "", RelativePath = "", IsDirectory = true };

        foreach (var action in actions)
        {
            string normalized = action.RelativePath.Replace('\\', '/').Trim('/');
            string[] parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0)
            {
                continue;
            }

            HierarchyNode current = root;
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                bool isLast = (i == parts.Length - 1);

                if (!current.SubNodes.TryGetValue(part, out var child))
                {
                    string childRelPath = part;
                    if (current != root && !string.IsNullOrEmpty(current.RelativePath))
                    {
                        childRelPath = current.RelativePath + "/" + part;
                    }

                    child = new HierarchyNode
                    {
                        Name = part,
                        RelativePath = childRelPath,
                        IsDirectory = !isLast
                    };
                    current.SubNodes[part] = child;
                }

                if (isLast)
                {
                    child.Action = action;
                    if ((action.Left != null && action.Left.IsDirectory) || (action.Right != null && action.Right.IsDirectory))
                    {
                        child.IsDirectory = true;
                    }
                }

                current = child;
            }
        }

        // Hitung total ukuran, tanggal, dan klasifikasi orphan pada folder
        void CalculateStats(HierarchyNode node)
        {
            int diffCount = 0;
            int orphanCount = 0;

            foreach (var child in node.SubNodes.Values)
            {
                if (child.IsDirectory)
                {
                    CalculateStats(child);
                    node.LeftTotalSize += child.LeftTotalSize;
                    node.RightTotalSize += child.RightTotalSize;
                    if (child.LeftMaxDate > node.LeftMaxDate) node.LeftMaxDate = child.LeftMaxDate;
                    if (child.RightMaxDate > node.RightMaxDate) node.RightMaxDate = child.RightMaxDate;
                    if (child.HasDiff)
                    {
                        node.HasDiff = true;
                        diffCount++;
                    }
                    if (child.IsOrphanOnly || child.IsOrphanNode)
                    {
                        orphanCount++;
                    }
                    if (child.HasLeftNode) node.HasLeftNode = true;
                    if (child.HasRightNode) node.HasRightNode = true;
                }
                else
                {
                    if (child.Action != null)
                    {
                        if (child.Action.Left != null)
                        {
                            child.LeftTotalSize = child.Action.Left.Size;
                            child.LeftMaxDate = child.Action.Left.ModifiedUtc;
                            child.HasLeftNode = true;
                            node.LeftTotalSize += child.LeftTotalSize;
                            node.HasLeftNode = true;
                            if (child.LeftMaxDate > node.LeftMaxDate) node.LeftMaxDate = child.LeftMaxDate;
                        }

                        if (child.Action.Right != null)
                        {
                            child.RightTotalSize = child.Action.Right.Size;
                            child.RightMaxDate = child.Action.Right.ModifiedUtc;
                            child.HasRightNode = true;
                            node.RightTotalSize += child.RightTotalSize;
                            node.HasRightNode = true;
                            if (child.RightMaxDate > node.RightMaxDate) node.RightMaxDate = child.RightMaxDate;
                        }

                        bool isOrphan = (child.Action.Left != null && child.Action.Right == null) ||
                                       (child.Action.Left == null && child.Action.Right != null);

                        child.IsOrphanNode = isOrphan;

                        if (child.Action.Kind != ActionKind.None && child.Action.Kind != ActionKind.Skip)
                        {
                            child.HasDiff = true;
                            node.HasDiff = true;
                            diffCount++;
                            if (isOrphan)
                            {
                                orphanCount++;
                            }
                        }
                    }
                }
            }

            // Jika ada perbedaan dan seluruh perbedaannya adalah orphan, tandai folder sebagai orphan-only
            if (diffCount > 0 && diffCount == orphanCount)
            {
                node.IsOrphanOnly = true;
            }
        }

        CalculateStats(root);

        // Bentuk pohon objek SyncActionViewModel dengan relasi Children
        var roots = new List<SyncActionViewModel>();

        SyncActionViewModel CreateViewModel(HierarchyNode node, int depth, SyncActionViewModel? parent)
        {
            SyncActionViewModel vm;
            if (node.IsDirectory)
            {
                vm = new SyncActionViewModel
                {
                    DisplayName = node.Name,
                    RelativePath = node.RelativePath,
                    IsDirectory = true,
                    Depth = depth,
                    Parent = parent,
                    HasLeft = node.HasLeftNode,
                    HasRight = node.HasRightNode,
                    LeftTotalBytes = node.LeftTotalSize,
                    RightTotalBytes = node.RightTotalSize,
                    LeftDateTime = node.LeftMaxDate,
                    RightDateTime = node.RightMaxDate,
                    HasDifference = node.HasDiff,
                    HasOrphanOnlyDiff = node.IsOrphanOnly,
                    IsExpanded = true
                };

                var sortedChildren = node.SubNodes.Values
                    .OrderByDescending(c => c.IsDirectory)
                    .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

                foreach (var child in sortedChildren)
                {
                    vm.Children.Add(CreateViewModel(child, depth + 1, vm));
                }
            }
            else
            {
                vm = new SyncActionViewModel(node.Action!)
                {
                    DisplayName = node.Name,
                    RelativePath = node.RelativePath,
                    IsDirectory = false,
                    Depth = depth,
                    Parent = parent,
                    HasLeft = node.HasLeftNode,
                    HasRight = node.HasRightNode,
                    HasDifference = node.HasDiff
                };
            }
            return vm;
        }

        var sortedRoots = root.SubNodes.Values
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var childNode in sortedRoots)
        {
            roots.Add(CreateViewModel(childNode, 0, null));
        }

        return roots;
    }

    /// <summary>
    /// Menghasilkan daftar flat yang hanya memuat item yang terlihat (anak folder yang ditutup / reduced tidak dimasukkan).
    /// Mendukung opsi filter 'showOnlyDifferent' untuk hanya menampilkan item yang memiliki perbedaan atau orphan.
    /// </summary>
    public static List<SyncActionViewModel> FlattenVisible(IEnumerable<SyncActionViewModel> roots, bool showOnlyDifferent = false)
    {
        var result = new List<SyncActionViewModel>();

        void Traverse(SyncActionViewModel item)
        {
            // Jika opsi showOnlyDifferent aktif, lewati item dan folder yang identik/sama
            if (showOnlyDifferent)
            {
                bool isDifferent = item.IsOrphan || item.HasDifference;
                if (!isDifferent)
                {
                    return;
                }
            }

            result.Add(item);

            if (item.IsDirectory && item.IsExpanded)
            {
                foreach (var child in item.Children)
                {
                    Traverse(child);
                }
            }
        }

        foreach (var root in roots)
        {
            Traverse(root);
        }

        return result;
    }

    public static List<SyncActionViewModel> BuildHierarchy(IEnumerable<SyncAction> actions)
    {
        var roots = BuildTree(actions);
        return FlattenVisible(roots);
    }
}
